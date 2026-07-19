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
using Caliper.Core.Scheduling;
using Caliper.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Scheduling;

/// <summary>
/// End-to-end job runs through the real <see cref="AgentRunner"/>, real
/// <see cref="PermissionGate"/>, and the <see cref="RoutingPermissionPrompt"/> — the same
/// composition the interactive console uses for <c>/schedule run</c> — proving the roadmap §3.2b
/// contract: job runs take the unattended path (interactive prompt never consulted), the per-job
/// overlay reaches the gate, and <c>RunSpec.WorkingRoot</c> scopes file auto-allow to the job
/// root.
/// </summary>
public sealed class ScheduleJobRunnerTests
{
    [Fact]
    public async Task Non_allowlisted_shell_in_a_job_run_is_denied_and_recorded()
    {
        var tool = new ShellStub();
        var args = JsonSerializer.SerializeToElement(new { command = "some-unlisted-tool --flag" });
        var harness = Harness.Build(
            tool,
            new ScriptedTurnStrategy(
                new TurnCompleted(new ModelTurn(null, [new ToolCall("call_1", tool.Name, args)], null, new UsageInfo(1, 1, 2))),
                new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2)))));
        var job = new ScheduleOptions
        {
            Name = "nightly",
            Cron = "0 6 * * *",
            Prompt = "run it",
            Permissions = new PermissionsOptions
            {
                Mode = PermissionMode.Auto,
                ShellAutoAllowlist = ["approved-tool"],
            },
        };

        var outcome = await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

        Assert.False(outcome.Skipped);
        Assert.Equal(1, outcome.DenialCount);
        Assert.Equal(0, tool.InvocationCount);
        // The interactive side of the routing prompt must never have been consulted: job runs are
        // unattended even inside an interactive host.
        Assert.Equal(0, harness.InteractivePrompt.Count);
        Assert.Equal(CompletionReason.Completed, outcome.Reason);
        Assert.Same(outcome, harness.Runner.GetLastResult("nightly"));
    }

    [Fact]
    public async Task Allowlisted_shell_in_a_job_run_executes_without_denial()
    {
        var tool = new ShellStub();
        var args = JsonSerializer.SerializeToElement(new { command = "approved-tool --flag" });
        var harness = Harness.Build(
            tool,
            new ScriptedTurnStrategy(
                new TurnCompleted(new ModelTurn(null, [new ToolCall("call_1", tool.Name, args)], null, new UsageInfo(1, 1, 2))),
                new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2)))));
        var job = new ScheduleOptions
        {
            Name = "nightly",
            Cron = "0 6 * * *",
            Prompt = "run it",
            Permissions = new PermissionsOptions
            {
                Mode = PermissionMode.Auto,
                ShellAutoAllowlist = ["approved-tool"],
            },
        };

        var outcome = await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

        Assert.Equal(0, outcome.DenialCount);
        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal(0, harness.InteractivePrompt.Count);
    }

    [Fact]
    public async Task Job_working_root_scopes_file_auto_allow_and_threads_into_ToolContext()
    {
        var globalRoot = Path.Combine(Path.GetTempPath(), "caliper-global-" + Guid.NewGuid().ToString("N"));
        var jobRoot = Path.Combine(Path.GetTempPath(), "caliper-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(jobRoot);
        try
        {
            // Turn 1 writes inside the job root (auto-allowed under Auto), turn 2 writes outside
            // it — inside the *global* root, which must NOT count — (denied), turn 3 finishes.
            var tool = new FileWriteStub();
            var inside = JsonSerializer.SerializeToElement(new { path = Path.Combine(jobRoot, "in.txt") });
            var outside = JsonSerializer.SerializeToElement(new { path = Path.Combine(globalRoot, "out.txt") });
            var harness = Harness.Build(
                tool,
                new ScriptedTurnStrategy(
                    new TurnCompleted(new ModelTurn(null, [new ToolCall("call_1", tool.Name, inside)], null, new UsageInfo(1, 1, 2))),
                    new TurnCompleted(new ModelTurn(null, [new ToolCall("call_2", tool.Name, outside)], null, new UsageInfo(1, 1, 2))),
                    new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2)))),
                globalWorkingRoot: globalRoot);
            var job = new ScheduleOptions
            {
                Name = "rooted",
                Cron = "0 6 * * *",
                Prompt = "write files",
                WorkingRoot = jobRoot,
                Permissions = new PermissionsOptions { Mode = PermissionMode.Auto },
            };

            var outcome = await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

            Assert.Equal(1, tool.InvocationCount);
            Assert.Equal(1, outcome.DenialCount);
            // AgentRunner must build the ToolContext against the job root, not the global one.
            var observedRoot = Assert.Single(tool.ObservedWorkingRoots);
            Assert.Equal(Path.GetFullPath(jobRoot), Path.GetFullPath(observedRoot));
        }
        finally
        {
            Directory.Delete(globalRoot, recursive: true);
            Directory.Delete(jobRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Job_session_is_titled_with_the_job_tag()
    {
        var harness = Harness.Build(
            new ShellStub(),
            new ScriptedTurnStrategy(
                new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2)))));
        var job = new ScheduleOptions { Name = "report", Cron = "0 6 * * *", Prompt = "summarize" };

        await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

        Assert.Contains("[job] report", harness.Sessions.Titles);
    }

    [Fact]
    public async Task Overlapping_manual_trigger_is_skipped_not_queued()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness.Build(
            new ShellStub(),
            new BlockingTurnStrategy(release.Task));
        var job = new ScheduleOptions { Name = "slow", Cron = "0 6 * * *", Prompt = "long haul" };

        var first = harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);
        var second = await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

        Assert.True(second.Skipped);
        release.SetResult();
        var firstOutcome = await first;
        Assert.False(firstOutcome.Skipped);
    }

    // ── A1: eviction of removed/renamed jobs ────────────────────────────────

    [Fact]
    public async Task PruneTo_evicts_state_for_jobs_no_longer_configured_and_keeps_current_ones()
    {
        var harness = Harness.Build(
            new ShellStub(),
            new ScriptedTurnStrategy(
                new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2))),
                new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(1, 1, 2)))));
        var removed = new ScheduleOptions { Name = "removed-job", Cron = "0 6 * * *", Prompt = "run" };
        var kept = new ScheduleOptions { Name = "kept-job", Cron = "0 7 * * *", Prompt = "run" };
        await harness.Runner.RunJobAsync(removed, concurrencyGate: null, onEvent: null, CancellationToken.None);
        await harness.Runner.RunJobAsync(kept, concurrencyGate: null, onEvent: null, CancellationToken.None);

        harness.Runner.PruneTo(["kept-job"]);

        Assert.Null(harness.Runner.GetLastResult("removed-job"));
        Assert.NotNull(harness.Runner.GetLastResult("kept-job"));
    }

    [Fact]
    public async Task PruneTo_skips_a_running_removed_job_then_evicts_it_once_idle()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = Harness.Build(new ShellStub(), new BlockingTurnStrategy(release.Task));
        var job = new ScheduleOptions { Name = "slow", Cron = "0 6 * * *", Prompt = "long haul" };
        var first = harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);

        // The job was removed from config while an occurrence is still running: prune must leave
        // its held lock alone — the overlap guard still applies to that in-flight occurrence.
        harness.Runner.PruneTo([]);
        var overlapped = await harness.Runner.RunJobAsync(job, concurrencyGate: null, onEvent: null, CancellationToken.None);
        Assert.True(overlapped.Skipped);

        release.SetResult();
        Assert.False((await first).Skipped);
        Assert.NotNull(harness.Runner.GetLastResult("slow"));

        // Once the occurrence has finished, the next tick's prune evicts lock and last result.
        harness.Runner.PruneTo([]);
        Assert.Null(harness.Runner.GetLastResult("slow"));
    }
}

