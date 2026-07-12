// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Events;
using Caliper.Core.Permissions;

namespace Caliper.App.Tests;

public sealed class ApprovalViewModelTests
{
    [Fact]
    public async Task AllowForSession_submits_and_resolves_as_session_approval()
    {
        var approval = Create(reason: null);

        approval.AllowForSessionCommand.Execute(null);
        var submission = await approval.Submission;
        approval.MarkResolved(PermissionDecision.Allow);

        Assert.Equal(PermissionDecision.AllowForSession, submission.Decision);
        Assert.False(approval.IsPending);
        Assert.True(approval.IsResolved);
        Assert.Equal("Allowed for this session", approval.Status);
    }

    [Fact]
    public void Denylist_request_disables_session_approval()
    {
        var approval = Create($"{PermissionGate.DenylistReasonPrefix} blocked command");

        Assert.False(approval.CanRememberApproval);
        Assert.False(approval.AllowForSessionCommand.CanExecute(null));
        Assert.True(approval.AllowCommand.CanExecute(null));
    }

    [Fact]
    public void Remember_approvals_disabled_hides_session_approval()
    {
        var request = new PermissionRequest(
            "powershell",
            SideEffect.Execute,
            JsonSerializer.SerializeToElement(new { command = "git status" }),
            Reason: null);
        var approval = new ApprovalViewModel(request, rememberApprovals: false, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.False(approval.CanRememberApproval);
        Assert.False(approval.AllowForSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Automatic_completion_marks_approval_denied()
    {
        var approval = Create(reason: null);

        Assert.True(approval.TryCompleteAutomatically(
            PermissionDecision.Deny,
            "Denied because the run was cancelled"));
        var submission = await approval.Submission;
        approval.MarkAutomaticallyResolved(submission.Decision, submission.AutomaticStatus!);

        Assert.True(approval.IsDenied);
        Assert.True(approval.IsResolved);
        Assert.Equal("Denied because the run was cancelled", approval.Status);
    }

    private static ApprovalViewModel Create(string? reason) =>
        new(new PermissionRequest(
            "powershell",
            SideEffect.Execute,
            JsonSerializer.SerializeToElement(new { command = "git status" }),
            reason), rememberApprovals: true, DateTimeOffset.UtcNow.AddMinutes(5));
}
