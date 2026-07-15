// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
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
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

/// <summary>
/// Roadmap §3.1 subagent tests. These build the same recursive object graph
/// <c>ServiceCollectionExtensions.AddCaliperCore</c> wires in production (see <c>Build</c> below) so
/// the DI-cycle break documented on <c>SubagentTool</c> is exercised for real, not mocked away: a
/// child run goes through the same <see cref="AgentRunner"/>/<see cref="ConversationOrchestrator"/>
/// instances as the parent, resolved lazily via <see cref="IServiceProvider"/>.
/// </summary>
public sealed class SubagentToolTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static (AgentRunner Runner, SubagentTool Tool, ISessionStore Sessions, IRuntimeSettings Runtime) Build(
        ITurnStrategy strategy,
        CaliperOptions? opts = null,
        PermissionsOptions? permissions = null,
        IReadOnlyList<ITool>? extraTools = null,
        Func<IRuntimeSettings, IPermissionGate>? buildGate = null,
        ISessionStore? sessions = null)
    {
        opts ??= new CaliperOptions { Model = "test", MaxSteps = 8, DuplicateCallLimit = 10 };
        permissions ??= new PermissionsOptions();
        var runtime = new RuntimeSettings(Options.Create(opts), Options.Create(permissions));
        sessions ??= new InMemorySessionStore();
        buildGate ??= _ => new AllowAllGate();

        var services = new ServiceCollection();
        services.AddSingleton<ISessionStore>(sessions);
        services.AddSingleton<IRuntimeSettings>(runtime);
        services.AddSingleton(buildGate(runtime));
        services.AddSingleton<ITurnStrategy>(strategy);
        services.AddSingleton<ISkillStore>(new EmptySkillStore());
        services.AddSingleton<IContextManager>(new PassthroughContextManager());
        services.AddSingleton<ITokenCounter>(new SimpleTokenCounter());
        services.AddSingleton<IMemoryStore>(new EmptyMemoryStore());
        services.AddSingleton<ICaliperMdProvider>(new EmptyCaliperMdProvider());
        services.AddSingleton<IHttpClientFactory>(new NullHttpClientFactory());
        services.AddSingleton<IModelCapabilityProvider>(new StaticCapabilityProvider());

        // Mirrors ServiceCollectionExtensions.AddCaliperCore's own DI-cycle break: SubagentTool only
        // needs IServiceProvider at construction (resolved lazily inside InvokeAsync), so it can be
        // built before IToolRegistry/AgentRunner/IConversationOrchestrator exist.
        services.AddSingleton(sp => new SubagentTool(
            sp,
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IRuntimeSettings>(),
            NullLogger<SubagentTool>.Instance));

        services.AddSingleton<IToolRegistry>(sp => new MultiToolRegistry([
            .. extraTools ?? [],
            sp.GetRequiredService<SubagentTool>(),
        ]));

        services.AddSingleton(sp => new AgentRunner(
            sp.GetRequiredService<ITurnStrategy>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ISkillStore>(),
            sp.GetRequiredService<IContextManager>(),
            sp.GetRequiredService<ITokenCounter>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<ICaliperMdProvider>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IModelCapabilityProvider>(),
            sp.GetRequiredService<IPermissionGate>(),
            sp.GetRequiredService<IRuntimeSettings>(),
            NullLogger<AgentRunner>.Instance));

        services.AddSingleton<IConversationOrchestrator>(sp => new ConversationOrchestrator(
            sp.GetRequiredService<AgentRunner>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<ISkillStore>(),
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IMemoryStore>(),
            sp.GetRequiredService<ICaliperMdProvider>(),
            sp.GetRequiredService<IContextManager>(),
            sp.GetRequiredService<IModelCapabilityProvider>(),
            sp.GetRequiredService<IRuntimeSettings>(),
            new InMemoryRunStore(),
            NullLogger<ConversationOrchestrator>.Instance));

        var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<AgentRunner>();
        var tool = provider.GetRequiredService<SubagentTool>();
        return (runner, tool, sessions, runtime);
    }

    private static ToolContext BuildToolContext(
        string sessionId = "parent-session",
        string callId = "call_task",
        int subagentDepth = 0,
        SubagentRunState? subagentState = null,
        PermissionsOptions? permissionsOverlay = null,
        CancellationToken ct = default) =>
        new(
            new NullHttpClientFactory(),
            NullLogger.Instance,
            "~/.caliper/skills",
            ".",
            allowOutsideWorkingRoot: false,
            ct,
            sessionId,
            callId,
            subagentDepth,
            subagentState,
            permissionsOverlay);

    private static JsonElement Args(string prompt, string? profile = null, string? title = null)
    {
        var obj = new Dictionary<string, string?> { ["prompt"] = prompt };
        if (profile is not null)
            obj["profile"] = profile;
        if (title is not null)
            obj["title"] = title;
        return JsonSerializer.SerializeToElement(obj);
    }

    private static TurnCompleted Respond(string content) =>
        new(new ModelTurn(content, [], null, new UsageInfo(1, 1, 2)));

    private static TurnCompleted CallTool(string tool, JsonElement arguments, string callId = "call_1") =>
        new(new ModelTurn(null, [new ToolCall(callId, tool, arguments)], null, new UsageInfo(1, 1, 2)));

    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    // ── Parent spawns child, receives folded summary ──────────────────────

    [Fact]
    public async Task Parent_spawns_child_via_task_tool_and_receives_a_folded_summary_in_the_ToolResult()
    {
        var opts = new CaliperOptions { Model = "test", MaxSteps = 8, DuplicateCallLimit = 10 };
        var taskArgs = Args("investigate the bug", profile: "research");
        var strategy = new ScriptedTurnStrategy(
            CallTool("task", taskArgs, "call_task"),      // parent turn 1: spawn a subagent
            Respond("child final message"),               // child turn 1: finish immediately
            Respond("parent final message"));              // parent turn 2: react to the folded summary

        var (runner, _, sessions, _) = Build(strategy, opts);
        var parentId = await sessions.CreateAsync(null, CancellationToken.None);

        var events = await Collect(runner.RunAsync(new RunSpec(parentId, "please investigate"), CancellationToken.None));

        var succeeded = Assert.Single(events.OfType<ToolSucceeded>(), e => e.Tool == "task");
        Assert.Contains("child final message", succeeded.Output, StringComparison.Ordinal);
        Assert.Contains("[subagent stats]", succeeded.Output, StringComparison.Ordinal);
        Assert.Contains("denials: 0", succeeded.Output, StringComparison.Ordinal);

        var started = Assert.Single(events.OfType<SubagentStarted>());
        Assert.StartsWith("Subagent:", started.Title, StringComparison.Ordinal);
        var completed = Assert.Single(events.OfType<SubagentCompleted>());
        Assert.Equal(CompletionReason.Completed, completed.Reason);
        Assert.Equal(started.ChildSessionId, completed.ChildSessionId);

        Assert.Contains(events, e => e is AssistantMessage { Content: "parent final message" });

        var allSessions = await sessions.ListAsync(CancellationToken.None);
        var child = Assert.Single(allSessions, s => s.Id == started.ChildSessionId);
        Assert.Equal(parentId, child.ParentSessionId);
        Assert.StartsWith("Subagent:", child.Title, StringComparison.Ordinal);
    }

    // ── Guards ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Depth_guard_at_MaxDepth_returns_a_failed_ToolResult_without_creating_a_child_session()
    {
        var opts = new CaliperOptions { Model = "test" };
        opts.Subagents.MaxDepth = 2;
        var (_, tool, sessions, _) = Build(new ScriptedTurnStrategy(), opts);

        var ctx = BuildToolContext(subagentDepth: 2);
        var result = await tool.InvokeAsync(Args("go deeper"), ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("maximum nesting depth", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await sessions.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Child_count_guard_at_MaxChildrenPerRun_returns_a_failed_ToolResult()
    {
        var opts = new CaliperOptions { Model = "test" };
        opts.Subagents.MaxChildrenPerRun = 2;
        var (_, tool, sessions, _) = Build(new ScriptedTurnStrategy(), opts);

        var state = new SubagentRunState();
        state.IncrementAndGetChildCount();
        state.IncrementAndGetChildCount(); // already spawned the configured maximum

        var ctx = BuildToolContext(subagentState: state);
        var result = await tool.InvokeAsync(Args("one more"), ctx, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("MaxChildrenPerRun", result.Output, StringComparison.Ordinal);
        Assert.Empty(await sessions.ListAsync(CancellationToken.None));
    }

    // A2: a spawn attempt that fails before the child run starts (session creation throws) must
    // hand its reserved slot back — otherwise store hiccups permanently shrink the run's budget.
    [Fact]
    public async Task Failed_session_creation_does_not_consume_a_child_slot()
    {
        var opts = new CaliperOptions { Model = "test", MaxSteps = 8, DuplicateCallLimit = 10 };
        opts.Subagents.MaxChildrenPerRun = 1;
        var store = new ThrowOnceSessionStore();
        var (_, tool, _, _) = Build(new ScriptedTurnStrategy(Respond("child done")), opts, sessions: store);

        var state = new SubagentRunState();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tool.InvokeAsync(Args("first attempt"), BuildToolContext(subagentState: state), CancellationToken.None));

        // The failed attempt released its slot, so the retry still fits within MaxChildrenPerRun = 1.
        var retry = await tool.InvokeAsync(Args("second attempt"), BuildToolContext(subagentState: state), CancellationToken.None);
        Assert.True(retry.Success);

        // ...and the limit still holds once a child has genuinely run.
        var third = await tool.InvokeAsync(Args("third attempt"), BuildToolContext(subagentState: state), CancellationToken.None);
        Assert.False(third.Success);
        Assert.Contains("MaxChildrenPerRun", third.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_profile_returns_a_failed_ToolResult_listing_valid_profile_names()
    {
        var (_, tool, sessions, _) = Build(new ScriptedTurnStrategy());

        var result = await tool.InvokeAsync(Args("go", profile: "nonexistent"), BuildToolContext(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown subagent profile", result.Output, StringComparison.Ordinal);
        Assert.Contains("research", result.Output, StringComparison.Ordinal);
        Assert.Contains("worker", result.Output, StringComparison.Ordinal);
        Assert.Empty(await sessions.ListAsync(CancellationToken.None));
    }

    // ── Profile tool restriction ───────────────────────────────────────────

    [Fact]
    public async Task Child_calling_a_tool_outside_its_profile_never_invokes_it()
    {
        var blocked = new CountingTool("blocked_tool");
        var opts = new CaliperOptions { Model = "test", MaxSteps = 6, DuplicateCallLimit = 10 };
        opts.Subagents.Profiles["research"].EnabledTools = ["read_file"]; // does not include "blocked_tool"

        var strategy = new ScriptedTurnStrategy(
            CallTool(blocked.Name, JsonDocument.Parse("{}").RootElement.Clone()),
            Respond("done despite restriction"));

        var (_, tool, _, _) = Build(strategy, opts, extraTools: [blocked]);
        var result = await tool.InvokeAsync(Args("try the blocked tool", profile: "research"), BuildToolContext(), CancellationToken.None);

        Assert.Equal(0, blocked.InvocationCount);
        Assert.True(result.Success);
        Assert.Contains("done despite restriction", result.Output, StringComparison.Ordinal);
    }

    // ── Permission non-escalation ───────────────────────────────────────────

    [Fact]
    public void BuildChildOverlay_profile_mode_can_only_tighten_never_loosen()
    {
        var parentOverlay = new PermissionsOptions { Mode = PermissionMode.Plan };
        var global = new PermissionsOptions { Mode = PermissionMode.Auto };

        var tightened = SubagentTool.BuildChildOverlay(parentOverlay, global, PermissionMode.Auto);
        Assert.Equal(PermissionMode.Plan, tightened.Mode);

        var tightenedFromGlobal = SubagentTool.BuildChildOverlay(null, global, PermissionMode.Plan);
        Assert.Equal(PermissionMode.Plan, tightenedFromGlobal.Mode);

        var unchanged = SubagentTool.BuildChildOverlay(parentOverlay, global, profileMode: null);
        Assert.Equal(PermissionMode.Plan, unchanged.Mode);
    }

    [Fact]
    public async Task Child_overlay_stays_at_the_parents_effective_mode_even_when_the_profile_requests_Auto()
    {
        var writeStub = new CountingTool("write_stub", SideEffect.Write);
        var opts = new CaliperOptions { Model = "test", MaxSteps = 6, DuplicateCallLimit = 10, WorkingRoot = "." };
        opts.Subagents.Profiles["worker"].EnabledTools = ["write_stub"];
        opts.Subagents.Profiles["worker"].Mode = PermissionMode.Auto; // profile tries to loosen

        var strategy = new ScriptedTurnStrategy(
            CallTool(writeStub.Name, JsonDocument.Parse("""{"value":"go"}""").RootElement.Clone()),
            Respond("finished"));

        var emptyServices = new ServiceCollection().BuildServiceProvider();
        var (_, tool, _, _) = Build(
            strategy,
            opts,
            permissions: new PermissionsOptions { Mode = PermissionMode.Auto },
            extraTools: [writeStub],
            buildGate: rt => new PermissionGate(rt, emptyServices));

        // Simulates the parent's own actual effective mode being Plan (e.g. the human put the whole
        // session in Plan) — the profile's Mode = Auto must not be able to escalate past it.
        var ctx = BuildToolContext(permissionsOverlay: new PermissionsOptions { Mode = PermissionMode.Plan });
        var result = await tool.InvokeAsync(Args("write something", profile: "worker"), ctx, CancellationToken.None);

        Assert.Equal(0, writeStub.InvocationCount);
        Assert.True(result.Success);
        Assert.Contains("denials: 1", result.Output, StringComparison.Ordinal);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_the_incoming_token_terminates_the_child_run_instead_of_continuing()
    {
        using var parentCts = new CancellationTokenSource();
        var cancelTool = new CancellingTool(parentCts);
        var opts = new CaliperOptions { Model = "test", MaxSteps = 6, DuplicateCallLimit = 10 };
        opts.Subagents.Profiles["worker"].EnabledTools = ["cancel"];

        var strategy = new ScriptedTurnStrategy(
            CallTool(cancelTool.Name, JsonDocument.Parse("{}").RootElement.Clone()));

        var (_, tool, _, _) = Build(strategy, opts, extraTools: [cancelTool]);
        var ctx = BuildToolContext();

        var result = await tool.InvokeAsync(Args("stall then cancel", profile: "worker"), ctx, parentCts.Token);

        Assert.Equal(1, cancelTool.InvocationCount);
        Assert.False(result.Success);
        Assert.Contains("Cancelled", result.Output, StringComparison.Ordinal);
    }

    // ── Timeout ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Child_timeout_produces_a_failed_ToolResult_instead_of_hanging()
    {
        var stallTool = new StallTool();
        var opts = new CaliperOptions { Model = "test", MaxSteps = 6, DuplicateCallLimit = 10, ToolTimeoutSeconds = 30 };
        opts.Subagents.TimeoutSeconds = 1; // SubagentTool clamps to >= 1s anyway
        opts.Subagents.Profiles["worker"].EnabledTools = ["stall"];

        var strategy = new ScriptedTurnStrategy(
            CallTool(stallTool.Name, JsonDocument.Parse("{}").RootElement.Clone()));

        var (_, tool, _, _) = Build(strategy, opts, extraTools: [stallTool]);
        var ctx = BuildToolContext();

        var stopwatch = Stopwatch.StartNew();
        var result = await tool.InvokeAsync(Args("stall", profile: "worker"), ctx, CancellationToken.None);
        stopwatch.Stop();

        Assert.False(result.Success);
        Assert.Contains("timed out after 1s", result.Output, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected the 1s Subagents.TimeoutSeconds to fire promptly, took {stopwatch.Elapsed}.");
    }

    // ── ToolTimeoutOverride ──────────────────────────────────────────────────

    [Fact]
    public void ToolTimeoutOverride_reflects_the_live_Subagents_TimeoutSeconds()
    {
        var opts = new CaliperOptions { Model = "test" };
        opts.Subagents.TimeoutSeconds = 45;
        var (_, tool, _, _) = Build(new ScriptedTurnStrategy(), opts);

        Assert.Equal(TimeSpan.FromSeconds(45), tool.ToolTimeoutOverride);
    }

    [Fact]
    public void ToolTimeoutOverride_floors_at_one_second()
    {
        var opts = new CaliperOptions { Model = "test" };
        opts.Subagents.TimeoutSeconds = 0;
        var (_, tool, _, _) = Build(new ScriptedTurnStrategy(), opts);

        Assert.Equal(TimeSpan.FromSeconds(1), tool.ToolTimeoutOverride);
    }
}

// ── Test doubles ────────────────────────────────────────────────────────────

file sealed class ScriptedTurnStrategy(params TurnUpdate[] updates) : ITurnStrategy
{
    private readonly Queue<TurnUpdate> _queue = new(updates);

#pragma warning disable CS1998
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_queue.TryDequeue(out var update))
            yield return update;
    }
#pragma warning restore CS1998
}

file sealed class CountingTool(string name, SideEffect sideEffect = SideEffect.Execute) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{"value":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => name;
    public string Description => "Counts invocations in tests.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => sideEffect;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, "ok"));
    }
}

file sealed class CancellingTool(CancellationTokenSource outerCancellation) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "cancel";
    public string Description => "Cancels the token supplied to the enclosing subagent run.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Execute;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        outerCancellation.Cancel();
        throw new OperationCanceledException(ct);
    }
}

file sealed class StallTool : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "stall";
    public string Description => "Never returns on its own; only stops when its token is cancelled.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Execute;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return new ToolResult(true, "unreachable");
    }
}

file sealed class MultiToolRegistry(IReadOnlyList<ITool> tools) : IToolRegistry
{
    public IReadOnlyList<ITool> Enabled => tools;
    public IReadOnlyList<ITool> All => tools;

    public ITool? Find(string name) =>
        tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([.. tools.Select(t => (t.Name, t.ParameterSchema))], skillMenu);

    public string BuildToolMenu() => string.Join('\n', tools.Select(t => $"- {t.Name}: {t.Description}"));
}

file sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, List<ChatMessage>> _data = [];
    private readonly Dictionary<string, (string? Title, string? ParentSessionId, DateTimeOffset CreatedAt)> _meta = [];
    private int _counter;

    public Task<string> CreateAsync(string? title, CancellationToken ct) =>
        CreateAsync(title, parentSessionId: null, ct);

    public Task<string> CreateAsync(string? title, string? parentSessionId, CancellationToken ct)
    {
        var id = $"session-{++_counter}";
        _data[id] = [];
        _meta[id] = (title, parentSessionId, DateTimeOffset.UtcNow);
        return Task.FromResult(id);
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
    {
        if (!_data.TryGetValue(sessionId, out var list))
        {
            list = [];
            _data[sessionId] = list;
        }

        list.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct)
    {
        IReadOnlyList<ChatMessage> messages = _data.TryGetValue(sessionId, out var list) ? [.. list] : [];
        return Task.FromResult(messages);
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>(
            [.. _meta.Select(kvp => new SessionSummary(kvp.Key, kvp.Value.Title, kvp.Value.CreatedAt, kvp.Value.ParentSessionId))]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _data.Remove(sessionId);
        _meta.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

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

/// <summary>Throws on the first session create only (A2: simulate a transient store error), then delegates.</summary>
file sealed class ThrowOnceSessionStore : ISessionStore
{
    private readonly InMemorySessionStore _inner = new();
    private bool _shouldThrow = true;

    public Task<string> CreateAsync(string? title, CancellationToken ct) =>
        CreateAsync(title, parentSessionId: null, ct);

    public Task<string> CreateAsync(string? title, string? parentSessionId, CancellationToken ct)
    {
        if (_shouldThrow)
        {
            _shouldThrow = false;
            throw new InvalidOperationException("store down");
        }

        return _inner.CreateAsync(title, parentSessionId, ct);
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct) =>
        _inner.AppendAsync(sessionId, message, ct);

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
        _inner.LoadAsync(sessionId, ct);

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        _inner.ListAsync(ct);

    public Task DeleteAsync(string sessionId, CancellationToken ct) =>
        _inner.DeleteAsync(sessionId, ct);

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) =>
        _inner.RenameAsync(sessionId, title, ct);

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) =>
        _inner.ReplaceWithCompactionAsync(sessionId, fit, ct);
}

file sealed class EmptySkillStore : ISkillStore
{
    public IReadOnlyList<SkillMetadata> List() => [];

    public Task<string> LoadBodyAsync(string name, CancellationToken ct) =>
        throw new InvalidOperationException($"Unknown skill: {name}");
}

file sealed class EmptyMemoryStore : IMemoryStore
{
    public Task<string> RenderForPromptAsync(string scope, CancellationToken ct) =>
        Task.FromResult(string.Empty);

    public Task RememberAsync(string scope, string key, string value, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task ForgetAsync(string scope, string key, CancellationToken ct) =>
        Task.CompletedTask;
}

file sealed class EmptyCaliperMdProvider : ICaliperMdProvider
{
    public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) =>
        Task.FromResult(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false));
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

file sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<string, RunRecord> _runs = [];

    public Task<string> StartAsync(string sessionId, string? jobName, int maxSteps, bool unattended, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        _runs[runId] = new RunRecord(runId, sessionId, jobName, RunStatus.Running, null, 0, maxSteps, unattended, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return Task.FromResult(runId);
    }

    public Task UpdateStepAsync(string runId, int stepNumber, CancellationToken ct)
    {
        if (_runs.TryGetValue(runId, out var run))
            _runs[runId] = run with { Step = stepNumber };
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string runId, RunStatus status, string? reason, CancellationToken ct)
    {
        if (_runs.TryGetValue(runId, out var run))
            _runs[runId] = run with { Status = status, Reason = reason };
        return Task.CompletedTask;
    }

    public Task MarkResumedAsync(string runId, int maxSteps, CancellationToken ct)
    {
        if (_runs.TryGetValue(runId, out var run))
            _runs[runId] = run with { Status = RunStatus.Running, Reason = null, MaxSteps = maxSteps };
        return Task.CompletedTask;
    }

    public Task<RunRecord?> GetAsync(string runId, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);

    public Task<IReadOnlyList<RunRecord>> ListRecentAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunRecord>>([.. _runs.Values.Take(limit)]);
}
