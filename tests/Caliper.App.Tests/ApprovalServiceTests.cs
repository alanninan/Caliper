// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.Permissions;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;

namespace Caliper.App.Tests;

public sealed class ApprovalServiceTests
{
    [Fact]
    public async Task AskAsync_user_allows_resolves_card()
    {
        var service = new ApprovalService(new InlineDispatcher(), TimeProvider.System, new TestRuntimeSettings());
        ApprovalViewModel? approval = null;
        service.ApprovalRequested += (_, args) => approval = args.Approval;

        var decisionTask = service.AskAsync(CreateRequest(), CancellationToken.None);
        Assert.NotNull(approval);
        approval.AllowCommand.Execute(null);

        Assert.Equal(PermissionDecision.Allow, await decisionTask);
        service.Resolve("powershell", PermissionDecision.Allow);
        Assert.True(approval.IsResolved);
        Assert.Equal("Allowed", approval.Status);
    }

    [Fact]
    public async Task AskAsync_run_cancellation_denies_and_resolves_card()
    {
        var service = new ApprovalService(new InlineDispatcher(), TimeProvider.System, new TestRuntimeSettings());
        ApprovalViewModel? approval = null;
        service.ApprovalRequested += (_, args) => approval = args.Approval;
        using var cancellation = new CancellationTokenSource();

        var decisionTask = service.AskAsync(CreateRequest(), cancellation.Token);
        cancellation.Cancel();

        Assert.Equal(PermissionDecision.Deny, await decisionTask);
        Assert.NotNull(approval);
        Assert.True(approval.IsResolved);
        Assert.True(approval.IsDenied);
        Assert.Contains("cancelled", approval.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_same_tool_uses_request_id()
    {
        var service = new ApprovalService(new InlineDispatcher(), TimeProvider.System, new TestRuntimeSettings());
        var approvals = new List<ApprovalViewModel>();
        service.ApprovalRequested += (_, args) => approvals.Add(args.Approval);
        var first = CreateRequest() with { RequestId = "call-1" };
        var second = CreateRequest() with { RequestId = "call-2" };

        var firstDecision = service.AskAsync(first, CancellationToken.None);
        var secondDecision = service.AskAsync(second, CancellationToken.None);
        approvals[0].AllowCommand.Execute(null);
        approvals[1].AllowCommand.Execute(null);
        _ = await firstDecision;
        _ = await secondDecision;

        service.Resolve("powershell", PermissionDecision.Allow, "call-1");

        Assert.True(approvals[0].IsResolved);
        Assert.False(approvals[1].IsResolved);
        service.Resolve("powershell", PermissionDecision.Allow, "call-2");
        Assert.True(approvals[1].IsResolved);
    }

    private static PermissionRequest CreateRequest() =>
        new(
            "powershell",
            SideEffect.Execute,
            JsonSerializer.SerializeToElement(new { command = "git status" }),
            Reason: null);

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

internal sealed class TestRuntimeSettings : IRuntimeSettings
{
    public CaliperOptions Caliper { get; } = new();
    public PermissionsOptions Permissions { get; } = new();
    public event EventHandler? SettingsChanged;
    public void SetProvider(string provider) { Caliper.Provider = provider; SettingsChanged?.Invoke(this, EventArgs.Empty); }
    public void SetModel(string model) { Caliper.Model = model; SettingsChanged?.Invoke(this, EventArgs.Empty); }
    public void SetPermissionMode(PermissionMode mode) { Permissions.Mode = mode; SettingsChanged?.Invoke(this, EventArgs.Empty); }
    public void UpdateCaliper(Action<CaliperOptions> mutate) { mutate(Caliper); SettingsChanged?.Invoke(this, EventArgs.Empty); }
    public void UpdatePermissions(Action<PermissionsOptions> mutate) { mutate(Permissions); SettingsChanged?.Invoke(this, EventArgs.Empty); }
    public bool TrySet(string key, string value, out string message)
    {
        if (string.Equals(key, "workingRoot", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(value))
        {
            Caliper.WorkingRoot = value.Trim();
            message = $"workingRoot = {Caliper.WorkingRoot}";
            return true;
        }

        message = $"Unsupported runtime setting: {key}";
        return false;
    }
}
