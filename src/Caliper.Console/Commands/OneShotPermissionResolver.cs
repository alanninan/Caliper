// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Console.Commands;

/// <summary>
/// Resolves how <c>--unattended</c> composes with the existing <c>--prompt</c>/<c>--permissions</c>
/// one-shot flags. Pure and side-effect free so the composition rules are unit-testable without
/// building the host.
/// </summary>
public static class OneShotPermissionResolver
{
    /// <summary>
    /// <paramref name="permissionMode"/> is the raw <c>--permissions</c> value; the caller is
    /// expected to have already validated it parses as a <see cref="PermissionMode"/> (Program.cs
    /// does this before calling in, so an invalid string never reaches here).
    /// </summary>
    public static OneShotPermissionPlan Resolve(bool unattended, bool hasPrompt, string? permissionMode)
    {
        // Unattended only makes sense for a one-shot run; there is no human to interact with an
        // "unattended REPL", so reject rather than silently ignoring the flag.
        if (unattended && !hasPrompt)
            return OneShotPermissionPlan.Invalid(
                "--unattended requires --prompt; there is no interactive REPL under an unattended run.");

        // Read-only-by-default is the *attended* one-shot fallback: no --permissions given means
        // "just let me look, don't act." Unattended has a different default (Auto, below) because
        // the point of an unattended run is to act — safety comes from the deny+report policy
        // (UnattendedPermissionPrompt) and the Auto-mode gate, not from a read-only tool surface.
        var readOnlyDefault = !unattended && hasPrompt && string.IsNullOrWhiteSpace(permissionMode);

        string? forcedMode = null;
        if (readOnlyDefault)
            forcedMode = nameof(PermissionMode.Plan);
        else if (!string.IsNullOrWhiteSpace(permissionMode))
            forcedMode = permissionMode; // Explicit --permissions always wins, even under --unattended
        else if (unattended)
            forcedMode = nameof(PermissionMode.Auto);

        return OneShotPermissionPlan.Valid(readOnlyDefault, forcedMode);
    }
}

/// <summary>
/// The resolved plan for a one-shot run: whether the flag combination was valid, whether the
/// read-only tool restriction applies, and which <c>Permissions:Mode</c> config override (if any)
/// should be forced for this run.
/// </summary>
public sealed record OneShotPermissionPlan(bool IsValid, string? Error, bool ReadOnlyToolsOnly, string? ForcedPermissionMode)
{
    public static OneShotPermissionPlan Invalid(string error) => new(false, error, false, null);

    public static OneShotPermissionPlan Valid(bool readOnlyToolsOnly, string? forcedMode) =>
        new(true, null, readOnlyToolsOnly, forcedMode);
}
