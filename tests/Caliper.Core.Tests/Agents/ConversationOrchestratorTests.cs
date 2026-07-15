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

public sealed class ConversationOrchestratorTests
{
    [Fact]
    public async Task Denial_during_run_appears_in_ConversationRunResult_Denials()
    {
        var tool = new WriteToolStub();
        var args = JsonDocument.Parse("""{"value":"go"}""").RootElement.Clone();
        var strategy = new ScriptedTurnStrategy(
            new TurnCompleted(new ModelTurn(null, [new ToolCall("call_1", tool.Name, args)], null, new UsageInfo(1, 1, 2))),
            new TurnCompleted(new ModelTurn("adapted", [], null, new UsageInfo(1, 1, 2))));

        var sessions = new InMemorySessionStore();
        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        var runtime = new RuntimeSettings(
            Options.Create(new CaliperOptions { Model = "test", MaxSteps = 4, DuplicateCallLimit = 4 }),
            Options.Create(new PermissionsOptions()));

        var runner = new AgentRunner(
            strategy,
            new SingleToolRegistry(tool),
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            sessions,
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            new StaticCapabilityProvider(),
            new DenyGate(),
            runtime,
            NullLogger<AgentRunner>.Instance);

        var orchestrator = new ConversationOrchestrator(
            runner,
            sessions,
            new EmptySkillStore(),
            new SingleToolRegistry(tool),
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new PassthroughContextManager(),
            new StaticCapabilityProvider(),
            runtime,
            new InMemoryRunStore(),
            NullLogger<ConversationOrchestrator>.Instance);

        var result = await orchestrator.RunToCompletionAsync(
            new RunSpec(sessionId, "write something"),
            onEvent: null,
            CancellationToken.None);

        var denial = Assert.Single(result.Denials);
        Assert.Equal("write_stub", denial.Tool);
        Assert.Contains("go", denial.Signature, StringComparison.Ordinal);
        Assert.Equal(0, tool.InvocationCount);
        Assert.Equal("adapted", result.AssistantMessage);
    }

    [Fact]
    public async Task No_denial_yields_an_empty_Denials_list()
    {
        var strategy = new ScriptedTurnStrategy(
            new TurnCompleted(new ModelTurn("hello", [], null, new UsageInfo(1, 1, 2))));

        var sessions = new InMemorySessionStore();
        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        var runtime = new RuntimeSettings(
            Options.Create(new CaliperOptions { Model = "test", MaxSteps = 4, DuplicateCallLimit = 4 }),
            Options.Create(new PermissionsOptions()));

        var runner = new AgentRunner(
            strategy,
            new EmptyToolRegistry(),
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            sessions,
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            new StaticCapabilityProvider(),
            new AllowAllGate(),
            runtime,
            NullLogger<AgentRunner>.Instance);

        var orchestrator = new ConversationOrchestrator(
            runner,
            sessions,
            new EmptySkillStore(),
            new EmptyToolRegistry(),
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new PassthroughContextManager(),
            new StaticCapabilityProvider(),
            runtime,
            new InMemoryRunStore(),
            NullLogger<ConversationOrchestrator>.Instance);

        var result = await orchestrator.RunToCompletionAsync(
            new RunSpec(sessionId, "hi"),
            onEvent: null,
            CancellationToken.None);

        Assert.Empty(result.Denials);
        Assert.Equal("hello", result.AssistantMessage);
    }

    [Fact]
    public async Task Unattended_deny_of_a_non_allowlisted_shell_call_appears_in_Denials()
    {
        // End-to-end through the *real*, unmodified PermissionGate (Auto mode, no allowlist match)
        // with UnattendedPermissionPrompt standing in for a human: the gate's Auto-mode fallback
        // calls the prompt, the prompt denies, and — because AgentRunner emits the same
        // PermissionRequested/PermissionResolved pair regardless of which decision path fired —
        // the denial is still correlated into ConversationRunResult.Denials with zero gate changes.
        var tool = new ShellStub();
        var args = JsonSerializer.SerializeToElement(new { command = "some-unlisted-tool --flag" });
        var strategy = new ScriptedTurnStrategy(
            new TurnCompleted(new ModelTurn(null, [new ToolCall("call_1", tool.Name, args)], null, new UsageInfo(1, 1, 2))),
            new TurnCompleted(new ModelTurn("adapted", [], null, new UsageInfo(1, 1, 2))));

        var sessions = new InMemorySessionStore();
        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        var runtime = new RuntimeSettings(
            Options.Create(new CaliperOptions { Model = "test", MaxSteps = 4, DuplicateCallLimit = 4, WorkingRoot = "." }),
            Options.Create(new PermissionsOptions { Mode = PermissionMode.Auto }));

        var promptServices = new ServiceCollection()
            .AddSingleton<IPermissionPrompt>(new UnattendedPermissionPrompt(NullLogger<UnattendedPermissionPrompt>.Instance))
            .BuildServiceProvider();
        var gate = new PermissionGate(runtime, promptServices);

        var runner = new AgentRunner(
            strategy,
            new SingleToolRegistry(tool),
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            sessions,
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            new StaticCapabilityProvider(),
            gate,
            runtime,
            NullLogger<AgentRunner>.Instance);

        var orchestrator = new ConversationOrchestrator(
            runner,
            sessions,
            new EmptySkillStore(),
            new SingleToolRegistry(tool),
            new EmptyMemoryStore(),
            new EmptyCaliperMdProvider(),
            new PassthroughContextManager(),
            new StaticCapabilityProvider(),
            runtime,
            new InMemoryRunStore(),
            NullLogger<ConversationOrchestrator>.Instance);

        var result = await orchestrator.RunToCompletionAsync(
            new RunSpec(sessionId, "run something"),
            onEvent: null,
            CancellationToken.None);

        var denial = Assert.Single(result.Denials);
        Assert.Equal("shell_stub", denial.Tool);
        Assert.Equal(0, tool.InvocationCount);
        Assert.Equal("adapted", result.AssistantMessage);
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

file sealed class ShellStub : ITool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "shell_stub";
    public string Description => "Runs a shell command in tests.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Execute;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, "ran"));
    }
}

file sealed class SingleToolRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Enabled => [tool];

    public IReadOnlyList<ITool> All => [tool];

    public ITool? Find(string name) =>
        string.Equals(name, tool.Name, StringComparison.Ordinal) ? tool : null;

    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([(tool.Name, tool.ParameterSchema)], skillMenu);

    public string BuildToolMenu() => $"- {tool.Name}: {tool.Description}";
}

file sealed class EmptyToolRegistry : IToolRegistry
{
    public IReadOnlyList<ITool> Enabled => [];
    public IReadOnlyList<ITool> All => [];
    public ITool? Find(string name) => null;
    public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> AsAIFunctions() => [];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([], skillMenu);

    public string BuildToolMenu() => string.Empty;
}

file sealed class InMemorySessionStore : ISessionStore
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
        IReadOnlyList<ChatMessage> msgs = _data.TryGetValue(id, out var list) ? [.. list] : [];
        return Task.FromResult(msgs);
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _data.Remove(sessionId);
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

file sealed class DenyGate : IPermissionGate
{
    public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionDecision.Deny);
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
