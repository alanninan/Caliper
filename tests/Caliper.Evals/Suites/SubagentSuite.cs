// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// Disambiguates against Microsoft.Extensions.AI.ChatMessage, pulled in for AIFunction below.
using ChatMessage = Caliper.Core.Models.ChatMessage;

namespace Caliper.Evals.Suites;

/// <summary>
/// §5.3 coverage gap: a hermetic scenario for the <c>task</c> subagent tool (roadmap §3.1). A
/// scripted parent model issues a <c>task</c> tool call; a scripted child completes; the scenario
/// asserts the folded summary shape (child's final message + the <c>[subagent stats]</c> trailer)
/// and the <c>SubagentStarted</c>/<c>SubagentCompleted</c> event pair.
///
/// This exercises the <b>real</b> <see cref="SubagentTool"/> — not a stand-in mock — the same way
/// <c>SubagentToolTests</c> does in <c>Caliper.Core.Tests</c>: a real, if small, recursive DI graph
/// (<see cref="ConversationOrchestrator"/> → its own <see cref="AgentRunner"/> → the child's own
/// scripted turns), resolved lazily through <see cref="IServiceProvider"/> exactly as
/// <c>SubagentTool</c>'s own doc comment describes for production. Unlike <c>SubagentToolTests</c>
/// (which shares one <see cref="AgentRunner"/>/turn-strategy queue between parent and child, because
/// that suite drives the parent directly), this scenario has to go through
/// <c>EvalHarnessRunner.RunHermeticAsync</c>'s generic per-case pipeline — the outer "parent" runner
/// is built by <c>EvalHarnessRunner.BuildRunner</c> from <see cref="EvalCase.ScriptedTurns"/> before
/// <see cref="EvalCase.ToolRegistryFactory"/> ever runs, so there's no way for this scenario to hand
/// the child the *same* <see cref="AgentRunner"/> instance the parent uses. Instead, the child gets
/// its own fully self-contained runner/orchestrator wired up inside <see cref="BuildTaskToolRegistry"/>,
/// with its own tiny scripted turn queue — which is arguably clearer anyway (the child's script is
/// explicit, not interleaved with the parent's by dequeue order). The <c>Eval*</c> plumbing types
/// (turn strategy, context manager, capability provider, ...) are shared with
/// <c>EvalHarnessRunner.cs</c> (promoted from <c>file</c> to <c>internal</c> visibility for this
/// reuse) rather than duplicated.
/// </summary>
internal static class SubagentSuite
{
    private const string ChildFinalMessage =
        "The nightly build fails because the CI runner ran out of disk space during the restore step.";

    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        new EvalCase(
            Id:            "task-tool-folds-subagent-summary",
            UserMessage:   "Investigate why the nightly build is failing and report back.",
            ScriptedTurns: [
                """
                {"rationale":"This needs focused investigation; delegate it to a research subagent.",
                 "action":"call_tool","tool":"task",
                 "arguments":{"prompt":"Investigate why the nightly build is failing.","profile":"research"}}
                """,
                """{"rationale":"The subagent found the root cause; relay it.","action":"respond","content":"Root cause found via subagent: disk space exhaustion during restore."}"""
            ],
            MockToolResponses: null,
            Assert: AssertFoldedSummary,
            ExpectedTools: ["task"],
            ValidateArgs: args =>
                args.TryGetProperty("prompt", out var prompt)
                && prompt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(prompt.GetString()),
            ToolRegistryFactory: BuildTaskToolRegistry
        ),
    ];

    private static EvalOutcome AssertFoldedSummary(IReadOnlyList<AgentEvent> events)
    {
        var taskSucceeded = events.OfType<ToolSucceeded>().FirstOrDefault(e => e.Tool == "task");
        if (taskSucceeded is null)
            return EvalOutcome.Fail("Expected a successful 'task' tool call (ToolSucceeded).");

        if (!taskSucceeded.Output.Contains(ChildFinalMessage, StringComparison.Ordinal))
            return EvalOutcome.Fail($"Folded summary is missing the child's final message. Output: {taskSucceeded.Output}");

        if (!taskSucceeded.Output.Contains("[subagent stats]", StringComparison.Ordinal))
            return EvalOutcome.Fail($"Folded summary is missing the stats trailer. Output: {taskSucceeded.Output}");

        if (!taskSucceeded.Output.Contains("denials: 0", StringComparison.Ordinal))
            return EvalOutcome.Fail($"Expected zero denials in the stats trailer. Output: {taskSucceeded.Output}");

        var started = events.OfType<SubagentStarted>().FirstOrDefault();
        var completed = events.OfType<SubagentCompleted>().FirstOrDefault();
        if (started is null || completed is null)
            return EvalOutcome.Fail("Expected a SubagentStarted/SubagentCompleted event pair.");

        if (!string.Equals(started.ChildSessionId, completed.ChildSessionId, StringComparison.Ordinal))
            return EvalOutcome.Fail("SubagentStarted and SubagentCompleted must correlate to the same child session id.");

        if (completed.Reason != CompletionReason.Completed)
            return EvalOutcome.Fail($"Expected the child to finish with CompletionReason.Completed, got {completed.Reason}.");

        var parentFinal = events.OfType<AssistantMessage>().LastOrDefault();
        if (parentFinal is null || !parentFinal.Content.Contains("disk space", StringComparison.Ordinal))
            return EvalOutcome.Fail("Expected the parent's final message to relay the subagent's finding.");

        var runCompleted = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
        if (!runCompleted)
            return EvalOutcome.Fail("Expected RunCompleted(Completed) for the parent run.");

        return EvalOutcome.Ok();
    }

    /// <summary>
    /// Builds an <see cref="IToolRegistry"/> exposing exactly one tool: a real <see cref="SubagentTool"/>
    /// wired to its own small recursive DI graph (mirrors <c>SubagentToolTests.Build</c> in
    /// <c>Caliper.Core.Tests</c>, trimmed to what a single "spawn one child that completes
    /// immediately" scenario needs). The child never calls a tool of its own, so its own
    /// <see cref="IToolRegistry"/> is deliberately empty.
    /// </summary>
    private static SingleToolRegistry BuildTaskToolRegistry()
    {
        string[] childScript =
        [
            $$"""{"rationale":"The regression only reproduces under load; the CI logs show ENOSPC.","action":"respond","content":"{{ChildFinalMessage}}"}""",
        ];

        var childOptions = new CaliperOptions { Model = "eval-fake-child", MaxSteps = 8, DuplicateCallLimit = 5 };
        var childRuntime = new RuntimeSettings(
            Options.Create(childOptions),
            Options.Create(new PermissionsOptions { Mode = PermissionMode.Auto }));

        var services = new ServiceCollection();
        services.AddSingleton<IRunStore>(new InMemoryEvalRunStore());
        services.AddSingleton<ISessionStore>(new ChildEvalSessionStore());
        services.AddSingleton<IRuntimeSettings>(childRuntime);
        services.AddSingleton<IPermissionGate>(new EvalPermissionGate());
        services.AddSingleton<ITurnStrategy>(new EnvelopeScriptTurnStrategy(childScript));
        services.AddSingleton<ISkillStore>(new EvalSkillStore());
        services.AddSingleton<ITokenCounter>(new EvalTokenCounter());
        services.AddSingleton<IContextManager>(sp => new EvalContextManager(sp.GetRequiredService<ITokenCounter>()));
        services.AddSingleton<IMemoryStore>(new EvalMemoryStore());
        services.AddSingleton<ICaliperMdProvider>(new EvalCaliperMdProvider());
        services.AddSingleton<IHttpClientFactory>(new NullHttpClientFactory());
        services.AddSingleton<IModelCapabilityProvider>(new EvalCapabilityProvider());
        // The child's own tool registry: empty — this scenario's child completes in one turn
        // without calling any tool of its own.
        services.AddSingleton<IToolRegistry>(EmptyEvalToolRegistry.Instance);

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
            sp.GetRequiredService<IRunStore>(),
            NullLogger<ConversationOrchestrator>.Instance));

        // Roadmap §3.1 DI-cycle break, same as production (ServiceCollectionExtensions.AddCaliperCore)
        // and SubagentToolTests: SubagentTool takes IServiceProvider itself and resolves
        // IConversationOrchestrator lazily inside InvokeAsync, so it can be constructed before the
        // orchestrator/registry exist.
        services.AddSingleton(sp => new SubagentTool(
            sp,
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IRuntimeSettings>(),
            NullLogger<SubagentTool>.Instance));

        var provider = services.BuildServiceProvider();
        return new SingleToolRegistry(provider.GetRequiredService<SubagentTool>());
    }
}

