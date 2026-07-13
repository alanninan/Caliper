// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Core.Agents;

/// <summary>
/// Scopes a single run: model, tool surface, permission overlay, step budget, subagent depth,
/// and job identity — without mutating the process-global <c>IRuntimeSettings</c> (which would
/// race concurrent runs, e.g. the App running session A while viewing session B, or the
/// scheduler running several jobs at once). Every optional member null/default means "fall back
/// to runtime settings / global options"; see the fallbacks applied in
/// <see cref="AgentRunner.RunAsync(RunSpec, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed record RunSpec(string SessionId, string Prompt)
{
    /// <summary>Model slug for this run. Null falls back to <c>runtimeSettings.Caliper.Model</c>.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Restricts the tool surface to these names (case-insensitive). Null means the full enabled
    /// surface. A call naming a tool outside this filter is resolved the same way an unknown tool
    /// is: the built-in registry lookup returns null.
    /// </summary>
    public IReadOnlyList<string>? ToolFilter { get; init; }

    /// <summary>
    /// Per-run permission overlay. Null falls back to <c>runtimeSettings.Permissions</c>. When
    /// present, the global shell denylist is still merged in (union, never replaced) by
    /// <c>PermissionGate</c> — an overlay can restrict further but never lift the global ban.
    /// </summary>
    public PermissionsOptions? PermissionsOverlay { get; init; }

    /// <summary>Step budget for this run. Null falls back to <c>runtimeSettings.Caliper.MaxSteps</c>.</summary>
    public int? MaxSteps { get; init; }

    /// <summary>0 for a top-level run; incremented per subagent nesting level by the caller.</summary>
    public int SubagentDepth { get; init; }

    /// <summary>Set by the scheduler for job runs; null for interactive runs.</summary>
    public string? JobName { get; init; }
}
