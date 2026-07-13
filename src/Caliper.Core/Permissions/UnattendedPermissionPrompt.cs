// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Events;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Permissions;

/// <summary>
/// The unattended (headless) permission prompt: it always denies and never asks a human.
/// Registered only by headless entry points — the console's <c>--unattended</c> one-shot flag
/// today, the scheduler in a later roadmap step — so interactive hosts (Console REPL, the App)
/// keep their own <see cref="IPermissionPrompt"/> implementations untouched.
/// </summary>
/// <remarks>
/// Roadmap §3.2a decision: there is no new <see cref="PermissionMode"/>. <c>Auto</c> mode is
/// already the unattended policy engine (allowlisted shell + in-root file writes auto-allow,
/// everything else "asks"); this type turns that "ask" fallback into "deny, never grant." The
/// contract is deny + report, never silent-allow and never silent-drop:
/// <list type="bullet">
/// <item>Every denial is logged here at <see cref="LogLevel.Warning"/> with the tool name, a
/// short argument signature, and the request reason (if any).</item>
/// <item><see cref="PermissionGate"/> emits the same <c>PermissionRequested</c>/
/// <c>PermissionResolved</c> event pair for every decision path — prompted or not — so this
/// denial also flows into <c>ConversationRunResult.Denials</c> via the orchestrator's existing
/// correlation. No gate or event-plumbing change was needed for that: see
/// <c>AgentRunner.RunAsync</c> around the <c>permissionGate.EvaluateAsync</c> call, which yields
/// <c>PermissionResolved</c> unconditionally regardless of which branch inside the gate produced
/// the decision.</item>
/// </list>
/// This type never returns <see cref="PermissionDecision.AllowForSession"/> or
/// <see cref="PermissionDecision.Allow"/> — no grants are ever made unattended, so
/// <c>RememberApprovals</c> has nothing to remember here even when the effective options have it
/// set to <see langword="true"/>.
/// </remarks>
public sealed class UnattendedPermissionPrompt(ILogger<UnattendedPermissionPrompt> logger) : IPermissionPrompt
{
    private const int MaxSignatureLength = 200;

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        logger.LogWarning(
            "Unattended: denied {Tool} ({Effect}) {Signature}{ReasonSuffix}",
            request.Tool,
            request.Effect,
            Signature(request),
            ReasonSuffix(request.Reason));

        return Task.FromResult(PermissionDecision.Deny);
    }

    private static string Signature(PermissionRequest request)
    {
        var raw = request.Effect == SideEffect.Execute && PermissionGate.ExtractCommand(request.Arguments) is { } command
            ? PermissionGate.NormalizeCommand(command)
            : request.Arguments.GetRawText();

        return raw.Length <= MaxSignatureLength
            ? raw
            : string.Concat(raw.AsSpan(0, MaxSignatureLength), "…");
    }

    private static string ReasonSuffix(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" — {reason}";
}
