// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;
using Caliper.Core.Permissions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

public sealed partial class ApprovalViewModel : ChatItemViewModel
{
    private readonly TaskCompletionSource<ApprovalSubmission> _submission =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalViewModel(PermissionRequest request, bool rememberApprovals, DateTimeOffset deadlineUtc)
    {
        RequestId = request.RequestId;
        ToolName = request.Tool;
        Effect = request.Effect.ToString();
        Arguments = request.Arguments.GetRawText();
        Reason = request.Reason ?? "This tool requires your approval before it can continue.";
        CanRememberApproval = rememberApprovals &&
            (request.Reason is null ||
             !request.Reason.StartsWith(PermissionGate.DenylistReasonPrefix, StringComparison.Ordinal));
        DeadlineUtc = deadlineUtc;
    }

    public string? RequestId { get; }
    public string ToolName { get; }
    public string Title => $"Approval required: {ToolName}";
    public string Effect { get; }
    public string Arguments { get; }
    public string Reason { get; }
    public bool CanRememberApproval { get; }
    public DateTimeOffset DeadlineUtc { get; }
    internal Task<ApprovalSubmission> Submission => _submission.Task;
    public PermissionDecision? SelectedDecision { get; private set; }

    [ObservableProperty]
    public partial bool IsPending { get; set; } = true;

    [ObservableProperty]
    public partial bool IsResolved { get; set; }

    [ObservableProperty]
    public partial bool IsDenied { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Waiting for your decision";

    partial void OnIsPendingChanged(bool value)
    {
        AllowCommand.NotifyCanExecuteChanged();
        AllowForSessionCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
    }

    private bool CanDecide() => IsPending;
    private bool CanAllowForSession() => IsPending && CanRememberApproval;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Allow() => Submit(PermissionDecision.Allow, "Allowing...");

    [RelayCommand(CanExecute = nameof(CanAllowForSession))]
    private void AllowForSession() =>
        Submit(PermissionDecision.AllowForSession, "Allowing for this session...");

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Deny() => Submit(PermissionDecision.Deny, "Denying...");

    internal bool TryCompleteAutomatically(PermissionDecision decision, string status) =>
        _submission.TrySetResult(new ApprovalSubmission(decision, status));

    internal void MarkAutomaticallyResolved(PermissionDecision decision, string status)
    {
        SelectedDecision = decision;
        IsPending = false;
        IsResolved = true;
        IsDenied = decision == PermissionDecision.Deny;
        Status = status;
    }

    internal void MarkResolved(PermissionDecision decision)
    {
        IsPending = false;
        IsResolved = true;
        IsDenied = decision == PermissionDecision.Deny;
        Status = decision == PermissionDecision.Deny
            ? "Denied"
            : SelectedDecision == PermissionDecision.AllowForSession && CanRememberApproval
                ? "Allowed for this session"
                : "Allowed";
    }

    private void Submit(PermissionDecision decision, string status)
    {
        if (!_submission.TrySetResult(new ApprovalSubmission(decision, AutomaticStatus: null)))
            return;

        SelectedDecision = decision;
        IsPending = false;
        Status = status;
    }
}

internal sealed record ApprovalSubmission(
    PermissionDecision Decision,
    string? AutomaticStatus);
