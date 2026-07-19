// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.App.Scheduling;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.App.Tests;

public sealed class SchedulesViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);

    private static (
        SchedulesViewModel ViewModel,
        FakeConfigWriter ConfigWriter,
        FakeConversationOrchestrator Orchestrator,
        FakeSessionStore Sessions,
        SessionsViewModel SessionsViewModel)
        Create(FakeRunStore? runStore = null, FakeChatSessionController? chatController = null)
    {
        var configWriter = new FakeConfigWriter();
        var sessionStore = new FakeSessionStore();
        var orchestrator = new FakeConversationOrchestrator();
        var timeProvider = new FakeTimeProvider(Now);
        var runner = new ScheduleJobRunner(orchestrator, sessionStore, timeProvider, NullLogger<ScheduleJobRunner>.Instance);
        var preferences = new FakePreferencesStore();
        var controller = new AppSchedulerController(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<AppSchedulerController>.Instance);
        var chat = chatController ?? new FakeChatSessionController();
        var sessions = new SessionsViewModel(sessionStore, chat, preferences, timeProvider);
        runStore ??= new FakeRunStore();
        var viewModel = new SchedulesViewModel(
            configWriter,
            runner,
            timeProvider,
            preferences,
            controller,
            sessions,
            runStore,
            orchestrator,
            chat);
        return (viewModel, configWriter, orchestrator, sessionStore, sessions);
    }

    // ── List projection: next occurrence ────────────────────────────────────

    [Fact]
    public async Task LoadAsync_disabled_job_shows_dash_for_next_occurrence()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "off", Cron = "*/5 * * * *", Prompt = "p", Enabled = false }];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("—", viewModel.Jobs.Single().NextOccurrenceText);
    }

    [Fact]
    public async Task LoadAsync_cron_that_never_fires_again_shows_never()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        // Feb 30th never occurs — Cronos parses this successfully but GetNextOccurrence always
        // returns null with a null error (see ScheduleCron's doc comment for this exact example).
        configWriter.Schedules = [new ScheduleOptions { Name = "dead", Cron = "0 0 30 2 *", Prompt = "p", Enabled = true }];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("never", viewModel.Jobs.Single().NextOccurrenceText);
    }

    [Fact]
    public async Task LoadAsync_invalid_cron_shows_invalid_prefix()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "bad", Cron = "not a cron", Prompt = "p", Enabled = true }];

        await viewModel.LoadCommand.ExecuteAsync(null);

        var job = viewModel.Jobs.Single();
        Assert.StartsWith("invalid — ", job.NextOccurrenceText, StringComparison.Ordinal);
        Assert.False(job.CronIsValid);
    }

    [Fact]
    public async Task LoadAsync_valid_enabled_cron_shows_local_time_occurrence()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "nightly", Cron = "*/5 * * * *", Prompt = "p", Enabled = true }];

        await viewModel.LoadCommand.ExecuteAsync(null);

        var job = viewModel.Jobs.Single();
        Assert.NotEqual("—", job.NextOccurrenceText);
        Assert.NotEqual("never", job.NextOccurrenceText);
        Assert.DoesNotContain("invalid", job.NextOccurrenceText, StringComparison.Ordinal);
        Assert.True(job.CronIsValid);
        Assert.Contains("Next occurrence:", job.CronValidationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_job_never_run_shows_dash_for_last_result()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "fresh", Cron = "*/5 * * * *", Prompt = "p" }];

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("—", viewModel.Jobs.Single().LastResultText);
    }

    // ── Cron client-side validation (edit-time) ─────────────────────────────

    [Fact]
    public void Editing_cron_to_invalid_text_updates_validation_message_live()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.AddJobCommand.Execute(null);
        var job = viewModel.SelectedJob!;
        job.Prompt = "do work";

        job.Cron = "garbage";

        Assert.False(job.CronIsValid);
        Assert.False(string.IsNullOrEmpty(job.ValidationMessage));
    }

    [Fact]
    public void Editing_cron_to_valid_text_clears_invalid_state()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.AddJobCommand.Execute(null);
        var job = viewModel.SelectedJob!;
        job.Prompt = "do work";

        job.Cron = "*/5 * * * *";

        Assert.True(job.CronIsValid);
        Assert.Equal(string.Empty, job.ValidationMessage);
    }

    // ── Add / edit / remove round-trip through the fake IConfigWriter ───────

    [Fact]
    public async Task SaveAsync_persists_added_job()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.AddJobCommand.Execute(null);
        var job = viewModel.SelectedJob!;
        job.Cron = "*/5 * * * *";
        job.Prompt = "summarize";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(configWriter.SavedSchedules!);
        Assert.Equal(job.Name, saved.Name);
        Assert.Equal("*/5 * * * *", saved.Cron);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveAsync_surfaces_failed_config_write_result_without_throwing()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.AddJobCommand.Execute(null);
        var job = viewModel.SelectedJob!;
        job.Cron = "*/5 * * * *";
        job.Prompt = "summarize";
        configWriter.NextSuccess = false;
        configWriter.NextError = "Schedule 'x': Permissions overlay sets ShellAutoAllowlist but Mode is AskAlways.";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Equal(configWriter.NextError, viewModel.StatusMessage);
    }

    [Fact]
    public async Task RemoveSelectedJob_then_save_persists_an_empty_list()
    {
        var (viewModel, configWriter, _, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "only", Cron = "*/5 * * * *", Prompt = "p" }];
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.RemoveSelectedJobCommand.Execute(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Jobs);
        Assert.False(viewModel.HasJobs);
        Assert.Empty(configWriter.SavedSchedules!);
    }

    [Fact]
    public void SaveCommand_is_disabled_while_a_job_is_missing_required_fields()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.AddJobCommand.Execute(null);

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void SaveCommand_is_disabled_for_duplicate_job_names()
    {
        var (viewModel, _, _, _, _) = Create();
        viewModel.AddJobCommand.Execute(null);
        var first = viewModel.SelectedJob!;
        first.Cron = "*/5 * * * *";
        first.Prompt = "p1";
        viewModel.AddJobCommand.Execute(null);
        var second = viewModel.SelectedJob!;
        second.Cron = "*/5 * * * *";
        second.Prompt = "p2";

        second.Name = first.Name;

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    // ── Permissions overlay: null <-> non-null mapping ──────────────────────

    [Fact]
    public void ToOptions_permissions_override_off_maps_to_null()
    {
        var item = ScheduleItemViewModel.FromOptions(
            new ScheduleOptions { Name = "n", Cron = "* * * * *", Prompt = "p" },
            _ => { });

        var options = item.ToOptions();

        Assert.Null(options.Permissions);
    }

    [Fact]
    public void ToOptions_permissions_override_on_maps_allowlists_and_mode()
    {
        var item = ScheduleItemViewModel.FromOptions(
            new ScheduleOptions { Name = "n", Cron = "* * * * *", Prompt = "p" },
            _ => { });
        item.OverridePermissions = true;
        item.PermissionsModeText = "Auto";
        item.ShellAutoAllowlistText = "git status\ndotnet build";
        item.AutoAllowFileRootsText = "C:\\shared";

        var options = item.ToOptions();

        Assert.NotNull(options.Permissions);
        Assert.Equal(PermissionMode.Auto, options.Permissions!.Mode);
        Assert.Equal(["git status", "dotnet build"], options.Permissions.ShellAutoAllowlist);
        Assert.Equal(["C:\\shared"], options.Permissions.AutoAllowFileRoots);
    }

    [Fact]
    public void FromOptions_then_ToOptions_round_trips_a_permissions_overlay()
    {
        var original = new ScheduleOptions
        {
            Name = "n",
            Cron = "* * * * *",
            Prompt = "p",
            Permissions = new PermissionsOptions
            {
                Mode = PermissionMode.Auto,
                ShellAutoAllowlist = ["git status"],
                AutoAllowFileRoots = ["C:\\shared"],
            },
        };

        var item = ScheduleItemViewModel.FromOptions(original, _ => { });

        Assert.True(item.OverridePermissions);
        Assert.Equal("Auto", item.PermissionsModeText);

        var roundTripped = item.ToOptions();
        Assert.NotNull(roundTripped.Permissions);
        Assert.Equal(PermissionMode.Auto, roundTripped.Permissions!.Mode);
        Assert.Equal(["git status"], roundTripped.Permissions.ShellAutoAllowlist);
        Assert.Equal(["C:\\shared"], roundTripped.Permissions.AutoAllowFileRoots);
    }

    // ── Run now ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunNow_shows_denial_count_and_session_hint_and_refreshes_last_result()
    {
        var (viewModel, configWriter, orchestrator, sessionStore, sessions) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "nightly", Cron = "*/5 * * * *", Prompt = "p" }];
        await viewModel.LoadCommand.ExecuteAsync(null);
        await sessions.InitializeAsync(CancellationToken.None);
        orchestrator.NextResult = new ConversationRunResult(
            "done", null, CompletionReason.Completed, [new DeniedAction("bash", "rm -rf /", "blocked")]);
        var job = viewModel.Jobs.Single();

        await viewModel.RunNowCommand.ExecuteAsync(job);

        Assert.False(job.IsRunning);
        Assert.False(job.RunOutcomeIsError);
        Assert.Contains("1 action(s) denied", job.RunOutcomeText, StringComparison.Ordinal);
        Assert.Contains("[job] nightly", job.RunOutcomeText, StringComparison.Ordinal);
        Assert.NotEqual("—", job.LastResultText);
        Assert.Contains("denial(s)", job.LastResultText, StringComparison.Ordinal);
        // The job session — created directly through ISessionStore by ScheduleJobRunner, not via
        // the chat controller — must show up in the (shared) SessionsViewModel after the refresh.
        Assert.Contains(sessions.Items, sessionItem => sessionItem.Title == "[job] nightly");
        Assert.Contains(await sessionStore.ListAsync(CancellationToken.None), s => s.Title == "[job] nightly");
    }

    [Fact]
    public async Task RunNow_job_error_marks_outcome_as_error()
    {
        var (viewModel, configWriter, orchestrator, _, _) = Create();
        configWriter.Schedules = [new ScheduleOptions { Name = "flaky", Cron = "*/5 * * * *", Prompt = "p" }];
        await viewModel.LoadCommand.ExecuteAsync(null);
        orchestrator.NextResult = new ConversationRunResult(null, "boom", CompletionReason.Denied, []);
        var job = viewModel.Jobs.Single();

        await viewModel.RunNowCommand.ExecuteAsync(job);

        Assert.True(job.RunOutcomeIsError);
        Assert.Contains("Job error: boom", job.RunOutcomeText, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatOutcome_skipped_reports_previous_occurrence_still_running()
    {
        var outcome = new ScheduleRunOutcome("nightly", Now, Reason: null, Error: null, DenialCount: 0, Skipped: true);

        var text = SchedulesViewModel.FormatOutcome("nightly", outcome);

        Assert.Equal("Skipped — a previous occurrence of this job is still running.", text);
    }

    [Fact]
    public void FormatOutcome_denials_and_session_hint_are_present()
    {
        var outcome = new ScheduleRunOutcome("nightly", Now, CompletionReason.Completed, Error: null, DenialCount: 3, Skipped: false);

        var text = SchedulesViewModel.FormatOutcome("nightly", outcome);

        Assert.Contains("3 action(s) denied (unattended policy).", text, StringComparison.Ordinal);
        Assert.Contains("Transcript saved to session '[job] nightly'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_populates_only_scheduled_run_history()
    {
        var runStore = new FakeRunStore
        {
            Records =
            [
                MakeRun("scheduled", "session-job", "nightly", RunStatus.Completed),
                MakeRun("other", "session-other", null, RunStatus.Completed),
            ],
        };
        var (viewModel, _, _, _, _) = Create(runStore);

        await viewModel.LoadCommand.ExecuteAsync(null);

        var history = Assert.Single(viewModel.RunHistory);
        Assert.Equal("nightly", history.ScheduleName);
        Assert.Equal("scheduled", history.RunId);
        Assert.True(viewModel.HasRunHistory);
    }

    [Fact]
    public async Task OpenHistorySession_selects_the_scheduled_run_chat()
    {
        var runStore = new FakeRunStore
        {
            Records = [MakeRun("scheduled", "session-job", "nightly", RunStatus.Completed)],
        };
        var chat = new FakeChatSessionController();
        var (viewModel, _, _, _, _) = Create(runStore, chat);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.OpenHistorySessionCommand.ExecuteAsync(viewModel.RunHistory.Single());

        Assert.Equal("session-job", chat.CurrentSessionId);
    }

    [Fact]
    public async Task ResumeHistoryRun_resumes_only_interrupted_scheduled_runs()
    {
        var runStore = new FakeRunStore
        {
            Records = [MakeRun("scheduled", "session-job", "nightly", RunStatus.Interrupted)],
        };
        var (viewModel, _, orchestrator, _, _) = Create(runStore);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.ResumeHistoryRunCommand.ExecuteAsync(viewModel.RunHistory.Single());

        Assert.Equal(1, orchestrator.ResumeCallCount);
        Assert.Contains("nightly finished", viewModel.HistoryStatusMessage, StringComparison.Ordinal);
    }

    private static RunRecord MakeRun(
        string runId,
        string sessionId,
        string? jobName,
        RunStatus status) =>
        new(
            runId,
            sessionId,
            jobName,
            status,
            "Completed",
            2,
            10,
            true,
            Now.AddMinutes(-5),
            Now,
            false);

    // ── Fakes (per-file convention, matching ChatViewModelTests/SessionsViewModelTests) ────────

    private sealed class FakeConversationOrchestrator : IConversationOrchestrator
    {
        public ConversationRunResult NextResult { get; set; } = new(null, null, CompletionReason.Completed, []);
        public int ResumeCallCount { get; private set; }

        public Task<ConversationRunResult> RunToCompletionAsync(
            string sessionId, string prompt, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(NextResult);

        public Task<ConversationRunResult> RunToCompletionAsync(
            RunSpec spec, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(NextResult);

        public Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(new ContextFit([], Compacted: false, BeforeTokens: null, AfterTokens: null, EstimatedPromptTokens: null));

        public Task<ConversationRunResult> ResumeAsync(
            string runId,
            Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
            CancellationToken ct)
        {
            ResumeCallCount++;
            return Task.FromResult(NextResult);
        }
    }

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

        public Task<IReadOnlyList<RunRecord>> ListRecentScheduledAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RunRecord>>(
                [.. Records.Where(record => record.JobName is not null).Take(limit)]);
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
        public event EventHandler? RunActivityChanged;
        public event EventHandler<SessionCreatedEventArgs>? SessionCreated;
        public event EventHandler<SessionRenamedEventArgs>? SessionRenamed;

        public Task SelectSessionAsync(string sessionId, CancellationToken ct)
        {
            CurrentSessionId = sessionId;
            return Task.CompletedTask;
        }

        public bool CanDeleteSession(string sessionId) => true;

        public void RemoveSession(string sessionId)
        {
        }

        public void ClearSessionSelection() => CurrentSessionId = null;

        // Unused by these tests, but kept (mirroring SessionsViewModelTests' identical fake) so the
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
