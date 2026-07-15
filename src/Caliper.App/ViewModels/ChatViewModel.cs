// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.ComponentModel;
using Caliper.App.Permissions;
using Caliper.Core.Abstractions;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IChatSessionController, IDisposable
{
    private static readonly TimeSpan StreamingFlushInterval = TimeSpan.FromMilliseconds(80);
    private const int TranscriptCacheLimit = 20;
    private readonly IAgentRunner _runner;
    private readonly ISessionStore _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly ApprovalService _approvals;
    private readonly IPermissionGate _permissionGate;
    private readonly IConversationOrchestrator _conversations;
    private readonly IRuntimeSettings _runtimeSettings;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, ObservableCollection<ChatItemViewModel>> _transcripts =
        new(StringComparer.Ordinal);
    // Token usage is per session: while a run streams in A and the user views B, B's footer must
    // not show A's cumulative counts.
    private readonly Dictionary<string, string> _usageBySession = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentTranscripts = [];
    private CancellationTokenSource? _runCancellation;
    private ObservableCollection<ChatItemViewModel>? _runningMessages;
    private string? _currentSessionId;
    private string? _runningSessionId;
    private string? _queuedSessionId;

    public ChatViewModel(
        IAgentRunner runner,
        ISessionStore sessions,
        TimeProvider timeProvider,
        ApprovalService approvals,
        IPermissionGate permissionGate,
        IConversationOrchestrator conversations,
        IRuntimeSettings runtimeSettings,
        IUiDispatcher dispatcher)
    {
        _runner = runner;
        _sessions = sessions;
        _timeProvider = timeProvider;
        _approvals = approvals;
        _permissionGate = permissionGate;
        _conversations = conversations;
        _runtimeSettings = runtimeSettings;
        _dispatcher = dispatcher;
        _approvals.ApprovalRequested += Approvals_ApprovalRequested;
        _runtimeSettings.SettingsChanged += RuntimeSettings_SettingsChanged;
    }

    private void RuntimeSettings_SettingsChanged(object? sender, EventArgs e)
    {
        // The change may arrive on a background thread (e.g. after a ConfigureAwait(false) save).
        void Refresh()
        {
            OnPropertyChanged(nameof(RuntimeSummary));
            OnPropertyChanged(nameof(PermissionModeText));
        }

        if (_dispatcher.HasThreadAccess)
            Refresh();
        else
            _dispatcher.TryEnqueue(Refresh);
    }

    [ObservableProperty]
    public partial ObservableCollection<ChatItemViewModel> Messages { get; set; } = [];

    public bool HasMessages => Messages.Count > 0;

    [ObservableProperty]
    public partial ToolCallViewModel? SelectedTool { get; set; }

    public bool HasSelectedTool => SelectedTool is not null;
    public bool SelectedToolHasDiff => SelectedTool?.HasDiff == true;
    public string SelectedToolName => SelectedTool?.ToolName ?? "Details";
    public string SelectedToolStatus => SelectedTool?.Status ?? string.Empty;
    public string SelectedToolOutput => SelectedTool?.Output ?? string.Empty;
    public string SelectedToolArguments => SelectedTool?.ArgumentsPretty ?? string.Empty;
    public bool SelectedToolHasArguments => SelectedTool?.HasArguments == true;
    public string SelectedDiffPath => SelectedTool?.Diff?.Path ?? string.Empty;
    public bool IsSelectedDiffTruncated => SelectedTool?.Diff?.IsTruncated == true;
    public IReadOnlyList<SideBySideDiffRowViewModel> SelectedSideBySideRows =>
        SelectedTool?.Diff?.SideBySideRows ?? [];
    public IReadOnlyList<InlineDiffRowViewModel> SelectedInlineRows =>
        SelectedTool?.Diff?.InlineRows ?? [];

    public string? CurrentSessionId => _currentSessionId;
    public string? RunningSessionId => _runningSessionId;
    public event EventHandler? TranscriptChanged;
    public event EventHandler? RunActivityChanged;
    public event EventHandler<SessionCreatedEventArgs>? SessionCreated;
    public event EventHandler<SessionRenamedEventArgs>? SessionRenamed;

    [ObservableProperty]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? QueuedMessage { get; set; }

    public bool HasQueuedMessage => QueuedMessage is not null;

    public string QueuedMessagePreview =>
        QueuedMessage is { } queued ? $"Queued: {queued.ReplaceLineEndings(" ")}" : string.Empty;

    [ObservableProperty]
    public partial ApprovalViewModel? PendingApproval { get; set; }

    public bool HasPendingApproval => PendingApproval is not null;

    [ObservableProperty]
    public partial ChatRunStatus RunStatus { get; set; } = ChatRunStatus.Ready;

    public string StatusText => RunStatus.ToDisplayText();

    [ObservableProperty]
    public partial string UsageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsContextBusy { get; set; }

    [ObservableProperty]
    public partial string ContextStatusMessage { get; set; } = string.Empty;

    public string RuntimeSummary => $"{_runtimeSettings.Caliper.Provider} · {_runtimeSettings.Caliper.Model}";
    public string PermissionModeText => _runtimeSettings.Permissions.Mode.ToString();

    partial void OnMessagesChanged(ObservableCollection<ChatItemViewModel> value) =>
        OnPropertyChanged(nameof(HasMessages));

    partial void OnInputTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        QueueMessageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        QueueMessageCommand.NotifyCanExecuteChanged();
        RefreshContextCommandState();
        RunActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnQueuedMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasQueuedMessage));
        OnPropertyChanged(nameof(QueuedMessagePreview));
    }

    partial void OnPendingApprovalChanged(ApprovalViewModel? value) => OnPropertyChanged(nameof(HasPendingApproval));

    partial void OnRunStatusChanged(ChatRunStatus value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSelectedToolChanged(ToolCallViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedTool));
        OnPropertyChanged(nameof(SelectedToolHasDiff));
        OnPropertyChanged(nameof(SelectedToolName));
        OnPropertyChanged(nameof(SelectedToolStatus));
        OnPropertyChanged(nameof(SelectedToolOutput));
        OnPropertyChanged(nameof(SelectedToolArguments));
        OnPropertyChanged(nameof(SelectedToolHasArguments));
        OnPropertyChanged(nameof(SelectedDiffPath));
        OnPropertyChanged(nameof(IsSelectedDiffTruncated));
        OnPropertyChanged(nameof(SelectedSideBySideRows));
        OnPropertyChanged(nameof(SelectedInlineRows));
    }

    public void SelectTool(ToolCallViewModel tool) => SelectedTool = tool;
    public void ClearSelectedTool() => SelectedTool = null;

    public async Task SelectSessionAsync(string sessionId, CancellationToken ct)
    {
        if (!_transcripts.TryGetValue(sessionId, out var transcript))
        {
            transcript = PersistedTranscriptFactory.Create(await _sessions.LoadAsync(sessionId, ct));
            _transcripts[sessionId] = transcript;
        }

        // Approvals are scoped per session by the gate, so switching sessions must NOT reset them —
        // doing so would silently revoke the "allow for session" grants of a run still going in the
        // session we're switching away from.
        _currentSessionId = sessionId;
        Messages = transcript;
        SelectedTool = null;
        UsageText = _usageBySession.GetValueOrDefault(sessionId, string.Empty);
        TouchTranscript(sessionId);
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ReloadCurrentSessionAsync(CancellationToken ct)
    {
        if (_currentSessionId is not { } sessionId)
            return;

        _transcripts.Remove(sessionId);
        await SelectSessionAsync(sessionId, ct);
    }

    public bool CanDeleteSession(string sessionId) =>
        !string.Equals(_runningSessionId, sessionId, StringComparison.Ordinal);

    public void RemoveSession(string sessionId)
    {
        // A deleted session's approvals should not linger in the gate.
        _permissionGate.ResetSessionApprovals(sessionId);
        _transcripts.Remove(sessionId);
        _usageBySession.Remove(sessionId);
        _recentTranscripts.Remove(sessionId);
        if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
            ClearSessionSelection();
    }

    public void ClearSessionSelection()
    {
        // Clear only the current session's approvals (if any) — never a run active in another.
        if (_currentSessionId is { } sessionId)
            _permissionGate.ResetSessionApprovals(sessionId);
        _currentSessionId = null;
        Messages = [];
        SelectedTool = null;
        UsageText = string.Empty;
    }

    private bool CanSend() => !IsRunning && !string.IsNullOrWhiteSpace(InputText);

    private bool CanQueue() => IsRunning && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanQueue))]
    private void QueueMessage()
    {
        QueuedMessage = InputText.Trim();
        // Remember which session this was queued in so we don't later post it into a different
        // transcript the user has since switched to.
        _queuedSessionId = _currentSessionId;
        InputText = string.Empty;
    }

    [RelayCommand]
    private void EditQueuedMessage()
    {
        if (QueuedMessage is not { } queued)
            return;

        InputText = queued;
        QueuedMessage = null;
        _queuedSessionId = null;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var prompt = InputText.Trim();
        InputText = string.Empty;
        var sessionId = await EnsureSessionAsync();
        var runMessages = _transcripts[sessionId];
        var isFirstMessage = runMessages.Count == 0;
        var mapper = new AgentEventMapper(runMessages, _timeProvider);

        runMessages.Add(new UserMessageViewModel(prompt));
        NotifyTranscriptChanged(runMessages);
        if (isFirstMessage)
            await AutoTitleSessionAsync(sessionId, prompt);
        mapper.ResetForRun();
        _runCancellation = new CancellationTokenSource();
        _runningSessionId = sessionId;
        _runningMessages = runMessages;
        IsRunning = true;
        RunStatus = ChatRunStatus.Running;
        using var streamingFlushCancellation = new CancellationTokenSource();
        var streamingFlushTask = FlushStreamingAsync(
            mapper,
            runMessages,
            streamingFlushCancellation.Token);

        try
        {
            await foreach (var evt in _runner.RunAsync(sessionId, prompt, _runCancellation.Token))
            {
                var transcriptChanged = mapper.Map(evt);
                if (evt is UsageReported)
                {
                    var usage = FormatUsage(mapper.PromptTokens, mapper.CompletionTokens);
                    _usageBySession[sessionId] = usage;
                    // Only reflect it in the footer if the user is still looking at this session.
                    if (string.Equals(_currentSessionId, sessionId, StringComparison.Ordinal))
                        UsageText = usage;
                }

                if (evt is PermissionResolved resolved)
                    _approvals.Resolve(resolved.Tool, resolved.Decision, resolved.RequestId);
                else if (evt is RunCompleted completed)
                    RunStatus = ChatRunStatusExtensions.FromCompletion(completed.Reason);
                else if (evt is RunFailed)
                    RunStatus = ChatRunStatus.Failed;

                if (transcriptChanged)
                    NotifyTranscriptChanged(runMessages);
            }
        }
        catch (OperationCanceledException)
        {
            RunStatus = ChatRunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            // A11: top-level resilience boundary by design — this spans the entire orchestrator run
            // (model client, every enabled tool, permission gate, persistence), so the realistic
            // failure set is intentionally not enumerable; any exception here must fold into a
            // graceful RunFailed transcript entry rather than crash the chat UI.
            _ = mapper.Map(new RunFailed($"Unexpected error: {ex.Message}"));
            NotifyTranscriptChanged(runMessages);
            RunStatus = ChatRunStatus.Failed;
        }
        finally
        {
            streamingFlushCancellation.Cancel();
            await streamingFlushTask;
            if (mapper.FlushStreamingMessage())
                NotifyTranscriptChanged(runMessages);
            _runCancellation.Dispose();
            _runCancellation = null;
            _runningSessionId = null;
            _runningMessages = null;
            IsRunning = false;
            if (RunStatus is ChatRunStatus.Running or ChatRunStatus.Stopping)
                RunStatus = mapper.LastCompletionReason is { } reason
                    ? ChatRunStatusExtensions.FromCompletion(reason)
                    : mapper.Failure is not null
                        ? ChatRunStatus.Failed
                        : ChatRunStatus.Ready;

            if (QueuedMessage is { } queued)
            {
                QueuedMessage = null;
                var queuedFor = _queuedSessionId;
                _queuedSessionId = null;
                InputText = queued;
                // Only auto-send if the user is still on the session the message was queued in.
                // Otherwise leave it in the composer rather than delivering it to a different
                // session. Awaited (not fire-and-forget) so a re-send failure isn't silently lost.
                if (string.Equals(_currentSessionId, queuedFor, StringComparison.Ordinal))
                    await SendAsync();
            }
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        RunStatus = ChatRunStatus.Stopping;
        _runCancellation?.Cancel();
    }

    private bool CanMutateContext() => !IsRunning && !IsContextBusy;

    public void RefreshContextCommandState()
    {
        CompactCommand.NotifyCanExecuteChanged();
        ClearContextCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsContextBusyChanged(bool value) => RefreshContextCommandState();

    [RelayCommand(CanExecute = nameof(CanMutateContext))]
    private async Task CompactAsync()
    {
        if (IsContextBusy)
            return;

        IsContextBusy = true;
        try
        {
            if (_currentSessionId is not { } sessionId)
            {
                ContextStatusMessage = "Select a session before compacting context.";
                return;
            }

            var fit = await _conversations.ForceCompactAsync(sessionId, CancellationToken.None);
            await ReloadCurrentSessionAsync(CancellationToken.None);
            ContextStatusMessage = fit.Compacted
                ? $"Context compacted from {fit.BeforeTokens ?? 0:N0} to {fit.AfterTokens ?? 0:N0} tokens."
                : "The current context did not need compaction.";
        }
        finally
        {
            IsContextBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateContext))]
    private async Task ClearContextAsync()
    {
        if (IsContextBusy)
            return;

        IsContextBusy = true;
        try
        {
            if (_currentSessionId is not { } sessionId)
            {
                ContextStatusMessage = "Select a session before clearing context.";
                return;
            }

            await _sessions.AppendAsync(
                sessionId,
                new ChatMessage(ChatRole.System, MessageKind.Summary, Caliper.Core.Agents.AgentRunner.ContextResetMarker),
                CancellationToken.None);
            await ReloadCurrentSessionAsync(CancellationToken.None);
            ContextStatusMessage = "Context cleared; transcript history was kept.";
        }
        finally
        {
            IsContextBusy = false;
        }
    }

    private async Task<string> EnsureSessionAsync()
    {
        if (_currentSessionId is not null)
            return _currentSessionId;

        var summary = await _sessions.CreateWithSummaryAsync("New session", CancellationToken.None);
        var sessionId = summary.Id;
        _currentSessionId = sessionId;
        _transcripts[sessionId] = [];
        TouchTranscript(sessionId);
        Messages = _transcripts[sessionId];
        SessionCreated?.Invoke(this, new SessionCreatedEventArgs(summary));
        return sessionId;
    }

    private async Task AutoTitleSessionAsync(string sessionId, string prompt)
    {
        var title = AutoTitle(prompt);
        if (string.IsNullOrEmpty(title))
            return;

        await _sessions.RenameAsync(sessionId, title, CancellationToken.None);
        SessionRenamed?.Invoke(this, new SessionRenamedEventArgs(sessionId, title));
    }

    private static string AutoTitle(string prompt) => Caliper.Core.SessionTitle.FromPrompt(prompt);

    private static string FormatUsage(int? cumulativePrompt, int? cumulativeCompletion) =>
        cumulativePrompt is null && cumulativeCompletion is null
            ? string.Empty
            : $"Total prompt: {cumulativePrompt ?? 0:N0}  Total completion: {cumulativeCompletion ?? 0:N0}";

    private async Task FlushStreamingAsync(
        AgentEventMapper mapper,
        ObservableCollection<ChatItemViewModel> runMessages,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StreamingFlushInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (mapper.FlushStreamingMessage())
                    NotifyTranscriptChanged(runMessages);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Approvals_ApprovalRequested(object? sender, ApprovalRequestedEventArgs e)
    {
        var target = _runningMessages ?? Messages;
        target.Add(e.Approval);
        NotifyTranscriptChanged(target);
        PendingApproval = e.Approval;
        e.Approval.PropertyChanged += Approval_PropertyChanged;
    }

    private void Approval_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApprovalViewModel.IsPending) ||
            sender is not ApprovalViewModel { IsPending: false } approval)
        {
            return;
        }

        approval.PropertyChanged -= Approval_PropertyChanged;
        if (ReferenceEquals(PendingApproval, approval))
            PendingApproval = null;
    }

    private void NotifyTranscriptChanged(ObservableCollection<ChatItemViewModel> transcript)
    {
        if (ReferenceEquals(Messages, transcript))
        {
            OnPropertyChanged(nameof(HasMessages));
            TranscriptChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TouchTranscript(string sessionId)
    {
        _recentTranscripts.Remove(sessionId);
        _recentTranscripts.AddFirst(sessionId);
        while (_recentTranscripts.Count > TranscriptCacheLimit)
        {
            var candidate = _recentTranscripts.Last;
            while (candidate is not null &&
                   (string.Equals(candidate.Value, _currentSessionId, StringComparison.Ordinal) ||
                    string.Equals(candidate.Value, _runningSessionId, StringComparison.Ordinal)))
            {
                candidate = candidate.Previous;
            }

            if (candidate is null)
                break;

            _recentTranscripts.Remove(candidate);
            _transcripts.Remove(candidate.Value);
        }
    }

    public void Dispose()
    {
        _approvals.ApprovalRequested -= Approvals_ApprovalRequested;
        _runtimeSettings.SettingsChanged -= RuntimeSettings_SettingsChanged;
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = null;
    }
}
