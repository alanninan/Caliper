// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Persistence;
using Caliper.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using CaliperChatMessage = Caliper.Core.Models.ChatMessage;
using CaliperChatRole = Caliper.Core.Models.ChatRole;

namespace Caliper.Core.Tests.Agents;

/// <summary>
/// Roadmap §3.4 durable execution. Uses the real <see cref="SqliteSessionStore"/>/
/// <see cref="SqliteRunStore"/> and the real <see cref="NativeToolStrategy"/> (not a scripted turn
/// strategy) so B1's dangling-tool-call healing and the resume note both actually flow through
/// <c>NativeToolStrategy.BuildMessages</c> into what the model sees — reusing the test doubles
/// already declared (non-<c>file</c>, so assembly-visible) in <c>NativeToolStrategyTests.cs</c>:
/// <see cref="ScriptedChatClient"/>, <see cref="SingleChatClientProvider"/>, <see cref="EchoTool"/>,
/// <see cref="StaticCapabilityProvider"/>, etc.
/// </summary>
public sealed class ConversationOrchestratorDurableExecutionTests : IDisposable
{
    private readonly List<string> _paths = [];

    // ── Terminal status mapping (one case per CompletionReason value) ───────

    [Theory]
    [InlineData(CompletionReason.Completed, RunStatus.Completed)]
    [InlineData(CompletionReason.Cancelled, RunStatus.Cancelled)]
    [InlineData(CompletionReason.StepLimit, RunStatus.Failed)]
    [InlineData(CompletionReason.LoopDetected, RunStatus.Failed)]
    [InlineData(CompletionReason.Denied, RunStatus.Failed)]
    public void MapCompletionStatus_maps_every_CompletionReason_value(CompletionReason reason, RunStatus expected)
    {
        Assert.Equal(expected, ConversationOrchestrator.MapCompletionStatus(reason, error: null));
    }

    [Fact]
    public void MapCompletionStatus_maps_a_RunFailed_error_to_Failed_regardless_of_reason()
    {
        Assert.Equal(RunStatus.Failed, ConversationOrchestrator.MapCompletionStatus(reason: null, "Streaming error: boom"));
    }

    // ── RunToCompletionAsync bookkeeping ─────────────────────────────────────

    [Fact]
    public async Task RunToCompletionAsync_creates_a_running_row_and_completes_it_on_success()
    {
        var path = NewDbPath();
        var (sessions, runStore, orchestrator, client) = Build(
            path,
            [[new ChatResponseUpdate(AIChatRole.Assistant, "done")]]);
        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);

        var result = await orchestrator.RunToCompletionAsync(
            new RunSpec(sessionId, "please help"),
            onEvent: null,
            CancellationToken.None);

        Assert.Equal("done", result.AssistantMessage);
        Assert.Equal(CompletionReason.Completed, result.Reason);

