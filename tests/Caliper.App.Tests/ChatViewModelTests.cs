// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.App.Permissions;
using Caliper.App.Preferences;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.App.Tests;

public sealed class ChatViewModelTests
{
    private static ChatViewModel Create(
        FakeAgentRunner runner,
        out FakeSessionStore sessions,
        out TrackingPermissionGate permissionGate) =>
        Create(runner, out sessions, out permissionGate, out _, out _, out _);

    private static ChatViewModel Create(
        FakeAgentRunner runner,
        out FakeSessionStore sessions,
        out TrackingPermissionGate permissionGate,
        out ApprovalService approvals,
        out FakeSessionUsageStore usageStore) =>
        Create(runner, out sessions, out permissionGate, out approvals, out usageStore, out _);

    private static ChatViewModel Create(
        FakeAgentRunner runner,
        out FakeSessionStore sessions,
        out TrackingPermissionGate permissionGate,
        out ApprovalService approvals,
        out FakeSessionUsageStore usageStore,
        out FakePreferencesStore preferences)
    {
        sessions = new FakeSessionStore();
        permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        usageStore = new FakeSessionUsageStore();
        preferences = new FakePreferencesStore();
        return new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            usageStore,
            preferences);
    }

    private static PermissionRequest Request(string tool, string requestId) =>
        new(
            tool,
            SideEffect.Execute,
            JsonSerializer.SerializeToElement(new { command = "ls" }),
            Reason: null,
            RequestId: requestId);

    [Fact]
    public async Task SendAsync_happy_path_builds_transcript_in_order()
    {
        var runner = new FakeAgentRunner
        {
            Events =
            [
                new AssistantMessage("hi there"),
                new RunCompleted(CompletionReason.Completed),
            ],
        };
        var viewModel = Create(runner, out _, out _);
        viewModel.InputText = "hello";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.IsType<UserMessageViewModel>(viewModel.Messages[0]);
        var assistant = Assert.IsType<AssistantMessageViewModel>(viewModel.Messages[1]);
        Assert.Equal("hi there", assistant.Content);
        Assert.Equal(ChatRunStatus.Ready, viewModel.RunStatus);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task Stop_cancels_active_run()
    {
        var runner = new FakeAgentRunner { HangAfterEvents = true };
        var viewModel = Create(runner, out _, out _);
        viewModel.InputText = "hello";

        var sendTask = viewModel.SendCommand.ExecuteAsync(null);
        await runner.Started.Task;
        Assert.True(viewModel.IsRunning);

        viewModel.StopCommand.Execute(null);
        await sendTask;

        Assert.Equal(ChatRunStatus.Cancelled, viewModel.RunStatus);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task SelectSessionAsync_does_not_reset_session_scoped_approvals()
    {
        // Approvals are scoped per session by the gate; switching sessions must not revoke a run's
        // grants in the session being switched away from.
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out var sessions, out var permissionGate);
        sessions.Seed("session-1");

        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        Assert.Equal(0, permissionGate.ResetCount);
    }

    [Fact]
    public async Task ClearSessionSelection_resets_current_session_approvals()
    {
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out var sessions, out var permissionGate);
        sessions.Seed("session-1");
        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        viewModel.ClearSessionSelection();

        Assert.Contains("session-1", permissionGate.ResetSessions);
    }

    [Fact]
    public async Task RemoveSession_resets_that_session_approvals()
    {
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out var sessions, out var permissionGate);
        sessions.Seed("session-1");
        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        viewModel.RemoveSession("session-1");

        Assert.Contains("session-1", permissionGate.ResetSessions);
    }

    [Fact]
    public async Task SelectSessionAsync_shows_usage_persisted_from_a_previous_run()
    {
        // B8: the footer must survive an app restart. Seed the fake store the way LoadAll() would
        // be populated from disk, before the view model (and its constructor-time seeding) exists.
        var usageStore = new FakeSessionUsageStore();
        usageStore.Seed("session-1", new SessionUsage(120, 40));
        var runner = new FakeAgentRunner();
        var sessions = new FakeSessionStore();
        var permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        var approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        var viewModel = new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            usageStore,
            new FakePreferencesStore());
        sessions.Seed("session-1");

        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        Assert.Equal("Total prompt: 120  Total completion: 40", viewModel.UsageText);
    }

    [Fact]
    public async Task RemoveSession_removes_persisted_usage_from_the_store()
    {
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out var sessions, out _, out _, out var usageStore);
        sessions.Seed("session-1");
        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        viewModel.RemoveSession("session-1");

        Assert.Contains("session-1", usageStore.RemovedSessionIds);
    }

    [Fact]
    public async Task Unmapped_completion_reason_does_not_throw()
    {
        var runner = new FakeAgentRunner
        {
            Events = [new RunCompleted((CompletionReason)999)],
        };
        var viewModel = Create(runner, out _, out _);
        viewModel.InputText = "hello";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(ChatRunStatus.Failed, viewModel.RunStatus);
    }

    [Fact]
    public void SendCommand_only_executable_while_idle_with_text()
    {
        // The composer's shared send-or-queue path (Enter, Ctrl+Enter, Send button) leans on these
        // guards: send when idle, never while running, never for a whitespace prompt.
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out _, out _);

        Assert.False(viewModel.SendCommand.CanExecute(null));

        viewModel.InputText = "   ";
        Assert.False(viewModel.SendCommand.CanExecute(null));

        viewModel.InputText = "hello";
        Assert.True(viewModel.SendCommand.CanExecute(null));

        viewModel.IsRunning = true;
        Assert.False(viewModel.SendCommand.CanExecute(null));
    }

    [Fact]
    public void QueueMessageCommand_only_executable_while_running_with_text()
    {
        var runner = new FakeAgentRunner();
        var viewModel = Create(runner, out _, out _);

        viewModel.InputText = "queued";
        Assert.False(viewModel.QueueMessageCommand.CanExecute(null));

        viewModel.IsRunning = true;
        Assert.True(viewModel.QueueMessageCommand.CanExecute(null));

        viewModel.QueueMessageCommand.Execute(null);

        Assert.Equal("queued", viewModel.QueuedMessage);
        Assert.Equal(string.Empty, viewModel.InputText);
        Assert.True(viewModel.HasQueuedMessage);
    }

    [Fact]
    public async Task SendAsync_queued_message_auto_sends_after_completion()
    {
        // The first run hangs so we can queue a message through the real command while it's active;
        // the second (auto-sent) run completes normally.
        var runner = new FakeAgentRunner { HangAfterEvents = true };
        var viewModel = Create(runner, out _, out _);
        viewModel.InputText = "first";

        var sendTask = viewModel.SendCommand.ExecuteAsync(null);
        await runner.Started.Task;

        viewModel.InputText = "queued text";
        viewModel.QueueMessageCommand.Execute(null);
        Assert.Equal("queued text", viewModel.QueuedMessage);

        viewModel.StopCommand.Execute(null);
        await sendTask;

        Assert.Equal(["first", "queued text"], runner.Prompts);
        Assert.Null(viewModel.QueuedMessage);
    }

    [Fact]
    public async Task SendAsync_queued_message_not_sent_when_user_switched_sessions()
    {
        // Queue in the running session, switch to another session before the run ends: the queued
        // message must not be posted into the session we switched to — it returns to the composer.
        var runner = new FakeAgentRunner { HangAfterEvents = true };
        var viewModel = Create(runner, out var sessions, out _);
        sessions.Seed("other");
        viewModel.InputText = "first";

        var sendTask = viewModel.SendCommand.ExecuteAsync(null);
        await runner.Started.Task;

        viewModel.InputText = "queued text";
        viewModel.QueueMessageCommand.Execute(null);

        await viewModel.SelectSessionAsync("other", CancellationToken.None);
        viewModel.StopCommand.Execute(null);
        await sendTask;

        Assert.Equal(["first"], runner.Prompts);
        Assert.Null(viewModel.QueuedMessage);
        Assert.Equal("queued text", viewModel.InputText);
    }

    [Fact]
    public void RuntimeSettings_change_raises_workspace_header_property_changes()
    {
        var runner = new FakeAgentRunner();
        var sessions = new FakeSessionStore();
        var permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        var approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        var viewModel = new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            new FakeSessionUsageStore(),
            new FakePreferencesStore());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        runtimeSettings.SetModel("new/model");

        Assert.Contains(nameof(ChatViewModel.RuntimeSummary), changed);
        Assert.Contains(nameof(ChatViewModel.PermissionModeText), changed);
        Assert.Contains("new/model", viewModel.RuntimeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_request_sets_and_clears_pending_approval()
    {
        var runner = new FakeAgentRunner();
        var sessions = new FakeSessionStore();
        var permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        var approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        var viewModel = new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            new FakeSessionUsageStore(),
            new FakePreferencesStore());
        var request = new PermissionRequest(
            "bash",
            SideEffect.Execute,
            JsonSerializer.SerializeToElement(new { command = "ls" }),
            Reason: null);

        var askTask = approvals.AskAsync(request, CancellationToken.None);

        Assert.NotNull(viewModel.PendingApproval);
        Assert.True(viewModel.HasPendingApproval);

        viewModel.PendingApproval!.AllowCommand.Execute(null);
        await askTask;

        Assert.Null(viewModel.PendingApproval);
        Assert.False(viewModel.HasPendingApproval);
    }

    [Fact]
    public async Task Approval_requests_queue_fifo_and_promote_on_resolution()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals, out _);

        var firstTask = approvals.AskAsync(Request("bash", "r1"), CancellationToken.None);
        var secondTask = approvals.AskAsync(Request("powershell", "r2"), CancellationToken.None);

        // The first request stays current; the second queues behind it instead of overwriting.
        Assert.Equal("bash", viewModel.PendingApproval?.ToolName);
        Assert.True(viewModel.HasQueuedApprovals);
        Assert.Equal("Approval 1 of 2", viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.AllowCommand.Execute(null);
        Assert.Equal(PermissionDecision.Allow, await firstTask);

        Assert.Equal("powershell", viewModel.PendingApproval?.ToolName);
        Assert.False(viewModel.HasQueuedApprovals);
        Assert.Equal(string.Empty, viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.Equal(PermissionDecision.Deny, await secondTask);

        Assert.Null(viewModel.PendingApproval);
        Assert.False(viewModel.HasPendingApproval);
    }

    [Fact]
    public async Task Queued_approval_resolved_externally_is_dropped_without_disturbing_current()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals, out _);
        using var secondCancellation = new CancellationTokenSource();

        var firstTask = approvals.AskAsync(Request("bash", "r1"), CancellationToken.None);
        var secondTask = approvals.AskAsync(Request("powershell", "r2"), secondCancellation.Token);
        Assert.Equal("Approval 1 of 2", viewModel.PendingApprovalCountText);

        // The queued (not current) approval resolves externally — the same
        // TryCompleteAutomatically path the 5-minute timeout auto-deny uses.
        secondCancellation.Cancel();
        Assert.Equal(PermissionDecision.Deny, await secondTask);

        Assert.Equal("bash", viewModel.PendingApproval?.ToolName);
        Assert.False(viewModel.HasQueuedApprovals);
        Assert.Equal(string.Empty, viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.AllowCommand.Execute(null);
        Assert.Equal(PermissionDecision.Allow, await firstTask);
        Assert.Null(viewModel.PendingApproval);
    }

    [Fact]
    public async Task Current_approval_resolved_externally_promotes_next_queued()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals, out _);
        using var firstCancellation = new CancellationTokenSource();

        var firstTask = approvals.AskAsync(Request("bash", "r1"), firstCancellation.Token);
        var secondTask = approvals.AskAsync(Request("powershell", "r2"), CancellationToken.None);
        Assert.Equal("bash", viewModel.PendingApproval?.ToolName);

        firstCancellation.Cancel();
        Assert.Equal(PermissionDecision.Deny, await firstTask);

        Assert.Equal("powershell", viewModel.PendingApproval?.ToolName);
        Assert.False(viewModel.HasQueuedApprovals);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.Equal(PermissionDecision.Deny, await secondTask);
        Assert.Null(viewModel.PendingApproval);
    }

    [Fact]
    public async Task PendingApprovalCountText_reflects_queue_depth()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals, out _);

        var tasks = new[]
        {
            approvals.AskAsync(Request("bash", "r1"), CancellationToken.None),
            approvals.AskAsync(Request("powershell", "r2"), CancellationToken.None),
            approvals.AskAsync(Request("write_file", "r3"), CancellationToken.None),
        };

        Assert.Equal("Approval 1 of 3", viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.Equal("Approval 1 of 2", viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.Equal(string.Empty, viewModel.PendingApprovalCountText);
        Assert.False(viewModel.HasQueuedApprovals);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.All(await Task.WhenAll(tasks), decision => Assert.Equal(PermissionDecision.Deny, decision));
    }

    [Fact]
    public async Task SelectSessionAsync_preserves_pending_approval_queue()
    {
        // Approvals survive session switches today (the docked card stays actionable while viewing
        // another session); the queue must not regress that.
        var viewModel = Create(new FakeAgentRunner(), out var sessions, out var permissionGate, out var approvals, out _);
        sessions.Seed("other");

        var firstTask = approvals.AskAsync(Request("bash", "r1"), CancellationToken.None);
        var secondTask = approvals.AskAsync(Request("powershell", "r2"), CancellationToken.None);

        await viewModel.SelectSessionAsync("other", CancellationToken.None);

        Assert.Equal(0, permissionGate.ResetCount);
        Assert.Equal("bash", viewModel.PendingApproval?.ToolName);
        Assert.Equal("Approval 1 of 2", viewModel.PendingApprovalCountText);

        viewModel.PendingApproval!.DenyCommand.Execute(null);
        viewModel.PendingApproval!.DenyCommand.Execute(null);
        Assert.Equal(PermissionDecision.Deny, await firstTask);
        Assert.Equal(PermissionDecision.Deny, await secondTask);
    }

    [Fact]
    public void IsInspectorCollapsed_initial_value_comes_from_preferences_store()
    {
        var runner = new FakeAgentRunner();
        var sessions = new FakeSessionStore();
        var permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        var approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        var preferences = new FakePreferencesStore { Saved = new AppPreferences { InspectorPaneCollapsed = true } };

        var viewModel = new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            new FakeSessionUsageStore(),
            preferences);

        Assert.True(viewModel.IsInspectorCollapsed);
    }

    [Fact]
    public void ToggleInspectorCommand_flips_and_persists_collapsed_state()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out _, out _, out var preferences);

        Assert.False(viewModel.IsInspectorCollapsed);

        viewModel.ToggleInspectorCommand.Execute(null);

        Assert.True(viewModel.IsInspectorCollapsed);
        Assert.True(preferences.Saved?.InspectorPaneCollapsed);

        viewModel.ToggleInspectorCommand.Execute(null);

        Assert.False(viewModel.IsInspectorCollapsed);
        Assert.False(preferences.Saved?.InspectorPaneCollapsed);
    }

    [Fact]
    public async Task SendAsync_user_message_gets_timestamp_from_time_provider()
    {
        var runner = new FakeAgentRunner
        {
            Events = [new AssistantMessage("hi"), new RunCompleted(CompletionReason.Completed)],
        };
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-16T12:00:00Z"));
        var runtimeSettings = new TestRuntimeSettings();
        var approvals = new ApprovalService(new InlineDispatcher(), timeProvider, runtimeSettings);
        var viewModel = new ChatViewModel(
            runner,
            new FakeSessionStore(),
            timeProvider,
            approvals,
            new TrackingPermissionGate(),
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher(),
            new FakeSessionUsageStore(),
            new FakePreferencesStore());
        viewModel.InputText = "hello";

        await viewModel.SendCommand.ExecuteAsync(null);

        var user = Assert.IsType<UserMessageViewModel>(viewModel.Messages[0]);
        Assert.Equal(timeProvider.GetUtcNow(), user.Timestamp);
    }

    [Fact]
    public void BuildTranscriptText_renders_user_assistant_tool_and_status_items_in_order()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        var activity = new ToolActivityViewModel();
        activity.Add(new ToolCallViewModel("call-1", "read_file", string.Empty)
        {
            Status = "Succeeded",
            Output = "contents",
        });
        ObservableCollection<ChatItemViewModel> messages =
        [
            new UserMessageViewModel("Hello"),
            new AssistantMessageViewModel { Content = "Hi there", IsStreaming = false },
            activity,
            new RunStatusViewModel("Run ended", "Cancelled"),
            new CompactionMarkerViewModel("Conversation compacted"),
        ];
        viewModel.Messages = messages;

        var text = viewModel.BuildTranscriptText();

        Assert.Equal(
            "You:\nHello\n\n" +
            "Assistant:\nHi there\n\n" +
            $"[tools] {activity.Summary}\n\n" +
            "[Run ended]\n\n" +
            "[Conversation compacted]",
            text);
    }

    [Fact]
    public void BuildTranscriptText_skips_reasoning_and_empty_assistant_messages()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        ObservableCollection<ChatItemViewModel> messages =
        [
            new UserMessageViewModel("Hello"),
            new ReasoningViewModel { Content = "thinking…" },
            new AssistantMessageViewModel { Content = string.Empty, IsStreaming = false },
        ];
        viewModel.Messages = messages;

        var text = viewModel.BuildTranscriptText();

        Assert.Equal("You:\nHello", text);
    }

    [Fact]
    public void SearchQuery_matches_user_assistant_and_tool_items_case_insensitively()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        var activity = new ToolActivityViewModel();
        activity.Add(new ToolCallViewModel("call-1", "bash", string.Empty) { Output = "needle in haystack" });
        var user = new UserMessageViewModel("looking for NEEDLE");
        var assistant = new AssistantMessageViewModel { Content = "no match here", IsStreaming = false };
        viewModel.Messages = [user, assistant, activity];

        viewModel.SearchQuery = "needle";

        Assert.Equal("1 of 2", viewModel.SearchMatchText);
        Assert.Same(user, viewModel.CurrentSearchMatch);
    }

    [Fact]
    public void NextAndPreviousSearchMatchCommand_wrap_around()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        var activity = new ToolActivityViewModel();
        activity.Add(new ToolCallViewModel("call-1", "bash", string.Empty) { Output = "needle in haystack" });
        var user = new UserMessageViewModel("looking for needle");
        viewModel.Messages = [user, activity];
        viewModel.SearchQuery = "needle";
        Assert.Equal("1 of 2", viewModel.SearchMatchText);

        viewModel.NextSearchMatchCommand.Execute(null);
        Assert.Equal("2 of 2", viewModel.SearchMatchText);
        Assert.Same(activity, viewModel.CurrentSearchMatch);

        viewModel.NextSearchMatchCommand.Execute(null);
        Assert.Equal("1 of 2", viewModel.SearchMatchText);
        Assert.Same(user, viewModel.CurrentSearchMatch);

        viewModel.PreviousSearchMatchCommand.Execute(null);
        Assert.Equal("2 of 2", viewModel.SearchMatchText);
        Assert.Same(activity, viewModel.CurrentSearchMatch);
    }

    [Fact]
    public void SearchQuery_with_no_matches_shows_no_matches_text()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        viewModel.Messages = [new UserMessageViewModel("hello world")];

        viewModel.SearchQuery = "zzz";

        Assert.Equal("No matches", viewModel.SearchMatchText);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public void SearchQuery_cleared_resets_match_state()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        viewModel.Messages = [new UserMessageViewModel("hello world")];
        viewModel.SearchQuery = "hello";
        Assert.Equal("1 of 1", viewModel.SearchMatchText);

        viewModel.SearchQuery = string.Empty;

        Assert.Equal(string.Empty, viewModel.SearchMatchText);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public void IsSearchActive_closing_resets_query_and_match_state()
    {
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        viewModel.Messages = [new UserMessageViewModel("hello world")];
        viewModel.IsSearchActive = true;
        viewModel.SearchQuery = "hello";
        Assert.Equal("1 of 1", viewModel.SearchMatchText);

        viewModel.IsSearchActive = false;

        Assert.Equal(string.Empty, viewModel.SearchQuery);
        Assert.Equal(string.Empty, viewModel.SearchMatchText);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public void Messages_reassignment_while_search_active_recomputes_against_new_collection()
    {
        // U7 PITFALL: Messages is reassigned wholesale on session switch — an active search must
        // not keep matches pointing at items from the session just left.
        var viewModel = Create(new FakeAgentRunner(), out _, out _);
        viewModel.Messages = [new UserMessageViewModel("needle here")];
        viewModel.IsSearchActive = true;
        viewModel.SearchQuery = "needle";
        Assert.Equal("1 of 1", viewModel.SearchMatchText);

        viewModel.Messages = [new UserMessageViewModel("no match")];

        Assert.Equal("No matches", viewModel.SearchMatchText);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public async Task GetMessageCountAsync_returns_cached_transcript_count_when_session_loaded()
    {
        var viewModel = Create(new FakeAgentRunner(), out var sessions, out _);
        sessions.Seed("session-1");
        // The Summary entry becomes a CompactionMarkerViewModel card in the cached transcript —
        // the cached path must count only the text bubbles, matching the uncached path's
        // MessageKind.Text semantics, so both show the same number in the delete dialog.
        sessions.SeedMessages(
            "session-1",
            ChatMessage.Text(ChatRole.User, "Hi"),
            ChatMessage.Text(ChatRole.Assistant, "Hello"),
            new ChatMessage(ChatRole.System, MessageKind.Summary, "compacted"));
        await viewModel.SelectSessionAsync("session-1", CancellationToken.None);

        var count = await viewModel.GetMessageCountAsync("session-1", CancellationToken.None);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GetMessageCountAsync_counts_text_messages_from_the_store_when_not_cached()
    {
        var viewModel = Create(new FakeAgentRunner(), out var sessions, out _);
        sessions.Seed("session-1");
        sessions.SeedMessages(
            "session-1",
            ChatMessage.Text(ChatRole.User, "Hi"),
            ChatMessage.Text(ChatRole.Assistant, "Hello"),
            new ChatMessage(ChatRole.System, MessageKind.Summary, "compacted"));

        // Not selected/cached — GetMessageCountAsync must load and count without populating
        // ChatViewModel's transcript cache, and narrow to MessageKind.Text (the "Summary" entry
        // above is not a message a user would count).
        var count = await viewModel.GetMessageCountAsync("session-1", CancellationToken.None);

        Assert.Equal(2, count);
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        private int _runCount;

        public List<AgentEvent> Events { get; set; } = [];

        // When set, only the first run hangs (until cancelled); later runs (e.g. an auto-sent queued
        // message) complete normally so the awaited re-send chain can finish.
        public bool HangAfterEvents { get; set; }
        public List<string> Prompts { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            string sessionId,
            string userMessage,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Prompts.Add(userMessage);
            var isFirstRun = Interlocked.Increment(ref _runCount) == 1;

            foreach (var evt in Events)
                yield return evt;

            Started.TrySetResult();

            if (HangAfterEvents && isFirstRun)
                await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly List<SessionSummary> _sessions = [];
        private readonly Dictionary<string, List<ChatMessage>> _messages = new(StringComparer.Ordinal);

        public void Seed(string id) => _sessions.Add(new SessionSummary(id, id, DateTimeOffset.UtcNow));

        // U8: seeds the raw persisted messages GetMessageCountAsync's uncached branch (and
        // SelectSessionAsync's transcript rebuild) reads via LoadAsync.
        public void SeedMessages(string id, params ChatMessage[] messages) => _messages[id] = [.. messages];

        public Task<string> CreateAsync(string? title, CancellationToken ct)
        {
            var id = $"session-{_sessions.Count + 1}";
            _sessions.Add(new SessionSummary(id, title, DateTimeOffset.UtcNow));
            return Task.FromResult(id);
        }

        public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(
                _messages.TryGetValue(sessionId, out var messages) ? [.. messages] : []);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([.. _sessions]);

        public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

        public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeConversationOrchestrator : IConversationOrchestrator
    {
        public Task<ConversationRunResult> RunToCompletionAsync(
            string sessionId,
            string prompt,
            Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
            CancellationToken ct) =>
            Task.FromResult(new ConversationRunResult(null, null, null, []));

        public Task<ConversationRunResult> RunToCompletionAsync(
            RunSpec spec,
            Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
            CancellationToken ct) =>
            Task.FromResult(new ConversationRunResult(null, null, null, []));

        public Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(new ContextFit([], Compacted: false, BeforeTokens: null, AfterTokens: null, EstimatedPromptTokens: null));
    }

    private sealed class TrackingPermissionGate : IPermissionGate
    {
        public int ResetCount { get; private set; }
        public List<string?> ResetSessions { get; } = [];

        public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(PermissionDecision.Allow);

        public void ResetSessionApprovals(string? sessionId = null)
        {
            ResetCount++;
            ResetSessions.Add(sessionId);
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => false;

        public bool TryEnqueue(Action action)
        {
            action();
            return true;
        }
    }

    private sealed class FakePreferencesStore : IAppPreferencesStore
    {
        public AppPreferences? Saved { get; set; }

        public AppPreferences Load() => Saved ?? new AppPreferences();

        public void Save(AppPreferences preferences) => Saved = preferences;
    }
}
