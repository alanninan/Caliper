// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.App.Permissions;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Permissions;

namespace Caliper.App.Tests;

public sealed class ChatViewModelTests
{
    private static ChatViewModel Create(
        FakeAgentRunner runner,
        out FakeSessionStore sessions,
        out TrackingPermissionGate permissionGate) =>
        Create(runner, out sessions, out permissionGate, out _);

    private static ChatViewModel Create(
        FakeAgentRunner runner,
        out FakeSessionStore sessions,
        out TrackingPermissionGate permissionGate,
        out ApprovalService approvals)
    {
        sessions = new FakeSessionStore();
        permissionGate = new TrackingPermissionGate();
        var runtimeSettings = new TestRuntimeSettings();
        approvals = new ApprovalService(new InlineDispatcher(), TimeProvider.System, runtimeSettings);
        return new ChatViewModel(
            runner,
            sessions,
            TimeProvider.System,
            approvals,
            permissionGate,
            new FakeConversationOrchestrator(),
            runtimeSettings,
            new InlineDispatcher());
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
            new InlineDispatcher());
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
            new InlineDispatcher());
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
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals);

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
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals);
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
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals);
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
        var viewModel = Create(new FakeAgentRunner(), out _, out _, out var approvals);

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
        var viewModel = Create(new FakeAgentRunner(), out var sessions, out var permissionGate, out var approvals);
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

        public void Seed(string id) => _sessions.Add(new SessionSummary(id, id, DateTimeOffset.UtcNow));

        public Task<string> CreateAsync(string? title, CancellationToken ct)
        {
            var id = $"session-{_sessions.Count + 1}";
            _sessions.Add(new SessionSummary(id, title, DateTimeOffset.UtcNow));
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
}