// ── Test harness ────────────────────────────────────────────────────────────

file sealed class Harness
{
    public required ScheduleJobRunner Runner { get; init; }
    public required RecordingSessionStore Sessions { get; init; }
    public required CountingPrompt InteractivePrompt { get; init; }

    public static Harness Build(ITool tool, ITurnStrategy strategy, string globalWorkingRoot = ".")
        {
            var sessions = new RecordingSessionStore();
            var runtime = new RuntimeSettings(
                Options.Create(new CaliperOptions
                {
                    Model = "test",
                    MaxSteps = 6,
                    DuplicateCallLimit = 4,
                    WorkingRoot = globalWorkingRoot,
                }),
                Options.Create(new PermissionsOptions { Mode = PermissionMode.AskAlways }));

            // The interactive console composition: RoutingPermissionPrompt in front of the real
            // gate. Job runs set RunSpec.Unattended = true, so the interactive side must stay cold.
            var interactive = new CountingPrompt();
            var routing = new RoutingPermissionPrompt(
                interactive,
                new UnattendedPermissionPrompt(NullLogger<UnattendedPermissionPrompt>.Instance));
            var services = new ServiceCollection()
                .AddSingleton<IPermissionPrompt>(routing)
                .BuildServiceProvider();
            var gate = new PermissionGate(runtime, services);

            var agentRunner = new AgentRunner(
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
                agentRunner,
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
            var runner = new ScheduleJobRunner(
                orchestrator,
                sessions,
                new FakeTimeProvider(),
                NullLogger<ScheduleJobRunner>.Instance);

            return new Harness { Runner = runner, Sessions = sessions, InteractivePrompt = interactive };
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

file sealed class BlockingTurnStrategy(Task release) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await release.WaitAsync(ct);
        yield return new TurnCompleted(new ModelTurn("finally done", [], null, new UsageInfo(1, 1, 2)));
    }
}

file sealed class CountingPrompt : IPermissionPrompt
{
    public int Count { get; private set; }

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Count++;
        return Task.FromResult(PermissionDecision.Allow);
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

file sealed class FileWriteStub : ITool
{
    private static readonly JsonElement s_parameterSchema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public List<string> ObservedWorkingRoots { get; } = [];

    // Named write_file so PermissionGate treats it as a file tool and consults FileAccessPolicy.
    public string Name => "write_file";
    public string Description => "Pretends to write a file in tests.";
    public JsonElement ParameterSchema => s_parameterSchema;
    public SideEffect SideEffect => SideEffect.Write;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        ObservedWorkingRoots.Add(ctx.WorkingRoot);
        return Task.FromResult(new ToolResult(true, "wrote"));
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

file sealed class RecordingSessionStore : ISessionStore
{
    private readonly Dictionary<string, List<ChatMessage>> _data = [];

    public List<string> Titles { get; } = [];

    public Task<string> CreateAsync(string? title, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_data)
        {
            _data[id] = [];
            if (title is not null)
                Titles.Add(title);
        }

        return Task.FromResult(id);
    }

    public Task AppendAsync(string id, ChatMessage msg, CancellationToken ct)
    {
        lock (_data)
        {
            if (!_data.TryGetValue(id, out var list))
            {
                list = [];
                _data[id] = list;
            }

            list.Add(msg);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string id, CancellationToken ct)
    {
        lock (_data)
        {
            IReadOnlyList<ChatMessage> msgs = _data.TryGetValue(id, out var list) ? [.. list] : [];
            return Task.FromResult(msgs);
        }
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        lock (_data)
            _data.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        lock (_data)
        {
            var prefix = _data.TryGetValue(sessionId, out var existing)
                ? existing.Take(Math.Max(0, fit.ActiveStartIndex)).ToList()
                : [];
            prefix.AddRange(fit.Messages);
            _data[sessionId] = prefix;
        }

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

    public Task<IReadOnlyList<RunRecord>> ListRecentScheduledAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunRecord>>(
            [.. _runs.Values.Where(run => run.JobName is not null).Take(limit)]);
}
