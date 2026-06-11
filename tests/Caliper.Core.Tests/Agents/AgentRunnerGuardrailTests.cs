// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Protocol;
using Caliper.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Agents;

public sealed class AgentRunnerGuardrailTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static AgentRunner Build(
        ITurnStrategy strategy,
        CaliperOptions? opts = null,
        ISkillStore? skillStore = null,
        IContextManager? context = null,
        ISessionStore? sessions = null,
        IToolRegistry? registry = null,
        IPermissionGate? permissionGate = null,
        IMemoryStore? memoryStore = null,
        ICaliperMdProvider? caliperMdProvider = null,
        ITokenCounter? tokenCounter = null)
    {
        opts ??= new CaliperOptions { Model = "test", MaxSteps = 8, DuplicateCallLimit = 2 };
        var options   = Options.Create(opts);
        sessions    ??= new InMemorySessionStoreStub();
        registry    ??= new EmptyToolRegistry();
        var skills    = skillStore ?? new EmptySkillStore();
        var tokens    = tokenCounter ?? new SimpleTokenCounter();
        context     ??= new PassthroughContextManager();
        var httpFac   = new NullHttpClientFactory();
        var caps      = new StaticCapabilityProvider();
        var runtime = new RuntimeSettings(options, Options.Create(new PermissionsOptions()));
        return new AgentRunner(strategy, registry, skills, context, tokens, sessions, memoryStore ?? new EmptyMemoryStore(), caliperMdProvider ?? new EmptyCaliperMdProvider(), httpFac, caps, permissionGate ?? new AllowAllGate(), runtime, NullLogger<AgentRunner>.Instance);
    }

    private static TurnCompleted Respond(string content) =>
        new(new ModelTurn(content, [], null, new UsageInfo(1, 1, 2)));

    private static TurnCompleted CallTool(string tool, JsonElement arguments, string callId = "call_1") =>
        new(new ModelTurn(null, [new ToolCall(callId, tool, arguments)], null, new UsageInfo(1, 1, 2)));

    private static TurnCompleted LoadSkill(string skill, string callId = "skill_1") =>
        CallTool("load_skill", JsonDocument.Parse($$"""{"name":{{JsonSerializer.Serialize(skill)}}}""").RootElement.Clone(), callId);

    private static async Task<List<AgentEvent>> RunAll(
        AgentRunner runner,
        string sessionId,
        string msg,
        CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(sessionId, msg, ct))
            events.Add(e);
        return events;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Respond_turn_emits_AssistantMessage_and_RunCompleted()
    {
        var strategy = FakeTurnStrategy.Returning(Respond("hello"));

        var runner    = Build(strategy);
        var sessionId = CreateSession();
        var events    = await RunAll(runner, sessionId, "hi");

        Assert.Contains(events, e => e is AssistantMessage { Content: "hello" });
        Assert.Contains(events, e => e is RunCompleted { Reason: CompletionReason.Completed });
    }

    [Fact]
    public async Task MaxSteps_emits_StepLimit_when_model_never_responds()
    {
        var strategy = FakeTurnStrategy.AlwaysReturning(CallTool("nonexistent", JsonDocument.Parse("{}").RootElement.Clone()));

        var runner    = Build(strategy, new CaliperOptions { Model = "x", MaxSteps = 3, DuplicateCallLimit = 10 });
        var sessionId = CreateSession();
        var events    = await RunAll(runner, sessionId, "go");

        Assert.Contains(events, e => e is RunCompleted { Reason: CompletionReason.StepLimit });
    }

    [Fact]
    public async Task DuplicateCallLimit_emits_LoopDetected_on_identical_tool_calls()
    {
        var args = JsonDocument.Parse("""{"query":"same"}""").RootElement.Clone();
        var strategy = FakeTurnStrategy.AlwaysReturning(CallTool("nonexistent", args));

        var runner    = Build(strategy, new CaliperOptions { Model = "x", MaxSteps = 20, DuplicateCallLimit = 2 });
        var sessionId = CreateSession();
        var events    = await RunAll(runner, sessionId, "go");

        Assert.Contains(events, e => e is RunCompleted { Reason: CompletionReason.LoopDetected });
    }

    [Fact]
    public async Task RunFailed_emitted_when_strategy_throws()
    {
        var strategy = new ThrowingTurnStrategy();
        var runner   = Build(strategy);
        var sessionId = CreateSession();
        var events    = await RunAll(runner, sessionId, "fail");

        Assert.Contains(events, e => e is RunFailed);
    }

    [Fact]
    public async Task Repeated_loaded_skill_is_noop_and_can_complete()
    {
        var strategy = CapturingTurnStrategy.Returning(
            LoadSkill("pdf-processing", "skill_1"),
            LoadSkill("pdf-processing", "skill_2"),
            Respond("done"));

        var runner = Build(strategy, skillStore: new SingleSkillStore());
        var events = await RunAll(runner, "test-session", "load twice");

        Assert.Contains(events, e => e is AssistantMessage { Content: "done" });
        Assert.DoesNotContain(events, e => e is RunCompleted { Reason: CompletionReason.StepLimit });
    }

    [Fact]
    public async Task Tool_call_decision_is_visible_in_next_turn_history()
    {
        var args = JsonDocument.Parse("""{"query":"same"}""").RootElement.Clone();
        var strategy = CapturingTurnStrategy.Returning(
            CallTool("nonexistent", args),
            Respond("done"));

        var runner = Build(strategy);
        _ = await RunAll(runner, "test-session", "use a tool");

        Assert.True(strategy.Contexts.Count >= 2);
        Assert.Contains(strategy.Contexts[1].Messages, message => message.Kind == MessageKind.ToolCall);
    }

    [Fact]
    public async Task Denied_tool_call_is_fed_back_and_run_can_continue()
    {
        var tool = new WriteToolStub();
        var args = JsonDocument.Parse("""{"value":"go"}""").RootElement.Clone();
        var strategy = CapturingTurnStrategy.Returning(
            CallTool(tool.Name, args),
            Respond("adapted"));

        var runner = Build(
            strategy,
            registry: new SingleToolRegistry(tool),
            permissionGate: new DenyGate());

        var events = await RunAll(runner, "test-session", "write something");

        Assert.Equal(0, tool.InvocationCount);
        Assert.Contains(events, e => e is ToolFailed { Tool: "write_stub", Error: "Denied by user/policy." });
        Assert.Contains(events, e => e is AssistantMessage { Content: "adapted" });
        Assert.Contains(strategy.Contexts[1].Messages, message => message.Kind == MessageKind.ToolCall);
        Assert.Contains(strategy.Contexts[1].Messages, message => message.Kind == MessageKind.ToolResult);
    }

    [Fact]
    public async Task Mcp_tool_bypasses_caliper_argument_validator()
    {
        var tool = new McpToolWithRequiredArgument();
        var args = JsonDocument.Parse("""{}""").RootElement.Clone();
        var strategy = CapturingTurnStrategy.Returning(
            CallTool(tool.Name, args),
            Respond("done"));

        var runner = Build(
            strategy,
            registry: new SingleToolRegistry(tool));

        var events = await RunAll(runner, "test-session", "call mcp");

        Assert.Equal(1, tool.InvocationCount);
        Assert.Contains(events, e => e is ToolSucceeded { Tool: "mcp_required" });
        Assert.Contains(events, e => e is AssistantMessage { Content: "done" });
    }

    [Fact]
    public async Task Execute_mcp_tool_prompts_in_auto_mode_through_real_permission_gate()
    {
        var prompt = new ScriptedPermissionPrompt(PermissionDecision.Allow);
        var tool = new McpExecuteTool();
        var args = JsonDocument.Parse("""{"target":"value"}""").RootElement.Clone();
        var strategy = CapturingTurnStrategy.Returning(
            CallTool(tool.Name, args),
            Respond("done"));

        var runner = Build(
            strategy,
            registry: new SingleToolRegistry(tool),
            permissionGate: RealPermissionGate(PermissionMode.Auto, prompt));

        var events = await RunAll(runner, "test-session", "call mcp");

        Assert.Equal(1, prompt.Count);
        Assert.Equal(tool.Name, prompt.Requests[0].Tool);
        Assert.Equal(SideEffect.Execute, prompt.Requests[0].Effect);
        Assert.Contains(events, e => e is PermissionRequested { Request.Tool: "mcp_execute" });
        Assert.Contains(events, e => e is PermissionResolved { Tool: "mcp_execute", Decision: PermissionDecision.Allow });
        Assert.Equal(1, tool.InvocationCount);
    }

    [Fact]
    public async Task Outer_cancellation_during_tool_dispatch_completes_cancelled_without_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var tool = new CancellingTool(cancellation);
        var args = JsonDocument.Parse("""{"value":"go"}""").RootElement.Clone();
        var strategy = FakeTurnStrategy.Returning(
            CallTool(tool.Name, args));
        var runner = Build(
            strategy,
            new CaliperOptions
            {
                Model = "test",
                MaxSteps = 4,
                DuplicateCallLimit = 10,
                ToolMaxRetries = 2,
            },
            registry: new SingleToolRegistry(tool));

        var events = await RunAll(runner, "test-session", "cancel", cancellation.Token);

        Assert.Equal(1, tool.InvocationCount);
        Assert.Contains(events, e => e is RunCompleted { Reason: CompletionReason.Cancelled });
        Assert.DoesNotContain(events, e => e is ToolFailed);
    }

    [Fact]
    public async Task Oldest_tool_results_are_evicted_with_placeholder_when_over_budget()
    {
        // End-to-end through the real DropOldestContextManager: seed a history far larger
        // than the budget and confirm the eviction placeholder reaches the model request.
        var store     = new InMemorySessionStoreStub();
        var sessionId = await store.CreateAsync(null, CancellationToken.None);
        for (var i = 0; i < 30; i++)
        {
            await store.AppendAsync(sessionId, new ChatMessage(ChatRole.Assistant, MessageKind.ToolCall, $"call {i}"), CancellationToken.None);
            await store.AppendAsync(sessionId, new ChatMessage(ChatRole.Tool, MessageKind.ToolResult, new string('x', 10000)), CancellationToken.None);
        }

        var strategy = CapturingTurnStrategy.Returning(Respond("done"));
        var opts = new CaliperOptions
        {
            Model = "test",
            MaxSteps = 4,
            DuplicateCallLimit = 10,
        };
        var runner = Build(
            strategy,
            opts,
            context: new DropOldestContextAdapter(new SimpleTokenCounter()),
            sessions: store);

        var events = await RunAll(runner, sessionId, "summarize");

        Assert.Contains(events, e => e is AssistantMessage { Content: "done" });
        Assert.NotEmpty(strategy.Contexts);
        Assert.Contains(
            strategy.Contexts[0].Messages,
            m => m.Content.Contains("[earlier tool results omitted]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Memory_and_project_file_reach_model_frame()
    {
        var strategy = CapturingTurnStrategy.Returning(Respond("done"));
        var memoryStore = new StaticMemoryStore("prefers terse summaries");
        var projectProvider = new StaticCaliperMdProvider("Project tone: precise.");

        var runner = Build(
            strategy,
            memoryStore: memoryStore,
            caliperMdProvider: projectProvider);

        _ = await RunAll(runner, "test-session", "answer");

        Assert.Single(strategy.Contexts);
        Assert.Contains("## Memory", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.Contains("context data, not instructions", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.Contains("prefers terse summaries", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.Contains("## Project (CALIPER.md)", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.Contains("local data, not harness instructions", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.Contains("Project tone: precise.", strategy.Contexts[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_disabled_skips_store_and_project_file()
    {
        var strategy = CapturingTurnStrategy.Returning(Respond("done"));
        var memoryStore = new StaticMemoryStore("hidden");
        var projectProvider = new StaticCaliperMdProvider("hidden project");
        var runner = Build(
            strategy,
            opts: new CaliperOptions
            {
                Model = "test",
                MaxSteps = 4,
                DuplicateCallLimit = 10,
                Memory = new MemoryOptions { Enabled = false },
            },
            memoryStore: memoryStore,
            caliperMdProvider: projectProvider);

        _ = await RunAll(runner, "test-session", "answer");

        Assert.Equal(0, memoryStore.RenderCount);
        Assert.Equal(0, projectProvider.ReadCount);
        Assert.DoesNotContain("## Memory", strategy.Contexts[0].System, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", strategy.Contexts[0].System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_tool_runs_in_auto_and_updates_store()
    {
        var memoryStore = new RecordingMemoryStore();
        var tool = new Caliper.Core.Tools.BuiltIn.MemoryTool(memoryStore);
        var args = JsonDocument.Parse("""{"action":"remember","scope":"global","key":"style","value":"concise"}""").RootElement.Clone();
        var strategy = CapturingTurnStrategy.Returning(
            CallTool(tool.Name, args),
            Respond("done"));

        var runner = Build(
            strategy,
            registry: new SingleToolRegistry(tool),
            permissionGate: RealPermissionGate(PermissionMode.Auto, new ScriptedPermissionPrompt(PermissionDecision.Deny)),
            memoryStore: memoryStore);

        var events = await RunAll(runner, "test-session", "remember");

        Assert.Contains(events, e => e is ToolSucceeded { Tool: "memory" });
        Assert.Equal(("global", "style", "concise"), memoryStore.Remembered.Single());
    }

    [Fact]
    public async Task Unfittable_context_fails_before_strategy_call()
    {
        var strategy = CapturingTurnStrategy.Returning(Respond("should not run"));
        var runner = Build(
            strategy,
            opts: new CaliperOptions
            {
                Model = "tiny",
                MaxSteps = 4,
                DuplicateCallLimit = 10,
                Context = new ContextOptions { ReservedOutputTokens = 10 },
            },
            context: new UnfittableContextManager());

        var events = await RunAll(runner, "test-session", "too much");

        Assert.Empty(strategy.Contexts);
        Assert.Contains(events, e => e is RunFailed failed && failed.Error.Contains("Context window exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoCompact_disabled_does_not_fail_before_strategy_call()
    {
        var strategy = CapturingTurnStrategy.Returning(Respond("sent"));
        var runner = Build(
            strategy,
            opts: new CaliperOptions
            {
                Model = "tiny",
                MaxSteps = 4,
                DuplicateCallLimit = 10,
                Context = new ContextOptions { AutoCompact = false, ReservedOutputTokens = 10 },
            },
            context: new UnfittableContextManager());

        var events = await RunAll(runner, "test-session", "send anyway");

        Assert.Single(strategy.Contexts);
        Assert.Contains(events, e => e is AssistantMessage { Content: "sent" });
    }

    [Fact]
    public async Task Reported_usage_calibrates_token_counter()
    {
        var tokens = new RecordingTokenCounter();
        var strategy = CapturingTurnStrategy.Returning(
            new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(200, 5, 205))));
        var runner = Build(strategy, tokenCounter: tokens);

        _ = await RunAll(runner, "test-session", "calibrate");

        Assert.Single(tokens.Calibrations);
        Assert.Equal(200, tokens.Calibrations[0].Actual);
        Assert.True(tokens.Calibrations[0].Estimated > 0);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private static string CreateSession() => "test-session";

    private static PermissionGate RealPermissionGate(PermissionMode mode, IPermissionPrompt prompt)
    {
        var services = new ServiceCollection()
            .AddSingleton(prompt)
            .BuildServiceProvider();

        return new PermissionGate(
            new RuntimeSettings(
                Options.Create(new CaliperOptions { WorkingRoot = "." }),
                Options.Create(new PermissionsOptions { Mode = mode })),
            services);
    }
}

// ── Test doubles ────────────────────────────────────────────────────────────

file sealed class FakeTurnStrategy(IEnumerable<TurnUpdate[]> sequence) : ITurnStrategy
{
    private readonly Queue<TurnUpdate[]> _q = new(sequence);

    public static FakeTurnStrategy Returning(params TurnUpdate[] updates) =>
        new([[..updates]]);

    public static ITurnStrategy AlwaysReturning(TurnUpdate update) =>
        new AlwaysReturnTurnStrategy(update);

#pragma warning disable CS1998
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var batch = _q.TryDequeue(out var b) ? b : [new TurnCompleted(new ModelTurn("fallback", [], null, new UsageInfo(null, null, null)))];
        foreach (var u in batch)
            yield return u;
    }
#pragma warning restore CS1998
}

file sealed class AlwaysReturnTurnStrategy(TurnUpdate update) : ITurnStrategy
{
#pragma warning disable CS1998
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return update;
    }
#pragma warning restore CS1998
}

file sealed class CapturingTurnStrategy(IEnumerable<TurnUpdate[]> sequence) : ITurnStrategy
{
    private readonly Queue<TurnUpdate[]> _q = new(sequence);
    public List<TurnContext> Contexts { get; } = [];

    public static CapturingTurnStrategy Returning(params TurnUpdate[] updates) =>
        new(updates.Select(update => new[] { update }).ToArray());

#pragma warning disable CS1998
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        Contexts.Add(context);
        var batch = _q.TryDequeue(out var b) ? b : [new TurnCompleted(new ModelTurn("fallback", [], null, new UsageInfo(null, null, null)))];
        foreach (var u in batch)
            yield return u;
    }
#pragma warning restore CS1998
}

file sealed class ThrowingTurnStrategy : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        bool doThrow = true;
        if (doThrow) throw new InvalidOperationException("Intentional test failure.");
        yield break; // unreachable at runtime; required for iterator compilation
    }
}

file sealed class InMemorySessionStoreStub : ISessionStore
{
    private readonly Dictionary<string, List<ChatMessage>> _data = [];

    public Task<string> CreateAsync(string? title, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _data[id] = [];
        return Task.FromResult(id);
    }

    public Task AppendAsync(string id, ChatMessage msg, CancellationToken ct)
    {
        if (!_data.TryGetValue(id, out var list)) { list = []; _data[id] = list; }
        list.Add(msg);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<ChatMessage> msgs = _data.TryGetValue(id, out var list) ? [..list] : [];
        return Task.FromResult(msgs);
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        var prefix = _data.TryGetValue(sessionId, out var existing)
            ? existing.Take(Math.Max(0, fit.ActiveStartIndex)).ToList()
            : [];
        prefix.AddRange(fit.Messages);
        _data[sessionId] = prefix;
        return Task.CompletedTask;
    }
}

file sealed class EmptyToolRegistry : IToolRegistry
{
    public IReadOnlyList<ITool> Enabled => [];
    public ITool? Find(string name) => null;
    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([], skillMenu);

    public string BuildToolMenu() => string.Empty;
}

file sealed class SingleToolRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Enabled => [tool];

    public ITool? Find(string name) =>
        string.Equals(name, tool.Name, StringComparison.Ordinal) ? tool : null;

    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([(tool.Name, tool.ParameterSchema)], skillMenu);

    public string BuildToolMenu() => $"- {tool.Name}: {tool.Description}";
}

file sealed class CancellingTool(CancellationTokenSource outerCancellation) : ITool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "cancel";
    public string Description => "Cancels the outer run.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Execute;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        outerCancellation.Cancel();
        throw new OperationCanceledException(ct);
    }
}

file sealed class WriteToolStub : ITool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "write_stub";
    public string Description => "Writes in tests.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Write;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, "wrote"));
    }
}

file sealed class McpToolWithRequiredArgument : IMcpTool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "mcp_required";
    public string Description => "MCP tool with SDK-owned argument validation.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Execute;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, "ok"));
    }
}

file sealed class McpExecuteTool : IMcpTool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["target"],"properties":{"target":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "mcp_execute";
    public string Description => "MCP tool that requires a prompt in Auto mode.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Execute;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, "ok"));
    }
}

file sealed class ScriptedPermissionPrompt(params PermissionDecision[] decisions) : IPermissionPrompt
{
    private readonly Queue<PermissionDecision> _decisions = new(decisions);
    public int Count { get; private set; }
    public List<PermissionRequest> Requests { get; } = [];

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Count++;
        Requests.Add(request);
        return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : PermissionDecision.Deny);
    }
}

