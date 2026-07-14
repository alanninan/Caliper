// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Configuration;

/// <summary>
/// Roadmap §3.3 "the payoff": a bare <c>"*"</c> entry in <c>PermissionsOptions.ShellAutoAllowlist</c>
/// only makes sense to accept when the shell backend confines the blast radius to a disposable
/// container. Today's <c>PermissionGate.AllowlistPrefixHit</c> matches allowlist entries by prefix
/// (<c>command.StartsWith(pattern)</c>), so a literal <c>"*"</c> entry does not actually behave as
/// a wildcard yet — it would only match a command that itself starts with the character <c>*</c>,
/// which is not a valid shell invocation. This guard exists anyway (validation, not convention) so
/// that a future wildcard-matching change to the gate can't silently combine with
/// <c>Execution.Backend == Host</c> to grant unattended blanket shell access; the config is
/// rejected at the boundary regardless of whether the matcher currently honors it.
/// </summary>
internal static class UnattendedAllowlistGuard
{
    internal const string WildcardEntry = "*";

    /// <summary>True when any entry, trimmed, is exactly <c>"*"</c>.</summary>
    internal static bool HasBareWildcard(IEnumerable<string> allowlist) =>
        allowlist.Any(entry => entry?.Trim() == WildcardEntry);

    /// <summary>
    /// Returns a failure message when <paramref name="allowlist"/> contains a bare wildcard and
    /// <paramref name="backend"/> is not <see cref="ExecutionBackendKind.Container"/>; null when the
    /// configuration is fine. <paramref name="context"/> names the offending section for the error
    /// (e.g. <c>"Schedule 'nightly'"</c> or <c>"The global Permissions section's"</c>).
    /// </summary>
    internal static string? Validate(IEnumerable<string> allowlist, ExecutionBackendKind backend, string context)
    {
        if (backend == ExecutionBackendKind.Container)
            return null;

        return HasBareWildcard(allowlist)
            ? $"{context} ShellAutoAllowlist contains a bare \"*\" wildcard, which requires " +
              $"Execution.Backend to be Container (Backend is currently {backend}). Set " +
              "Execution.Backend to Container, or replace \"*\" with specific command prefixes."
            : null;
    }
}