/// <summary>
/// A minimal <see cref="IToolRegistry"/> exposing exactly one real <see cref="ITool"/>. Internal
/// (not file-scoped): <see cref="SubagentSuite.BuildTaskToolRegistry"/> returns this type by name
/// (CA1859 — avoid the extra interface-dispatch indirection), and a file-local type can't appear in
/// a member signature belonging to a non-file-local type.
/// </summary>
internal sealed class SingleToolRegistry(ITool tool) : IToolRegistry
{
    private readonly IReadOnlyList<ITool> _tools = [tool];

    public IReadOnlyList<ITool> Enabled => _tools;
    public IReadOnlyList<ITool> All => _tools;
    public ITool? Find(string name) => string.Equals(name, tool.Name, StringComparison.Ordinal) ? tool : null;
    public IReadOnlyList<AIFunction> AsAIFunctions() => [];
    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
        ProtocolBuilder.BuildSchema([(tool.Name, tool.ParameterSchema)], skillMenu);
    public string BuildToolMenu() => $"- {tool.Name}: {tool.Description}";
}

/// <summary>
/// A dedicated in-memory <see cref="ISessionStore"/> for the child graph <see cref="SubagentTool"/>
/// spawns — separate from the outer "parent" session store <c>EvalHarnessRunner.RunHermeticAsync</c>
/// builds, since <see cref="EvalCase.ToolRegistryFactory"/> has no way to receive that instance (it
/// runs before the parent's <c>AgentRunner</c> is built). This is harmless: <c>ctx.SessionId</c>
/// (the parent's session id) is only ever recorded here as opaque <c>parentSessionId</c> metadata,
/// never dereferenced.
/// </summary>
file sealed class ChildEvalSessionStore : ISessionStore
{
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
    private readonly Dictionary<string, (string? Title, string? ParentSessionId, DateTimeOffset CreatedAt)> _meta = [];
    private int _counter;

    public Task<string> CreateAsync(string? title, CancellationToken ct) =>
        CreateAsync(title, parentSessionId: null, ct);

    public Task<string> CreateAsync(string? title, string? parentSessionId, CancellationToken ct)
    {
        var id = $"child-session-{++_counter}";
        _messages[id] = [];
        _meta[id] = (title, parentSessionId, DateTimeOffset.UtcNow);
        return Task.FromResult(id);
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
    {
        if (!_messages.TryGetValue(sessionId, out var list))
            list = _messages[sessionId] = [];
        list.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct)
    {
        IReadOnlyList<ChatMessage> messages = _messages.TryGetValue(sessionId, out var list) ? [.. list] : [];
        return Task.FromResult(messages);
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>(
            [.. _meta.Select(kvp => new SessionSummary(kvp.Key, kvp.Value.Title, kvp.Value.CreatedAt, kvp.Value.ParentSessionId))]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _messages.Remove(sessionId);
        _meta.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        var prefix = _messages.TryGetValue(sessionId, out var existing)
            ? existing.Take(Math.Max(0, fit.ActiveStartIndex)).ToList()
            : [];
        prefix.AddRange(fit.Messages);
        _messages[sessionId] = prefix;
        return Task.CompletedTask;
    }
}

/// <summary>Minimal in-memory <see cref="IRunStore"/> for the child graph's <see cref="ConversationOrchestrator"/>.</summary>
file sealed class InMemoryEvalRunStore : IRunStore
{
    private readonly Dictionary<string, RunRecord> _runs = [];

    public Task<string> StartAsync(string sessionId, string? jobName, int maxSteps, bool unattended, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("N");
        _runs[runId] = new RunRecord(
            runId, sessionId, jobName, RunStatus.Running, null, 0, maxSteps, unattended,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
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

    public Task<IReadOnlyList<RunRecord>> ListRecentScheduledAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunRecord>>(
            [.. _runs.Values.Where(run => run.JobName is not null).Take(limit)]);
}

file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
