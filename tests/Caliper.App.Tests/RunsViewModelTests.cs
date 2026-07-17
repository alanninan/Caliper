// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.App.Tests;

public sealed class RunsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    private static (
        RunsViewModel ViewModel,
        FakeRunStore RunStore,
        FakeConversationOrchestrator Orchestrator,
        FakeChatSessionController Chat,
        SessionsViewModel Sessions)
        Create()
    {
        var runStore = new FakeRunStore();
        var orchestrator = new FakeConversationOrchestrator();
        var sessionStore = new FakeSessionStore();
        var timeProvider = new FakeTimeProvider(Now);
        var preferences = new FakePreferencesStore();
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(sessionStore, chat, preferences, timeProvider);
        var viewModel = new RunsViewModel(runStore, orchestrator, chat, sessions);
        return (viewModel, runStore, orchestrator, chat, sessions);
    }

    private static RunRecord MakeRun(
        string runId = "run-00000001",
        string sessionId = "session-00000001",
        string? jobName = null,
        RunStatus status = RunStatus.Completed,
        string? reason = "Completed",
        int step = 3,
        int maxSteps = 40,
        bool unattended = false,
        bool resumed = false) =>
        new(runId, sessionId, jobName, status, reason, step, maxSteps, unattended, Now.AddMinutes(-5), Now, resumed);

    // ── Row projection ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_projects_short_ids_and_dash_for_null_job()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(runId: "abcdefgh12345", sessionId: "ijklmnop67890", jobName: null)];

        await viewModel.LoadCommand.ExecuteAsync(null);

        var row = viewModel.Runs.Single();
        Assert.Equal("abcdefgh", row.ShortRunId);
        Assert.Equal("ijklmnop", row.ShortSessionId);
        Assert.Equal("—", row.JobText);
    }

    [Fact]
    public async Task LoadAsync_job_name_shown_when_present()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(jobName: "nightly")];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("nightly", viewModel.Runs.Single().JobText);
    }

    [Fact]
    public async Task LoadAsync_resumed_run_shows_resumed_suffix_in_status_text()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(status: RunStatus.Completed, resumed: true)];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Completed (resumed)", viewModel.Runs.Single().StatusText);
    }

    [Fact]
    public async Task LoadAsync_projects_step_over_max_steps_text()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(step: 7, maxSteps: 40)];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Step 7/40", viewModel.Runs.Single().StepText);
    }

    [Fact]
    public async Task LoadAsync_surfaces_non_null_reason_as_secondary_text()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(reason: "boom: connection refused")];

        await viewModel.LoadCommand.ExecuteAsync(null);

        var row = viewModel.Runs.Single();
        Assert.True(row.HasReason);
        Assert.Equal("boom: connection refused", row.Reason);
    }

    [Fact]
    public async Task LoadAsync_null_reason_hides_secondary_text()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(reason: null)];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(viewModel.Runs.Single().HasReason);
    }

    // ── Interrupted-only resume gating ──────────────────────────────────────

    [Fact]
    public async Task LoadAsync_only_interrupted_rows_can_resume()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records =
        [
            MakeRun(runId: "run-a", status: RunStatus.Interrupted, reason: "sweep note"),
            MakeRun(runId: "run-b", status: RunStatus.Completed),
        ];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.Runs.Single(r => r.RunId == "run-a").CanResume);
        Assert.False(viewModel.Runs.Single(r => r.RunId == "run-b").CanResume);
    }

    [Fact]
    public async Task ResumeCommand_ignores_a_non_interrupted_run()
    {
        var (viewModel, runStore, orchestrator, _, _) = Create();
        runStore.Records = [MakeRun(status: RunStatus.Completed)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.Equal(0, orchestrator.ResumeCallCount);
    }

    // ── Resume outcomes ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeCommand_success_reports_finished_reason_and_denial_count()
    {
        var (viewModel, runStore, orchestrator, _, _) = Create();
        runStore.Records = [MakeRun(runId: "run-1", sessionId: "sess0001", status: RunStatus.Interrupted)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();
        orchestrator.NextResumeResult = new ConversationRunResult(
            "done", null, CompletionReason.Completed, [new DeniedAction("bash", "rm -rf /", "blocked")]);

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.False(viewModel.ResumeOutcomeIsError);
        Assert.Contains("Finished: Completed", viewModel.ResumeOutcomeText, StringComparison.Ordinal);
        Assert.Contains("1 action(s) denied (unattended policy).", viewModel.ResumeOutcomeText, StringComparison.Ordinal);
        Assert.Contains("Transcript is in session 'sess0001'", viewModel.ResumeOutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeCommand_error_marks_outcome_as_error()
    {
        var (viewModel, runStore, orchestrator, _, _) = Create();
        runStore.Records = [MakeRun(status: RunStatus.Interrupted)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();
        orchestrator.NextResumeResult = new ConversationRunResult(null, "boom", null, []);

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.True(viewModel.ResumeOutcomeIsError);
        Assert.Contains("Error: boom", viewModel.ResumeOutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcome_denials_and_session_hint_are_present()
    {
        var result = new ConversationRunResult("done", null, CompletionReason.Completed, [new DeniedAction("bash", "x", null)]);

        var text = RunsViewModel.FormatOutcome(result, "abcdefgh");

        Assert.Contains("1 action(s) denied (unattended policy).", text, StringComparison.Ordinal);
        Assert.Contains("Transcript is in session 'abcdefgh'", text, StringComparison.Ordinal);
    }

    // ── List reload after resume ─────────────────────────────────────────────

    [Fact]
    public async Task ResumeCommand_reloads_the_runs_list_after_completion()
    {
        var (viewModel, runStore, orchestrator, _, _) = Create();
        runStore.Records = [MakeRun(runId: "run-1", status: RunStatus.Interrupted)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();
        orchestrator.NextResumeResult = new ConversationRunResult("done", null, CompletionReason.Completed, []);
        // The store now reflects the completed run — this is what makes the reload observable.
        runStore.Records = [MakeRun(runId: "run-1", status: RunStatus.Completed)];

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.Equal(RunStatus.Completed, viewModel.Runs.Single().Status);
        Assert.False(viewModel.Runs.Single().CanResume);
    }

    // ── Current-session re-select behavior ──────────────────────────────────

    [Fact]
    public async Task ResumeCommand_reselects_session_when_it_matches_the_current_chat_session()
    {
        var (viewModel, runStore, orchestrator, chat, _) = Create();
        runStore.Records = [MakeRun(runId: "run-1", sessionId: "session-current", status: RunStatus.Interrupted)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();
        await chat.SelectSessionAsync("session-current", CancellationToken.None);
        chat.SelectSessionCallCount = 0;
        orchestrator.NextResumeResult = new ConversationRunResult("done", null, CompletionReason.Completed, []);

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.Equal(1, chat.SelectSessionCallCount);
        Assert.Equal("session-current", chat.CurrentSessionId);
    }

    [Fact]
    public async Task ResumeCommand_does_not_reselect_session_when_it_differs_from_current_chat_session()
    {
        var (viewModel, runStore, orchestrator, chat, _) = Create();
        runStore.Records = [MakeRun(runId: "run-1", sessionId: "session-other", status: RunStatus.Interrupted)];
        await viewModel.LoadCommand.ExecuteAsync(null);
        var row = viewModel.Runs.Single();
        await chat.SelectSessionAsync("session-current", CancellationToken.None);
        chat.SelectSessionCallCount = 0;
        orchestrator.NextResumeResult = new ConversationRunResult("done", null, CompletionReason.Completed, []);

        await viewModel.ResumeCommand.ExecuteAsync(row);

        Assert.Equal(0, chat.SelectSessionCallCount);
        Assert.Equal("session-current", chat.CurrentSessionId);
    }

    // ── Startup interruption count ────────────────────────────────────────────

    [Fact]
    public async Task CheckStartupInterruptionsAsync_reports_zero_when_no_interrupted_runs()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records = [MakeRun(status: RunStatus.Completed)];

        await viewModel.CheckStartupInterruptionsAsync(CancellationToken.None);

        Assert.Equal(0, viewModel.InterruptedStartupCount);
        Assert.False(viewModel.ShowStartupBanner);
    }

    [Fact]
    public async Task CheckStartupInterruptionsAsync_counts_interrupted_runs_and_shows_banner()
    {
        var (viewModel, runStore, _, _, _) = Create();
        runStore.Records =
        [
            MakeRun(runId: "run-a", status: RunStatus.Interrupted),
            MakeRun(runId: "run-b", status: RunStatus.Interrupted),
            MakeRun(runId: "run-c", status: RunStatus.Completed),
        ];

        await viewModel.CheckStartupInterruptionsAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.InterruptedStartupCount);
        Assert.True(viewModel.ShowStartupBanner);
    }

    [Fact]
    public void DismissStartupBanner_hides_the_banner_even_with_interrupted_runs()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.InterruptedStartupCount = 2;

        viewModel.DismissStartupBannerCommand.Execute(null);

        Assert.False(viewModel.ShowStartupBanner);
    }

    // ── Fakes (per-file convention, matching SchedulesViewModelTests) ────────

    private sealed class FakeRunStore : IRunStore
    {
        public IReadOnlyList<RunRecord> Records { get; set; } = [];

        public Task<string> StartAsync(string sessionId, string? jobName, int maxSteps, bool unattended, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task UpdateStepAsync(string runId, int stepNumber, CancellationToken ct) => Task.CompletedTask;

        public Task CompleteAsync(string runId, RunStatus status, string? reason, CancellationToken ct) => Task.CompletedTask;

        public Task MarkResumedAsync(string runId, int maxSteps, CancellationToken ct) => Task.CompletedTask;

        public Task<RunRecord?> GetAsync(string runId, CancellationToken ct) =>
            Task.FromResult(Records.FirstOrDefault(record => record.RunId == runId));

        public Task<IReadOnlyList<RunRecord>> ListRecentAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RunRecord>>([.. Records.Take(limit)]);
    }

    private sealed class FakeConversationOrchestrator : IConversationOrchestrator
    {
        public ConversationRunResult NextResumeResult { get; set; } = new(null, null, CompletionReason.Completed, []);
        public int ResumeCallCount { get; private set; }

        public Task<ConversationRunResult> RunToCompletionAsync(
            string sessionId, string prompt, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(new ConversationRunResult(null, null, CompletionReason.Completed, []));

        public Task<ConversationRunResult> RunToCompletionAsync(
            RunSpec spec, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(new ConversationRunResult(null, null, CompletionReason.Completed, []));

        public Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(new ContextFit([], Compacted: false, BeforeTokens: null, AfterTokens: null, EstimatedPromptTokens: null));

        public Task<ConversationRunResult> ResumeAsync(
            string runId, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct)
        {
            ResumeCallCount++;
            return Task.FromResult(NextResumeResult);
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly List<SessionSummary> _sessions = [];
        private int _nextId;

        public Task<string> CreateAsync(string? title, CancellationToken ct)
        {
            var id = $"session-{++_nextId}";
            _sessions.Insert(0, new SessionSummary(id, title, Now));
            return Task.FromResult(id);
        }

        public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([]);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([.. _sessions]);

        public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

        public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeChatSessionController : IChatSessionController
    {
        public string? CurrentSessionId { get; private set; }
        public string? RunningSessionId { get; set; }
        public int SelectSessionCallCount { get; set; }
        public event EventHandler? RunActivityChanged;
        public event EventHandler<SessionCreatedEventArgs>? SessionCreated;
        public event EventHandler<SessionRenamedEventArgs>? SessionRenamed;

        public Task SelectSessionAsync(string sessionId, CancellationToken ct)
        {
            CurrentSessionId = sessionId;
            SelectSessionCallCount++;
            return Task.CompletedTask;
        }

        public bool CanDeleteSession(string sessionId) => true;

        public void RemoveSession(string sessionId)
        {
        }

        public void ClearSessionSelection() => CurrentSessionId = null;

        // Unused by these tests, but kept (mirroring SchedulesViewModelTests' identical fake) so the
        // field-like events required by IChatSessionController are demonstrably invokable — avoids
        // CS0067 under TreatWarningsAsErrors.
        public void RaiseRunActivityChanged() => RunActivityChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionCreated(SessionSummary summary) =>
            SessionCreated?.Invoke(this, new SessionCreatedEventArgs(summary));

        public void RaiseSessionRenamed(string sessionId, string title) =>
            SessionRenamed?.Invoke(this, new SessionRenamedEventArgs(sessionId, title));
    }

    private sealed class FakePreferencesStore : IAppPreferencesStore
    {
        public AppPreferences Saved { get; private set; } = new();

        public AppPreferences Load() => Saved;

        public void Save(AppPreferences preferences) => Saved = preferences;
    }
}
