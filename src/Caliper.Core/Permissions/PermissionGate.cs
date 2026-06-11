// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Caliper.Core.Permissions;

public sealed class PermissionGate(
    IRuntimeSettings runtimeSettings,
    IServiceProvider services) : IPermissionGate
{
    public const string DenylistReasonPrefix = "[denylist]";
    public const string FileOutsideRootReasonPrefix = "[file-outside-root]";

    private readonly HashSet<string> _sessionApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _approvalsGate = new();

    public async Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct)
    {
        var opts = runtimeSettings.Permissions;
        var fileAccess = new FileAccessPolicy(runtimeSettings.Caliper, opts);
        var effect = EffectiveSideEffect(request, fileAccess);

        if (effect == SideEffect.ReadOnly)
            return PermissionDecision.Allow;

        if (opts.Mode == PermissionMode.Plan)
            return PermissionDecision.Deny;

        var signature = Signature(request);
        if (request.Effect == SideEffect.Execute && DenylistHit(NormalizeCommand(ExtractCommand(request.Arguments) ?? ""), opts))
            return await PromptAsync(request with { Reason = DenylistReason(request) }, signature, rememberSession: false, ct).ConfigureAwait(false);

        if (opts.RememberApprovals && HasSessionApproval(signature))
            return PermissionDecision.Allow;

        if (opts.Mode == PermissionMode.Auto)
        {
            if (request.Effect == SideEffect.Execute)
                return await EvaluateShellAutoAsync(request, opts, signature, ct).ConfigureAwait(false);

            if (effect == SideEffect.Write && IsOutsideRootFileRequest(request, fileAccess))
                return await PromptAsync(request with { Reason = FileOutsideRootReason(request, fileAccess) }, signature, rememberSession: opts.RememberApprovals, ct).ConfigureAwait(false);

            return PermissionDecision.Allow;
        }

        return await PromptAsync(request, signature, rememberSession: opts.RememberApprovals, ct).ConfigureAwait(false);
    }

    public static string NormalizeCommand(string command) =>
        Regex.Replace(command.Trim(), @"\s+", " ");

    public static string? ExtractCommand(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty("command", out var command) &&
        command.ValueKind == JsonValueKind.String
            ? command.GetString()
            : null;

    private async Task<PermissionDecision> EvaluateShellAutoAsync(
        PermissionRequest request,
        PermissionsOptions opts,
        string signature,
        CancellationToken ct)
    {
        var command = NormalizeCommand(ExtractCommand(request.Arguments) ?? "");
        if (DenylistHit(command, opts))
            return await PromptAsync(request with { Reason = DenylistReason(request) }, signature, rememberSession: false, ct).ConfigureAwait(false);

        if (opts.ShellAutoAllowlist.Any(pattern =>
                command.StartsWith(NormalizeCommand(pattern), StringComparison.OrdinalIgnoreCase)))
            return PermissionDecision.Allow;

        return await PromptAsync(request, signature, rememberSession: opts.RememberApprovals, ct).ConfigureAwait(false);
    }

    private async Task<PermissionDecision> PromptAsync(
        PermissionRequest request,
        string signature,
        bool rememberSession,
        CancellationToken ct)
    {
        var prompt = services.GetService<IPermissionPrompt>();
        if (prompt is null)
            return PermissionDecision.Deny;

        var decision = await prompt.AskAsync(request, ct).ConfigureAwait(false);
        if (decision == PermissionDecision.AllowForSession)
        {
            if (rememberSession)
                RememberSessionApproval(signature);
            return PermissionDecision.Allow;
        }

        return decision;
    }

    private static SideEffect EffectiveSideEffect(PermissionRequest request, FileAccessPolicy fileAccess)
    {
        if (request.Effect == SideEffect.ReadOnly && IsOutsideRootFileRequest(request, fileAccess))
            return SideEffect.Write;

        return request.Effect;
    }

    private static bool IsOutsideRootFileRequest(PermissionRequest request, FileAccessPolicy fileAccess) =>
        FileAccessPolicy.IsFileTool(request.Tool) &&
        fileAccess.RequiresPermission(request.Tool, request.Arguments);

    private bool HasSessionApproval(string signature)
    {
        lock (_approvalsGate)
            return _sessionApprovals.Contains(signature);
    }

    private void RememberSessionApproval(string signature)
    {
        lock (_approvalsGate)
            _sessionApprovals.Add(signature);
    }

    private static bool DenylistHit(string command, PermissionsOptions opts) =>
        opts.ShellDenylist.Any(pattern =>
            command.Contains(NormalizeCommand(pattern), StringComparison.OrdinalIgnoreCase));

    private static string Signature(PermissionRequest request)
    {
        if (request.Effect == SideEffect.Execute && ExtractCommand(request.Arguments) is { } command)
            return $"{request.Tool}:{NormalizeCommand(command)}";

        return $"{request.Tool}:{request.Arguments.GetRawText()}";
    }

    private static string DenylistReason(PermissionRequest request) =>
        $"{DenylistReasonPrefix} Shell command matches denylist: {NormalizeCommand(ExtractCommand(request.Arguments) ?? "")}";

    private static string FileOutsideRootReason(PermissionRequest request, FileAccessPolicy fileAccess)
    {
        FileAccessPolicy.TryGetRequestedPath(request.Tool, request.Arguments, out var path);
        return $"{FileOutsideRootReasonPrefix} File path is outside configured auto-access roots: {fileAccess.ResolvePath(path)}";
    }
}
