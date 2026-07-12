// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Caliper.Core.Events;
using Caliper.Core.Permissions;
using Caliper.Core.Abstractions;

namespace Caliper.App.Permissions;

public sealed class ApprovalService(
    IUiDispatcher dispatcher,
    TimeProvider timeProvider,
    IRuntimeSettings runtimeSettings) : IPermissionPrompt
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly List<ApprovalViewModel> _active = [];

    public event EventHandler<ApprovalRequestedEventArgs>? ApprovalRequested;

    public async Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return PermissionDecision.Deny;

        var approval = new ApprovalViewModel(
            request,
            runtimeSettings.Permissions.RememberApprovals,
            timeProvider.GetUtcNow() + ApprovalTimeout);
        lock (_gate)
            _active.Add(approval);

        if (!Enqueue(() => ApprovalRequested?.Invoke(this, new ApprovalRequestedEventArgs(approval))))
        {
            RemoveActive(approval);
            return PermissionDecision.Deny;
        }

        using var timeout = new CancellationTokenSource(ApprovalTimeout, timeProvider);
        using var cancellationRegistration = ct.Register(() =>
            approval.TryCompleteAutomatically(PermissionDecision.Deny, "Denied because the run was cancelled"));
        using var timeoutRegistration = timeout.Token.Register(() =>
            approval.TryCompleteAutomatically(PermissionDecision.Deny, "Denied because the approval timed out"));

        var submission = await approval.Submission.ConfigureAwait(false);
        if (submission.AutomaticStatus is not null)
        {
            _ = Enqueue(() => approval.MarkAutomaticallyResolved(
                submission.Decision,
                submission.AutomaticStatus));
            RemoveActive(approval);
        }

        return submission.Decision;
    }

    public void Resolve(string toolName, PermissionDecision decision, string? requestId = null)
    {
        ApprovalViewModel? approval;
        lock (_gate)
        {
            approval = requestId is null
                ? _active.LastOrDefault(item =>
                    string.Equals(item.ToolName, toolName, StringComparison.Ordinal))
                : _active.LastOrDefault(item =>
                    string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
            if (approval is not null)
                _active.Remove(approval);
        }

        if (approval is not null)
            _ = Enqueue(() => approval.MarkResolved(decision));
    }

    private bool Enqueue(Action action)
    {
        if (dispatcher.HasThreadAccess)
        {
            action();
            return true;
        }

        return dispatcher.TryEnqueue(action);
    }

    private void RemoveActive(ApprovalViewModel approval)
    {
        lock (_gate)
            _active.Remove(approval);
    }
}

public sealed class ApprovalRequestedEventArgs(ApprovalViewModel approval) : EventArgs
{
    public ApprovalViewModel Approval { get; } = approval;
}
