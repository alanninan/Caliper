// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Caliper.App.Permissions;
using Caliper.App.Preferences;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IChatSessionController, IDisposable
{
    private static readonly TimeSpan StreamingFlushInterval = TimeSpan.FromMilliseconds(80);
    // Crash hardening (TO_FIX.md item 2): a MarkdownTextBlock fully re-parses and rebuilds its
    // visual tree on every Content change (U3), so pushing on every 80ms tick during a long stream
    // is the measure/arrange churn behind the ItemsRepeater layout-cycle crash. The tick loop below
    // still runs every StreamingFlushInterval (other flush work — reasoning elapsed-time, etc. —
    // keeps that cadence); this only throttles how often a bubble's Content is actually re-pushed,
    // via AgentEventMapper.FlushStreamingMessage(TimeSpan). A chunk crossing a structural boundary
    // (newline or a ``` fence) still pushes immediately so paragraph breaks and code fences render
    // promptly.
    private static readonly TimeSpan MinStreamingRenderInterval = TimeSpan.FromMilliseconds(250);
    private const int TranscriptCacheLimit = 20;
    private readonly IAgentRunner _runner;
    private readonly ISessionStore _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly ApprovalService _approvals;
    private readonly IPermissionGate _permissionGate;
    private readonly IConversationOrchestrator _conversations;
    private readonly IRuntimeSettings _runtimeSettings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISessionUsageStore _usageStore;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly IModelCatalog _modelCatalog;
    // U4: the quick-switcher's model catalog is fetched lazily on first flyout open, not at
    // construction — the same "don't hit the network until the UI actually needs it" reasoning
    // as ModelsProvidersSettingsViewModel's separate LoadModelsCommand.
    private bool _modelCatalogLoaded;
    private List<string> _quickModelIds = [];
    private readonly Dictionary<string, ObservableCollection<ChatItemViewModel>> _transcripts =
        new(StringComparer.Ordinal);
    // Token usage is per session: while a run streams in A and the user views B, B's footer must
    // not show A's cumulative counts.
    private readonly Dictionary<string, string> _usageBySession = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentTranscripts = [];
    // FIFO queue of unresolved approvals; the head is surfaced as PendingApproval on the docked
    // card, the rest wait behind it. ApprovalService supports multiple in-flight approvals
    // (subagent runs make that real), so later requests must never overwrite an earlier one.
    private readonly List<ApprovalViewModel> _pendingApprovals = [];
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
        IUiDispatcher dispatcher,
        ISessionUsageStore usageStore,
        IAppPreferencesStore preferencesStore,
        IModelCatalog modelCatalog)
    {
        _runner = runner;
        _sessions = sessions;
        _timeProvider = timeProvider;
        _approvals = approvals;
        _permissionGate = permissionGate;
        _conversations = conversations;
        _runtimeSettings = runtimeSettings;
        _dispatcher = dispatcher;
        _usageStore = usageStore;
        _preferencesStore = preferencesStore;
        _modelCatalog = modelCatalog;
        _approvals.ApprovalRequested += Approvals_ApprovalRequested;
        _runtimeSettings.SettingsChanged += RuntimeSettings_SettingsChanged;

        // Restore the token-usage footer across restarts: Core's session store persists no usage
        // data, so this is the only source for it. A stale entry for a since-deleted session is
        // harmless — it just never gets looked up by SelectSessionAsync.
        foreach (var (sessionId, usage) in usageStore.LoadAll())
            _usageBySession[sessionId] = FormatUsage(usage.PromptTokens, usage.CompletionTokens);

        // Mirrors SessionsViewModel's pane-collapsed persistence pattern for the inspector pane.
        IsInspectorCollapsed = preferencesStore.Load().InspectorPaneCollapsed;
    }

    private void RuntimeSettings_SettingsChanged(object? sender, EventArgs e)
    {
        // The change may arrive on a background thread (e.g. after a ConfigureAwait(false) save).
        void Refresh()
        {
            OnPropertyChanged(nameof(RuntimeSummary));
            OnPropertyChanged(nameof(PermissionModeText));
            OnPropertyChanged(nameof(IsAskAlwaysMode));
            OnPropertyChanged(nameof(IsAutoMode));
            OnPropertyChanged(nameof(IsPlanMode));
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

    public bool HasQueuedApprovals => _pendingApprovals.Count > 1;

    // The head of the queue is always position 1; the indicator only appears when something waits.
    public string PendingApprovalCountText =>
        _pendingApprovals.Count > 1 ? $"Approval 1 of {_pendingApprovals.Count}" : string.Empty;

    [ObservableProperty]
    public partial ChatRunStatus RunStatus { get; set; } = ChatRunStatus.Ready;

    public string StatusText => RunStatus.ToDisplayText();

    [ObservableProperty]
    public partial string UsageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsContextBusy { get; set; }

    [ObservableProperty]
    public partial string ContextStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInspectorCollapsed { get; set; }

    // U7: transcript search state. Matches are recomputed from scratch (never cached indexes)
    // whenever the query changes or Messages is reassigned (session switch) — see OnMessagesChanged.
    private readonly List<ChatItemViewModel> _searchMatches = [];
    private int _searchMatchIndex = -1;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchActive { get; set; }

    [ObservableProperty]
    public partial ChatItemViewModel? CurrentSearchMatch { get; set; }

    public string SearchMatchText { get; private set; } = string.Empty;

    public string RuntimeSummary => $"{_runtimeSettings.Caliper.Provider} · {_runtimeSettings.Caliper.Model}";
    public string PermissionModeText => _runtimeSettings.Permissions.Mode.ToString();

    // U4: header quick-switcher checked-state — refreshed alongside PermissionModeText in
    // RuntimeSettings_SettingsChanged, including for a change made outside the switcher itself
    // (e.g. the Permissions settings page).
    public bool IsAskAlwaysMode => _runtimeSettings.Permissions.Mode == PermissionMode.AskAlways;
    public bool IsAutoMode => _runtimeSettings.Permissions.Mode == PermissionMode.Auto;
    public bool IsPlanMode => _runtimeSettings.Permissions.Mode == PermissionMode.Plan;

    // U4: header model quick-switcher. Session-scoped by design (SetModel writes only the live
    // runtimeSettings clone) — the Models & providers settings page remains the persistent path.
    [ObservableProperty]
    public partial string ModelSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QuickModelHintText { get; set; } = "Session only — persist in Settings.";

    public ObservableCollection<string> FilteredQuickModels { get; } = [];

    partial void OnModelSearchTextChanged(string value) => FilterQuickModels(value);

    private void FilterQuickModels(string? query)
    {
        FilteredQuickModels.Clear();
        foreach (var id in _quickModelIds
                     .Where(id => string.IsNullOrWhiteSpace(query) || id.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .Take(20))
        {
            FilteredQuickModels.Add(id);
        }
    }

    // Lazy, once-per-session-of-the-page fetch: called from the quick-switcher flyout's Opened
    // event, not eagerly at construction. The catalog call hits a live, provider-selected network
    // endpoint — same unenumerable failure surface as
    // ModelsProvidersSettingsViewModel.LoadModelsAsync (A11) — so a failure must degrade to an
    // empty list and surface the error as the hint text rather than throw out of a flyout-open
    // handler.
    public async Task LoadModelCatalogAsync()
    {
        if (_modelCatalogLoaded)
            return;

        _modelCatalogLoaded = true;
        try
        {
            var entries = await _modelCatalog.ListAsync(CancellationToken.None);
            _quickModelIds = [.. entries.Select(static entry => entry.Id).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)];
            FilterQuickModels(ModelSearchText);
        }
        catch (Exception ex)
        {
            QuickModelHintText = ex.Message;
        }
    }

    [RelayCommand]
    private void SetPermissionMode(string mode)
    {
        if (Enum.TryParse<PermissionMode>(mode, ignoreCase: true, out var parsed))
            _runtimeSettings.SetPermissionMode(parsed);
    }

    [RelayCommand]
    private void ApplyModel(string modelId)
    {
        var trimmed = modelId?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
            _runtimeSettings.SetModel(trimmed);
    }

    partial void OnMessagesChanged(ObservableCollection<ChatItemViewModel> value)
    {
        OnPropertyChanged(nameof(HasMessages));
        // U7 PITFALL: Messages is reassigned wholesale on session switch (SelectSessionAsync,
        // EnsureSessionAsync, ClearSessionSelection) rather than mutated in place, so an active
        // search must recompute against the newly assigned collection rather than keep matches
        // pointing at items from the session just left.
        if (IsSearchActive)
            RecomputeSearchMatches();
    }

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

    partial void OnPendingApprovalChanged(ApprovalViewModel? value)
    {
        OnPropertyChanged(nameof(HasPendingApproval));
        NotifyApprovalQueueChanged();
    }

    partial void OnRunStatusChanged(ChatRunStatus value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsInspectorCollapsedChanged(bool value) =>
        _preferencesStore.Save(_preferencesStore.Load() with { InspectorPaneCollapsed = value });

    partial void OnSearchQueryChanged(string value) => RecomputeSearchMatches();

    // Closing the search box (Esc, the close button) resets all search state, not just visibility.
    partial void OnIsSearchActiveChanged(bool value)
    {
        if (!value)
        {
            SearchQuery = string.Empty;
            RecomputeSearchMatches();
        }
    }

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
        _usageStore.Remove(sessionId);
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

    // U7: pure/testable plain-text export of the current transcript for the "Copy conversation"
    // toolbar button — reasoning is skipped (it's a secondary/collapsible detail, not part of the
    // conversation itself); tool activity and status/compaction markers each collapse to one line.
    public string BuildTranscriptText()
    {
        var builder = new StringBuilder();
        foreach (var item in Messages)
        {
            switch (item)
            {
                case UserMessageViewModel user:
                    builder.Append("You:").Append('\n').Append(user.Content).Append("\n\n");
                    break;
                case AssistantMessageViewModel assistant when assistant.HasContent:
                    builder.Append("Assistant:").Append('\n').Append(assistant.Content).Append("\n\n");
                    break;
                case ToolActivityViewModel activity:
                    builder.Append("[tools] ").Append(activity.Summary).Append("\n\n");
                    break;
                case RunStatusViewModel status:
                    builder.Append('[').Append(status.Title).Append("]\n\n");
                    break;
                case CompactionMarkerViewModel marker:
                    builder.Append('[').Append(marker.Label).Append("]\n\n");
                    break;
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    // U8: counts what a user would call "messages" in the delete-confirmation dialog: the
    // user/assistant text bubbles, not tool-activity cards or status/compaction markers. Both
    // paths use the same semantics so the dialog shows the same number whether or not the session
    // happens to be open this run — cached transcripts count their text-bubble view models, and
    // uncached sessions count raw MessageKind.Text entries (the kind that renders as a bubble)
    // without populating the VM cache or building view models.
    public async Task<int> GetMessageCountAsync(string sessionId, CancellationToken ct)
    {
        if (_transcripts.TryGetValue(sessionId, out var cached))
            return cached.Count(item => item is UserMessageViewModel or AssistantMessageViewModel);

        var messages = await _sessions.LoadAsync(sessionId, ct);
        return messages.Count(message => message.Kind == MessageKind.Text);
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

        runMessages.Add(new UserMessageViewModel(prompt) { Timestamp = _timeProvider.GetUtcNow() });
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
                    _usageStore.Save(sessionId, new SessionUsage(mapper.PromptTokens, mapper.CompletionTokens));
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

    [RelayCommand]
    private void ToggleInspector() => IsInspectorCollapsed = !IsInspectorCollapsed;

    [RelayCommand]
    private void CloseSearch() => IsSearchActive = false;

    private bool CanNavigateSearchMatches() => _searchMatches.Count > 0;

    [RelayCommand(CanExecute = nameof(CanNavigateSearchMatches))]
    private void NextSearchMatch() => MoveSearchMatch(1);

    [RelayCommand(CanExecute = nameof(CanNavigateSearchMatches))]
    private void PreviousSearchMatch() => MoveSearchMatch(-1);

    private void MoveSearchMatch(int direction)
    {
        if (_searchMatches.Count == 0)
            return;

        _searchMatchIndex = ((_searchMatchIndex + direction) % _searchMatches.Count + _searchMatches.Count)
            % _searchMatches.Count;
        CurrentSearchMatch = _searchMatches[_searchMatchIndex];
        UpdateSearchMatchText();
    }

    // Recomputes from scratch against the *current* Messages collection every time — never against
    // a cached/stale reference (see the U7 pitfall note on OnMessagesChanged).
    private void RecomputeSearchMatches()
    {
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        CurrentSearchMatch = null;

        var query = SearchQuery.Trim();
        if (query.Length > 0)
        {
            foreach (var item in Messages)
            {
                if (MatchesSearchQuery(item, query))
                    _searchMatches.Add(item);
            }

            if (_searchMatches.Count > 0)
            {
                _searchMatchIndex = 0;
                CurrentSearchMatch = _searchMatches[0];
            }
        }

        UpdateSearchMatchText();
        NextSearchMatchCommand.NotifyCanExecuteChanged();
        PreviousSearchMatchCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSearchMatchText()
    {
        var hasQuery = SearchQuery.Trim().Length > 0;
        SearchMatchText = !hasQuery
            ? string.Empty
            : _searchMatches.Count == 0
                ? "No matches"
                : $"{_searchMatchIndex + 1} of {_searchMatches.Count}";
        OnPropertyChanged(nameof(SearchMatchText));
    }

    // Only these visible-text sources participate in search; a ToolCallViewModel is never a
    // top-level Messages entry (it's always nested under a ToolActivityViewModel's Calls), so a
    // match there surfaces the containing activity card as the searchable/scrollable item.
    private static bool MatchesSearchQuery(ChatItemViewModel item, string query) =>
        item switch
        {
            UserMessageViewModel user => ContainsQuery(user.Content, query),
            AssistantMessageViewModel assistant => ContainsQuery(assistant.Content, query),
            ToolActivityViewModel activity => activity.Calls.Any(call =>
                ContainsQuery(call.Headline, query) || ContainsQuery(call.Output, query)),
            _ => false,
        };

    private static bool ContainsQuery(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

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
                if (mapper.FlushStreamingMessage(MinStreamingRenderInterval))
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

        // Resolved before it reached the UI (e.g. the run was cancelled between the service raising
        // the event and this handler running): the IsPending change already fired, so subscribing
        // now would leave it stranded in the queue. The transcript copy above still renders it.
        if (!e.Approval.IsPending)
            return;

        e.Approval.PropertyChanged += Approval_PropertyChanged;
        _pendingApprovals.Add(e.Approval);
        if (PendingApproval is null)
            PendingApproval = e.Approval;
        else
            NotifyApprovalQueueChanged();
    }

    private void Approval_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ApprovalViewModel.IsPending) ||
            sender is not ApprovalViewModel { IsPending: false } approval)
        {
            return;
        }

        approval.PropertyChanged -= Approval_PropertyChanged;
        // Remove by reference: a queued approval resolved externally (timeout auto-deny, run
        // cancellation, or a PermissionResolved event) is dropped without disturbing the current
        // one; the current one promotes the next queued approval when it resolves.
        var wasCurrent = ReferenceEquals(PendingApproval, approval);
        _pendingApprovals.Remove(approval);
        if (wasCurrent)
            PendingApproval = _pendingApprovals.Count > 0 ? _pendingApprovals[0] : null;
        else
            NotifyApprovalQueueChanged();
    }

    private void NotifyApprovalQueueChanged()
    {
        OnPropertyChanged(nameof(HasQueuedApprovals));
        OnPropertyChanged(nameof(PendingApprovalCountText));
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
