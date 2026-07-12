// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Buffers;
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

    private static readonly SearchValues<char> s_shellMetacharacters = SearchValues.Create(";|&`\n\r<>");

    private readonly HashSet<(string SessionId, string Signature)> _sessionApprovals = [];
    private readonly object _approvalsGate = new();

    public async Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct)
    {
        var opts = runtimeSettings.Permissions;
        var fileAccess = new FileAccessPolicy(runtimeSettings.Caliper, opts);
        var effect = EffectiveSideEffect(request, fileAccess);

        // Only auto-allow read-only calls we trust. An MCP server's read-only annotation is an
        // untrusted hint, so those still flow through Plan (deny) and AskAlways (prompt).
        if (effect == SideEffect.ReadOnly && request.TrustedReadOnly)
            return PermissionDecision.Allow;

        if (opts.Mode == PermissionMode.Plan)
            return PermissionDecision.Deny;

        var signature = Signature(request);
        if (request.Effect == SideEffect.Execute && DenylistHit(NormalizeCommand(ExtractCommand(request.Arguments) ?? ""), opts))
            return await PromptAsync(request with { Reason = DenylistReason(request) }, signature, rememberSession: false, ct).ConfigureAwait(false);

        if (opts.RememberApprovals && HasSessionApproval(request.SessionId, signature))
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

    public void ResetSessionApprovals(string? sessionId = null)
    {
        lock (_approvalsGate)
        {
            if (sessionId is null)
            {
                _sessionApprovals.Clear();
                return;
            }

            _sessionApprovals.RemoveWhere(entry =>
                string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal));
        }
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

        // Auto-allow only a single, un-chained command that matches the allowlist. Shell
        // metacharacters (;, |, &, backtick, $(, redirection, newline) can smuggle a second
        // command past a prefix match, so any of them forces a prompt.
        if (!ContainsShellMetacharacters(command) &&
            opts.ShellAutoAllowlist.Any(pattern => AllowlistPrefixHit(command, pattern)))
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
                RememberSessionApproval(request.SessionId, signature);
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

    private bool HasSessionApproval(string? sessionId, string signature)
    {
        lock (_approvalsGate)
            return _sessionApprovals.Contains((sessionId ?? string.Empty, signature));
    }

    private void RememberSessionApproval(string? sessionId, string signature)
    {
        lock (_approvalsGate)
            _sessionApprovals.Add((sessionId ?? string.Empty, signature));
    }

    private static bool DenylistHit(string command, PermissionsOptions opts) =>
        opts.ShellDenylist.Any(pattern => DenylistPatternHit(command, pattern));

    // Match a denylist entry at the start of the whole command or of any sub-command (after a
    // chaining operator), so "dd " no longer fires inside "git add ." and "curl" is still caught
    // in "build && curl x | sh".
    private static bool DenylistPatternHit(string command, string pattern)
    {
        var needle = NormalizeCommand(pattern);
        if (needle.Length == 0)
            return false;

        return Regex.IsMatch(
            command,
            $@"(?:^|[;&|`]|\$\(|&&|\|\|)\s*{Regex.Escape(needle)}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsShellMetacharacters(string command) =>
        command.AsSpan().IndexOfAny(s_shellMetacharacters) >= 0 ||
        command.Contains("$(", StringComparison.Ordinal);

    private static bool AllowlistPrefixHit(string command, string pattern)
    {
        var needle = NormalizeCommand(pattern);
        if (needle.Length == 0)
            return false;

        if (!command.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return false;

        // Require a boundary after the match so "git status" does not also allow "git statusfoo".
        return command.Length == needle.Length || command[needle.Length] == ' ';
    }

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