file sealed class EmptySkillStore : ISkillStore
{
    public IReadOnlyList<SkillMetadata> List() => [];

    public Task<string> LoadBodyAsync(string name, CancellationToken ct) =>
        throw new InvalidOperationException($"Unknown skill: {name}");
}

file sealed class EmptyMemoryStore : IMemoryStore
{
    public int RenderCount { get; private set; }

    public Task<string> RenderForPromptAsync(string scope, CancellationToken ct)
    {
        RenderCount++;
        return Task.FromResult(string.Empty);
    }

    public Task RememberAsync(string scope, string key, string value, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task ForgetAsync(string scope, string key, CancellationToken ct) =>
        Task.CompletedTask;
}

file sealed class StaticMemoryStore(string rendered) : IMemoryStore
{
    public int RenderCount { get; private set; }

    public Task<string> RenderForPromptAsync(string scope, CancellationToken ct)
    {
        RenderCount++;
        return Task.FromResult(rendered);
    }

    public Task RememberAsync(string scope, string key, string value, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task ForgetAsync(string scope, string key, CancellationToken ct) =>
        Task.CompletedTask;
}

file sealed class RecordingMemoryStore : IMemoryStore
{
    private readonly List<MemoryEntry> _entries = [];
    public List<(string Scope, string Key, string Value)> Remembered { get; } = [];

    public Task<string> RenderForPromptAsync(string scope, CancellationToken ct) =>
        Task.FromResult(string.Empty);

    public Task RememberAsync(string scope, string key, string value, CancellationToken ct)
    {
        Remembered.Add((scope, key, value));
        _entries.RemoveAll(entry => entry.Scope == scope && entry.Key == key);
        _entries.Add(new MemoryEntry(scope, key, value, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct)
    {
        IReadOnlyList<MemoryEntry> entries = [.. _entries.Where(entry => entry.Scope == scope || entry.Scope == MemoryScope.Global)];
        return Task.FromResult(entries);
    }

    public Task ForgetAsync(string scope, string key, CancellationToken ct)
    {
        _entries.RemoveAll(entry => entry.Scope == scope && entry.Key == key);
        return Task.CompletedTask;
    }
}

file sealed class EmptyCaliperMdProvider : ICaliperMdProvider
{
    public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) =>
        Task.FromResult(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false));
}

file sealed class StaticCaliperMdProvider(string content) : ICaliperMdProvider
{
    public int ReadCount { get; private set; }

    public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct)
    {
        ReadCount++;
        return Task.FromResult(new ProjectMemoryDocument(Path.Combine(workingRoot, "CALIPER.md"), content, Truncated: false));
    }
}

file sealed class SingleSkillStore : ISkillStore
{
    public IReadOnlyList<SkillMetadata> List() =>
    [
        new("pdf-processing", "Extract PDF text."),
    ];

    public Task<string> LoadBodyAsync(string name, CancellationToken ct) =>
        Task.FromResult("# PDF Processing\nInstructions.");
}

file sealed class SimpleTokenCounter : ITokenCounter
{
    public int Count(string text) => Math.Max(1, text.Length / 4);

    public int Count(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message => Count(message.Content) + 4);

    public void Calibrate(int estimated, int actual)
    {
    }
}

file sealed class RecordingTokenCounter : ITokenCounter
{
    public List<(int Estimated, int Actual)> Calibrations { get; } = [];

    public int Count(string text) => Math.Max(1, text.Length / 4);

    public int Count(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message => Count(message.Content) + 4);

    public void Calibrate(int estimated, int actual) =>
        Calibrations.Add((estimated, actual));
}

file sealed class PassthroughContextManager : IContextManager
{
    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct) =>
        Task.FromResult(new ContextFit(
            frame.History,
            Compacted: false,
            BeforeTokens: null,
            AfterTokens: null,
            EstimatedPromptTokens: 1));
}

file sealed class UnfittableContextManager : IContextManager
{
    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct) =>
        Task.FromResult(new ContextFit(
            frame.History,
            Compacted: false,
            BeforeTokens: 1000,
            AfterTokens: 1000,
            EstimatedPromptTokens: budget.ContextWindowTokens + 1));
}

file sealed class DropOldestContextAdapter(ITokenCounter tokens) : IContextManager
{
    private readonly DropOldestContextManager _inner = new(tokens);

    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct)
    {
        var hardLimit = Math.Max(0, budget.ContextWindowTokens - budget.ReservedOutputTokens);
        var fitted = _inner.FitMessages(frame.History, hardLimit, ct);
        return Task.FromResult(new ContextFit(fitted, !fitted.SequenceEqual(frame.History), null, null, 1));
    }
}

file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

file sealed class StaticCapabilityProvider : IModelCapabilityProvider
{
    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(new ModelCapabilities(true, true, true, 32768));
}

file sealed class AllowAllGate : IPermissionGate
{
    public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionDecision.Allow);
}

file sealed class DenyGate : IPermissionGate
{
    public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionDecision.Deny);
}
