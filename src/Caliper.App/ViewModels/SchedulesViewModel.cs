// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Globalization;
using Caliper.App.Navigation;
using Caliper.App.Preferences;
using Caliper.App.Scheduling;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

/// <summary>
/// P1a/P1b/P2: the App's parity surface for the console's <c>/schedule list|run</c> and the
/// headless <c>--serve</c> scheduler. Mirrors <see cref="Settings.McpServersSettingsViewModel"/>'s
/// master-detail shape (a flat in-memory list edited freely, persisted as a whole by one Save),
/// with per-job "Run now" (roadmap P1b) and an opt-in in-app scheduler toggle (roadmap P2) layered
/// on top.
/// </summary>
public sealed partial class SchedulesViewModel(
    IConfigWriter configWriter,
    ScheduleJobRunner scheduleRunner,
    TimeProvider timeProvider,
    IAppPreferencesStore preferencesStore,
    AppSchedulerController schedulerController,
    SessionsViewModel sessions,
    IRunStore runStore,
    IConversationOrchestrator orchestrator,
    IChatSessionController chatSessionController,
    AppNavigationService? navigation = null) : ObservableObject
{
    private const int RecentHistoryLimit = 20;

    public ObservableCollection<ScheduleItemViewModel> Jobs { get; } = [];
    public ObservableCollection<ScheduledRunItemViewModel> RunHistory { get; } = [];

    [ObservableProperty]
    public partial ScheduleItemViewModel? SelectedJob { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    // P2: bound TwoWay to the page's "Run scheduler while the app is open" ToggleSwitch. Setting
    // it (including the initial seed from saved prefs in LoadAsync) starts/stops the shared
    // AppSchedulerController and persists the preference — see ApplySchedulerToggleAsync.
    [ObservableProperty]
    public partial bool RunSchedulerEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsSchedulerRunning { get; set; }

    [ObservableProperty]
    public partial string SchedulerStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsHistoryLoading { get; set; }

    [ObservableProperty]
    public partial string HistoryStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HistoryStatusIsError { get; set; }

    public bool HasJobs => Jobs.Count > 0;
    public bool HasRunHistory => RunHistory.Count > 0;
    public string HistorySummaryText
    {
        get
        {
            var interrupted = RunHistory.Count(run => run.CanResume);
            return interrupted == 0
                ? $"{RunHistory.Count:N0} recent scheduled run{(RunHistory.Count == 1 ? "" : "s")}."
                : $"{RunHistory.Count:N0} recent scheduled runs · {interrupted:N0} interrupted.";
        }
    }

    partial void OnRunSchedulerEnabledChanged(bool value) => _ = ApplySchedulerToggleAsync(value);

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var schedules = await configWriter.LoadSchedulesAsync(ct);
            Jobs.Clear();
            foreach (var schedule in schedules)
                Jobs.Add(ScheduleItemViewModel.FromOptions(schedule, OnJobChanged));

            foreach (var job in Jobs)
                RecomputeJob(job);

            SelectedJob = Jobs.FirstOrDefault();
            OnPropertyChanged(nameof(HasJobs));
            SaveCommand.NotifyCanExecuteChanged();

            // Seeds RunSchedulerEnabled from the saved preference. If this differs from the
            // property's current (constructor-default false) value, OnRunSchedulerEnabledChanged
            // fires once and reconciles the controller — harmless when it's already running
            // (AppSchedulerController.StartAsync no-ops), matching SessionsViewModel.InitializeAsync's
            // identical re-save-on-seed pattern for IsPaneCollapsed/ShowSubagentRuns.
            RunSchedulerEnabled = preferencesStore.Load().RunSchedulerInApp;
            RefreshSchedulerStatus();
            await LoadHistoryAsync(ct);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        if (IsHistoryLoading)
            return;

        IsHistoryLoading = true;
        try
        {
            var records = await runStore.ListRecentScheduledAsync(RecentHistoryLimit, ct);
            RunHistory.Clear();
            foreach (var record in records)
                RunHistory.Add(new ScheduledRunItemViewModel(record));

            HistoryStatusIsError = false;
            HistoryStatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasRunHistory));
            OnPropertyChanged(nameof(HistorySummaryText));
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    [RelayCommand]
    private void AddJob()
    {
        var job = new ScheduleItemViewModel(OnJobChanged) { Name = NextDefaultName() };
        Jobs.Add(job);
        SelectedJob = job;
        OnPropertyChanged(nameof(HasJobs));
        RecomputeJob(job);
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called by the page after the user confirms a delete dialog — never directly from XAML.</summary>
    [RelayCommand]
    private void RemoveSelectedJob()
    {
        if (SelectedJob is not { } job)
            return;

        Jobs.Remove(job);
        SelectedJob = Jobs.FirstOrDefault();
        OnPropertyChanged(nameof(HasJobs));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var options = Jobs.Select(job => job.ToOptions()).ToList();
        var result = await configWriter.SaveSchedulesAsync(options, CancellationToken.None);
        StatusIsError = !result.Success;
        StatusMessage = result.Success ? "Saved." : result.Error ?? "Save failed.";
        if (result.Success)
        {
            foreach (var job in Jobs)
                RecomputeJob(job);
            RefreshSchedulerStatus();
        }
    }

    private bool CanSave()
    {
        if (Jobs.Count == 0)
            return true;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name) ||
                string.IsNullOrWhiteSpace(job.Cron) ||
                string.IsNullOrWhiteSpace(job.Prompt))
            {
                return false;
            }

            if (!names.Add(job.Name.Trim()))
                return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task DiscardAsync(CancellationToken ct)
    {
        StatusMessage = string.Empty;
        StatusIsError = false;
        await LoadAsync(ct);
    }

    [RelayCommand]
    private async Task RunNowAsync(ScheduleItemViewModel? job)
    {
        if (job is null || job.IsRunning)
            return;

        job.IsRunning = true;
        job.RunOutcomeIsError = false;
        job.RunOutcomeText = "Running…";
        try
        {
            var options = job.ToOptions();
            var outcome = await Task.Run(
                () => scheduleRunner.RunJobAsync(options, concurrencyGate: null, onEvent: null, CancellationToken.None));
            job.RunOutcomeIsError = !outcome.Skipped && outcome.Error is not null;
            job.RunOutcomeText = FormatOutcome(job.Name, outcome);
        }
        finally
        {
            job.IsRunning = false;
            RecomputeJob(job);
            await sessions.RefreshAsync(CancellationToken.None);
            await LoadHistoryAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task OpenHistorySessionAsync(ScheduledRunItemViewModel? run)
    {
        if (run is null)
            return;

        await chatSessionController.SelectSessionAsync(run.SessionId, CancellationToken.None);
        navigation?.Navigate(AppRoute.Chat);
    }

    [RelayCommand]
    private async Task ResumeHistoryRunAsync(ScheduledRunItemViewModel? run)
    {
        if (run is null || run.IsResuming || !run.CanResume)
            return;

        run.IsResuming = true;
        HistoryStatusIsError = false;
        HistoryStatusMessage = $"Resuming {run.ScheduleName}…";
        ConversationRunResult? result = null;
        try
        {
            result = await Task.Run(
                () => orchestrator.ResumeAsync(run.RunId, onEvent: null, CancellationToken.None));
            await sessions.RefreshAsync(CancellationToken.None);
        }
        finally
        {
            run.IsResuming = false;
            await LoadHistoryAsync(CancellationToken.None);
        }

        if (result is not null)
        {
            HistoryStatusIsError = result.Error is not null;
            HistoryStatusMessage = FormatResumeOutcome(run.ScheduleName, result);
        }
    }

    // internal (not private): SchedulesViewModelTests exercises the Skipped-outcome formatting in
    // isolation, in addition to (and cheaper/more deterministic than) racing two real overlapping
    // ScheduleJobRunner.RunJobAsync calls end to end — that race is already covered thoroughly by
    // Caliper.Core.Tests.Scheduling.ScheduleJobRunnerTests.
    internal static string FormatOutcome(string jobName, ScheduleRunOutcome outcome)
    {
        if (outcome.Skipped)
            return "Skipped — a previous occurrence of this job is still running.";

        var parts = new List<string>
        {
            outcome.Error is { } error ? $"Job error: {error}" : $"Finished: {outcome.Reason?.ToString() ?? "?"}.",
        };
        if (outcome.DenialCount > 0)
            parts.Add($"{outcome.DenialCount} action(s) denied (unattended policy).");
        parts.Add($"Transcript saved to session '[job] {jobName}' — open it from Chat.");
        return string.Join(" ", parts);
    }

    internal static string FormatResumeOutcome(string jobName, ConversationRunResult result)
    {
        var parts = new List<string>
        {
            result.Error is { } error
                ? $"Could not resume {jobName}: {error}"
                : $"{jobName} finished: {result.Reason?.ToString() ?? "unknown reason"}.",
        };
        if (result.Denials.Count > 0)
            parts.Add($"{result.Denials.Count} action(s) denied (unattended policy).");
        parts.Add("Open its chat to review the transcript.");
        return string.Join(" ", parts);
    }

    private async Task ApplySchedulerToggleAsync(bool enable)
    {
        if (enable)
            await schedulerController.StartAsync(CancellationToken.None);
        else
            await schedulerController.StopAsync();

        preferencesStore.Save(preferencesStore.Load() with { RunSchedulerInApp = enable });
        RefreshSchedulerStatus();
    }

    private void RefreshSchedulerStatus()
    {
        IsSchedulerRunning = schedulerController.IsRunning;
        var enabledCount = Jobs.Count(job => job.Enabled);
        SchedulerStatusText = IsSchedulerRunning
            ? $"Scheduler active — {enabledCount} enabled schedule{(enabledCount == 1 ? "" : "s")}."
            : "Scheduler is off. Jobs only fire on their cron schedule while this toggle is on and the app stays open.";
    }

    private string NextDefaultName()
    {
        var index = Jobs.Count + 1;
        string candidate;
        do
        {
            candidate = $"schedule-{index}";
            index++;
        }
        while (Jobs.Any(job => string.Equals(job.Name, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }

    private void OnJobChanged(ScheduleItemViewModel job)
    {
        RecomputeJob(job);
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeJob(ScheduleItemViewModel job)
    {
        var options = job.ToOptions();
        var now = timeProvider.GetUtcNow();
        var occurrence = ScheduleCron.GetNextOccurrence(options, now, out var error);

        // Inline validation shown under the Cron box — always live regardless of Enabled, so a
        // schedule can be authored/validated before it's switched on.
        job.CronIsValid = error is null;
        job.CronValidationText = error is not null
            ? error
            : occurrence is { } when
                ? $"Next occurrence: {when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
                : "This cron expression never fires again.";

        // List column mirrors the console's /schedule list exactly (Program.cs RenderScheduleList).
        job.NextOccurrenceText = !job.Enabled
            ? "—"
            : occurrence is { } wakeAt
                ? wakeAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                : error is null ? "never" : $"invalid — {error}";

        var last = scheduleRunner.GetLastResult(job.Name);
        job.LastResultText = last is null
            ? "—"
            : $"{(last.Reason?.ToString() ?? (last.Error is null ? "?" : "Error"))}, " +
              $"{last.DenialCount} denial(s) at {last.CompletedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

        if (string.IsNullOrWhiteSpace(job.Name))
            job.ValidationMessage = "Name is required.";
        else if (string.IsNullOrWhiteSpace(job.Cron))
            job.ValidationMessage = "Cron is required.";
        else if (string.IsNullOrWhiteSpace(job.Prompt))
            job.ValidationMessage = "Prompt is required.";
        else if (!job.CronIsValid)
            job.ValidationMessage = error ?? "Invalid cron expression.";
        else if (IsDuplicateName(job))
            job.ValidationMessage = $"Another schedule is already named '{job.Name.Trim()}'.";
        else
            job.ValidationMessage = string.Empty;
    }

    private bool IsDuplicateName(ScheduleItemViewModel job) =>
        Jobs.Count(other => string.Equals(other.Name.Trim(), job.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;
}

public sealed partial class ScheduledRunItemViewModel : ObservableObject
{
    public ScheduledRunItemViewModel(RunRecord record)
    {
        RunId = record.RunId;
        SessionId = record.SessionId;
        ScheduleName = record.JobName
            ?? throw new ArgumentException("Scheduled history rows require a job name.", nameof(record));
        Status = record.Status;
        StatusText = record.Resumed ? $"{record.Status} (resumed)" : record.Status.ToString();
        ProgressText = $"{record.Step} of {record.MaxSteps} steps";
        UpdatedAtText = record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Reason = record.Reason ?? string.Empty;
        CanResume = record.Status == RunStatus.Interrupted;
        AccessibleName = $"{StatusText} run for {ScheduleName}, {ProgressText}, updated {UpdatedAtText}";
    }

    public string RunId { get; }
    public string SessionId { get; }
    public string ScheduleName { get; }
    public RunStatus Status { get; }
    public string StatusText { get; }
    public string ProgressText { get; }
    public string UpdatedAtText { get; }
    public string Reason { get; }
    public bool CanResume { get; }
    public string AccessibleName { get; }

    [ObservableProperty]
    public partial bool IsResuming { get; set; }
}

/// <summary>
/// One schedule's editable form state (list row + detail pane). <see cref="ToOptions"/>/
/// <see cref="FromOptions"/> round-trip against <see cref="ScheduleOptions"/>; every "empty means
/// inherit" nullable field (WorkingRoot, Model, MaxSteps, Permissions) maps an empty/default UI
/// value to null. The <paramref name="onChanged"/> callback (owned by <see cref="SchedulesViewModel"/>)
/// re-derives this job's live next-occurrence/validation text on every edit that could affect it —
/// mirrors <c>SessionItemViewModel</c>'s callback-based construction rather than a page-level event.
/// </summary>
public sealed partial class ScheduleItemViewModel(Action<ScheduleItemViewModel> onChanged) : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Cron { get; set; } = string.Empty;
    [ObservableProperty] public partial string TimeZone { get; set; } = ScheduleCron.LocalTimeZone;
    [ObservableProperty] public partial string Prompt { get; set; } = string.Empty;
    [ObservableProperty] public partial string WorkingRoot { get; set; } = string.Empty;
    [ObservableProperty] public partial string Model { get; set; } = string.Empty;
    // NumberBox convention: NaN represents "no value" (empty box), mapped to a null MaxSteps.
    [ObservableProperty] public partial double MaxStepsValue { get; set; } = double.NaN;
    [ObservableProperty] public partial bool Enabled { get; set; } = true;

    [ObservableProperty] public partial bool OverridePermissions { get; set; }
    // String (not PermissionMode) so the detail ComboBox can use plain <x:String> items in XAML —
    // mirrors McpServerSettingViewModel.Type's identical string-backed ComboBox pattern.
    [ObservableProperty] public partial string PermissionsModeText { get; set; } = nameof(PermissionMode.Auto);
    [ObservableProperty] public partial string ShellAutoAllowlistText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AutoAllowFileRootsText { get; set; } = string.Empty;

    [ObservableProperty] public partial string NextOccurrenceText { get; set; } = "—";
    [ObservableProperty] public partial string LastResultText { get; set; } = "—";
    [ObservableProperty] public partial string CronValidationText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CronIsValid { get; set; } = true;
    [ObservableProperty] public partial string ValidationMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string RunOutcomeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool RunOutcomeIsError { get; set; }

    public string EnabledStatusText => Enabled ? "Enabled" : "Disabled";

    // A dedicated bool (rather than binding Visibility straight to a HasText(string) function
    // call) — the WinUI x:Bind compiler mis-generates the Update_* method for a function call fed
    // into an implicit bool->Visibility conversion in this DataTemplate shape (CS0103 on the
    // generated code's synthetic parameter name). A plain bool property sidesteps it entirely and
    // matches every other BoolToVisibility(...) binding already used across this app.
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    partial void OnNameChanged(string value) => onChanged(this);
    partial void OnCronChanged(string value) => onChanged(this);
    partial void OnTimeZoneChanged(string value) => onChanged(this);
    partial void OnPromptChanged(string value) => onChanged(this);
    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EnabledStatusText));
        onChanged(this);
    }

    public ScheduleOptions ToOptions() => new()
    {
        Name = Name.Trim(),
        Cron = Cron.Trim(),
        TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? ScheduleCron.LocalTimeZone : TimeZone.Trim(),
        Prompt = Prompt,
        WorkingRoot = string.IsNullOrWhiteSpace(WorkingRoot) ? null : WorkingRoot.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
        MaxSteps = double.IsNaN(MaxStepsValue) ? null : (int)MaxStepsValue,
        Enabled = Enabled,
        Permissions = OverridePermissions
            ? new PermissionsOptions
            {
                Mode = Enum.TryParse<PermissionMode>(PermissionsModeText, out var mode) ? mode : PermissionMode.Auto,
                ShellAutoAllowlist = ParseLines(ShellAutoAllowlistText),
                AutoAllowFileRoots = ParseLines(AutoAllowFileRootsText),
            }
            : null,
    };

    public static ScheduleItemViewModel FromOptions(ScheduleOptions options, Action<ScheduleItemViewModel> onChanged)
    {
        var item = new ScheduleItemViewModel(onChanged)
        {
            Name = options.Name,
            Cron = options.Cron,
            TimeZone = options.TimeZone,
            Prompt = options.Prompt,
            WorkingRoot = options.WorkingRoot ?? string.Empty,
            Model = options.Model ?? string.Empty,
            MaxStepsValue = options.MaxSteps ?? double.NaN,
            Enabled = options.Enabled,
        };

        if (options.Permissions is { } overlay)
        {
            item.OverridePermissions = true;
            item.PermissionsModeText = overlay.Mode.ToString();
            item.ShellAutoAllowlistText = string.Join(Environment.NewLine, overlay.ShellAutoAllowlist);
            item.AutoAllowFileRootsText = string.Join(Environment.NewLine, overlay.AutoAllowFileRoots);
        }

        return item;
    }

    private static string[] ParseLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