        var runs = await runStore.ListRecentAsync(10, CancellationToken.None);
        var run = Assert.Single(runs);
        Assert.Equal(sessionId, run.SessionId);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, run.Step);
        Assert.NotEmpty(client.Requests);
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeAsync_on_an_interrupted_run_heals_the_dangling_call_appends_the_note_and_completes()
    {
        var path = NewDbPath();
        var (sessions, runStore, orchestrator, client) = Build(
            path,
            [[new ChatResponseUpdate(AIChatRole.Assistant, "resumed and done")]]);

        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        await sessions.AppendAsync(sessionId, CaliperChatMessage.Text(CaliperChatRole.User, "original task"), CancellationToken.None);
        // Simulate a run killed mid-tool: a ToolCall persisted with no matching ToolResult.
        var args = JsonDocument.Parse("""{"text":"hi"}""").RootElement.Clone();
        await sessions.AppendAsync(
            sessionId,
            CaliperChatMessage.FromToolCall(new ToolCall("call_orphan", "echo", args)),
            CancellationToken.None);

        var runId = await runStore.StartAsync(sessionId, jobName: null, maxSteps: 10, unattended: false, CancellationToken.None);
        await runStore.UpdateStepAsync(runId, 4, CancellationToken.None);
        await runStore.CompleteAsync(runId, RunStatus.Interrupted, "Interrupted by startup sweep.", CancellationToken.None);

        var result = await orchestrator.ResumeAsync(runId, onEvent: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("resumed and done", result.AssistantMessage);
        Assert.Equal(CompletionReason.Completed, result.Reason);

        // What the model actually saw on the resumed turn.
        var sentMessages = Assert.Single(client.Requests);
        Assert.Contains(sentMessages, message =>
            message.Role == AIChatRole.Tool &&
            message.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call_orphan"));
        Assert.Contains(sentMessages, message =>
            message.Role == AIChatRole.System &&
            message.Text.Contains("run interrupted at step 4", StringComparison.Ordinal));

        var run = await runStore.GetAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, run!.Status);
    }

    [Fact]
    public async Task ResumeAsync_bounds_the_resumed_run_to_the_remaining_step_budget()
    {
        // Original MaxSteps 10, interrupted at step 4 => resumed run bounded to 6. The scripted
        // client always returns a tool call (never plain text) for as many turns as it's asked, so
        // the run can only stop via the step budget — 8 scripted tool-call turns is more than the
        // correct bound (6) but fewer than the original (10), so an incorrectly-unbounded resume
        // would visibly run past 6 turns instead of stopping there.
        var path = NewDbPath();
        var toolCallResponses = Enumerable.Range(0, 8)
            .Select(i => (IReadOnlyList<ChatResponseUpdate>)
            [
                new ChatResponseUpdate(AIChatRole.Assistant,
                [
                    new FunctionCallContent($"call_{i}", "echo", new Dictionary<string, object?> { ["text"] = $"turn {i}" }),
                ]),
            ])
            .ToList();
        var (sessions, runStore, orchestrator, client) = Build(path, toolCallResponses, maxSteps: 25);

        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        await sessions.AppendAsync(sessionId, CaliperChatMessage.Text(CaliperChatRole.User, "keep going"), CancellationToken.None);

        var runId = await runStore.StartAsync(sessionId, jobName: null, maxSteps: 10, unattended: false, CancellationToken.None);
        await runStore.UpdateStepAsync(runId, 4, CancellationToken.None);
        await runStore.CompleteAsync(runId, RunStatus.Interrupted, "Interrupted by startup sweep.", CancellationToken.None);

        var result = await orchestrator.ResumeAsync(runId, onEvent: null, CancellationToken.None);

        Assert.Equal(CompletionReason.StepLimit, result.Reason);
        Assert.Equal(6, client.Requests.Count);

        var run = await runStore.GetAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatus.Failed, run!.Status); // StepLimit maps to Failed (see mapping doc comment)
        Assert.Equal(6, run.MaxSteps);
        Assert.Equal(6, run.Step);
    }

    [Fact]
    public async Task ResumeAsync_on_a_nonexistent_run_returns_a_clear_error_and_creates_no_run()
    {
        var path = NewDbPath();
        var (_, runStore, orchestrator, client) = Build(path, [[new ChatResponseUpdate(AIChatRole.Assistant, "unused")]]);

        var result = await orchestrator.ResumeAsync("missing-run-id", onEvent: null, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Reason);
        Assert.Empty(client.Requests);
        Assert.Empty(await runStore.ListRecentAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task ResumeAsync_on_a_non_interrupted_run_returns_a_clear_error_without_driving_it()
    {
        var path = NewDbPath();
        var (sessions, runStore, orchestrator, client) = Build(path, [[new ChatResponseUpdate(AIChatRole.Assistant, "unused")]]);
        var sessionId = await sessions.CreateAsync(null, CancellationToken.None);
        var runId = await runStore.StartAsync(sessionId, null, 10, false, CancellationToken.None);
        await runStore.CompleteAsync(runId, RunStatus.Completed, "Completed", CancellationToken.None);

        var result = await orchestrator.ResumeAsync(runId, onEvent: null, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("not resumable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.Requests);

        var run = await runStore.GetAsync(runId, CancellationToken.None);
        Assert.Equal(RunStatus.Completed, run!.Status); // unchanged
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static (SqliteSessionStore Sessions, SqliteRunStore Runs, ConversationOrchestrator Orchestrator, ScriptedChatClient Client) Build(
        string path,
        IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> responses,
        int maxSteps = 25)
    {
        var options = new CaliperOptions
        {
            Model = "test/model",
            MaxSteps = maxSteps,
            DuplicateCallLimit = 20,
            EnabledTools = ["echo"],
        };
        var runtime = new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions()));
        var capabilities = new StaticCapabilityProvider(new ModelCapabilities(true, true, true, 32768));
        var client = new ScriptedChatClient(responses);
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            capabilities,
            runtime,
            NullLogger<NativeToolStrategy>.Instance);

        var sessions = new SqliteSessionStore(
            Options.Create(new PersistenceOptions { SqlitePath = path }),
            Options.Create(options),
            NullLogger<SqliteSessionStore>.Instance);
        var runStore = new SqliteRunStore(
            Options.Create(new PersistenceOptions { SqlitePath = path }),
            TimeProvider.System,
            NullLogger<SqliteRunStore>.Instance);

        var registry = new ToolRegistry([new EchoTool()], Options.Create(options), NullLogger<ToolRegistry>.Instance);

        var runner = new AgentRunner(
            strategy,
            registry,
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            sessions,
            new NativeEmptyMemoryStore(),
            new NativeEmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            capabilities,
            new NativeAllowAllGate(),
            runtime,
            NullLogger<AgentRunner>.Instance);

        var orchestrator = new ConversationOrchestrator(
            runner,
            sessions,
            new EmptySkillStore(),
            registry,
            new NativeEmptyMemoryStore(),
            new NativeEmptyCaliperMdProvider(),
            new PassthroughContextManager(),
            capabilities,
            runtime,
            runStore,
            NullLogger<ConversationOrchestrator>.Instance);

        return (sessions, runStore, orchestrator, client);
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "caliper-durable-" + Guid.NewGuid().ToString("N") + ".db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths.SelectMany(path => new[] { path, $"{path}-wal", $"{path}-shm" }))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
