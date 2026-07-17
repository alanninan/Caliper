// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Globalization;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

/// <summary>
/// P3: the App's parity surface for the console's <c>/runs</c> and <c>--resume &lt;run-id&gt;</c>.
/// Lists the durable-run rows tracked by <see cref="IRunStore"/> (see its doc comment for exactly
/// which runs that is — one-shot, <c>--unattended</c>, scheduled, and subagent runs; not
/// interactive chat turns) and, for any row left <see cref="RunStatus.Interrupted"/> by the
/// startup sweep, offers a "Resume" action that drives
/// <see cref="IConversationOrchestrator"/>.<c>ResumeAsync</c> on a background thread — mirrors
/// <see cref="SchedulesViewModel"/>'s RunNowAsync shape (per-row in-flight guard, outcome text,
/// then a <see cref="SessionsViewModel"/> refresh).
/// </summary>
public sealed partial class RunsViewModel(
    IRunStore runStore,
    IConversationOrchestrator orchestrator,
    IChatSessionController chatSessionController,
    SessionsViewModel sessions) : ObservableObject
{
    private const int RecentLimit = 20;

    public ObservableCollection<RunItemViewModel> Runs { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ResumeOutcomeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ResumeOutcomeIsError { get; set; }

    // Roadmap item 3 (startup sweep surfacing): populated both by LoadAsync (this page's own load)
    // and by CheckStartupInterruptionsAsync (fired once from MainPage at launch, independent of
    // whether the user ever opens this page) — either can flip the MainPage banner on.
    [ObservableProperty]
    public partial int InterruptedStartupCount { get; set; }

    // Dismissal is session-only (no persistence) — MainPage's InfoBar close button (or its "View
    // Runs" action) sets this true, and it never resets until the app restarts.
    [ObservableProperty]
    public partial bool StartupBannerDismissed { get; set; }

    public bool HasRuns => Runs.Count > 0;

    public bool ShowStartupBanner => InterruptedStartupCount > 0 && !StartupBannerDismissed;

    partial void OnInterruptedStartupCountChanged(int value) => OnPropertyChanged(nameof(ShowStartupBanner));

    partial void OnStartupBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowStartupBanner));

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var records = await runStore.ListRecentAsync(RecentLimit, ct);
            Runs.Clear();
            foreach (var record in records)
                Runs.Add(new RunItemViewModel(record));

            InterruptedStartupCount = records.Count(record => record.Status == RunStatus.Interrupted);
            OnPropertyChanged(nameof(HasRuns));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Called once from <c>MainPage</c> right after launch — not gated on the user ever navigating
    /// to this page — so a startup sweep that interrupted runs is surfaced immediately. Runs the
    /// store read off the UI thread; the caller (MainPage.OnNavigatedTo) wraps this in the app's
    /// usual top-level A11 try/catch boundary, so a store failure here can never crash startup.
    /// </summary>
    public async Task CheckStartupInterruptionsAsync(CancellationToken ct)
    {
        var records = await Task.Run(() => runStore.ListRecentAsync(RecentLimit, ct), ct);
        InterruptedStartupCount = records.Count(record => record.Status == RunStatus.Interrupted);
    }

    [RelayCommand]
    private void DismissStartupBanner() => StartupBannerDismissed = true;

    [RelayCommand]
    private async Task ResumeAsync(RunItemViewModel? run)
    {
        if (run is null || run.IsResuming || run.Status != RunStatus.Interrupted)
            return;

        run.IsResuming = true;
        ResumeOutcomeIsError = false;
        ResumeOutcomeText = "Resuming…";
        try
        {
            var result = await Task.Run(
                () => orchestrator.ResumeAsync(run.RunId, onEvent: null, CancellationToken.None));
            ResumeOutcomeIsError = result.Error is not null;
            ResumeOutcomeText = FormatOutcome(result, run.ShortSessionId);

            // Cheap v1 transcript freshness (deferred: live event streaming into an open transcript,
            // and cross-page navigation to Chat — same scope cut the Schedules page made for its own
            // "Run now"). Refresh the shared sessions pane so a resumed job/subagent session is
            // listed, and if the resumed run's session happens to be the one currently open in Chat,
            // re-select it so the appended resume note/continuation isn't stale until a manual reload.
            await sessions.RefreshAsync(CancellationToken.None);
            if (string.Equals(chatSessionController.CurrentSessionId, run.SessionId, StringComparison.Ordinal))
                await chatSessionController.SelectSessionAsync(run.SessionId, CancellationToken.None);
        }
        finally
        {
            run.IsResuming = false;
            // The row's status/step/reason are stale the instant resume finishes (it may no longer
            // even be Interrupted) — reload from the store rather than patching this row in place.
            await LoadAsync(CancellationToken.None);
        }
    }

    // internal: RunsViewModelTests exercises this formatting in isolation, matching
    // SchedulesViewModel.FormatOutcome's identical internal-for-testing convention.
    internal static string FormatOutcome(ConversationRunResult result, string shortSessionId)
    {
        var parts = new List<string>
        {
            result.Error is { } error ? $"Error: {error}" : $"Finished: {result.Reason?.ToString() ?? "?"}.",
        };
        if (result.Denials.Count > 0)
            parts.Add($"{result.Denials.Count} action(s) denied (unattended policy).");
        parts.Add($"Transcript is in session '{shortSessionId}' — open it from Chat.");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// One <see cref="RunRecord"/> projected for display — mirrors the console's
/// <c>RenderRunsListAsync</c> (Program.cs) row shape exactly (short run/session ids, "—" for a
/// null job name, "(resumed)" suffix, step/budget, localized updated time), plus the App-only
/// <see cref="IsResuming"/> in-flight flag for the per-row Resume button.
/// </summary>
public sealed partial class RunItemViewModel : ObservableObject
{
    public RunItemViewModel(RunRecord record)
    {
        RunId = record.RunId;
        SessionId = record.SessionId;
        Status = record.Status;
        ShortRunId = record.RunId[..Math.Min(8, record.RunId.Length)];
        ShortSessionId = record.SessionId[..Math.Min(8, record.SessionId.Length)];
        JobText = record.JobName ?? "—";
        StatusText = record.Resumed ? $"{record.Status} (resumed)" : record.Status.ToString();
        IsInterrupted = record.Status == RunStatus.Interrupted;
        CanResume = IsInterrupted;
        StepText = $"Step {record.Step}/{record.MaxSteps}";
        UpdatedAtText = record.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        Reason = record.Reason;
        HasReason = !string.IsNullOrWhiteSpace(record.Reason);

        var meta = $"Session {ShortSessionId}   ·   {StepText}   ·   {UpdatedAtText}";
        if (record.Unattended)
            meta += "   ·   unattended";
        MetaText = meta;
    }

    public string RunId { get; }
    public string SessionId { get; }
    public RunStatus Status { get; }
    public string ShortRunId { get; }
    public string ShortSessionId { get; }
    public string JobText { get; }
    public string StatusText { get; }
    public bool IsInterrupted { get; }
    public bool CanResume { get; }
    public string StepText { get; }
    public string UpdatedAtText { get; }
    public string? Reason { get; }
    public bool HasReason { get; }
    public string MetaText { get; }

    [ObservableProperty]
    public partial bool IsResuming { get; set; }
}
