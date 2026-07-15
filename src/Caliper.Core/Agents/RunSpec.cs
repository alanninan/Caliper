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

    /// <summary>
    /// Per-run working root (roadmap §3.2b: scheduled jobs run in their own root). Null falls back
    /// to <c>runtimeSettings.Caliper.WorkingRoot</c>. Threaded into every <c>ToolContext</c> this
    /// run builds and onto <c>PermissionRequest.WorkingRoot</c>, so both file-tool path resolution
    /// and <c>PermissionGate</c>'s <c>FileAccessPolicy</c> evaluate against this run's root.
    /// </summary>
    public string? WorkingRoot { get; init; }

    /// <summary>
    /// True for runs that must never wait on a human (scheduled jobs and <c>/schedule run</c>
    /// manual triggers). Threaded onto <c>PermissionRequest.Unattended</c> so a
    /// <c>RoutingPermissionPrompt</c> can send this run's prompts to the deny+report
    /// <c>UnattendedPermissionPrompt</c> even inside an interactive host. <c>PermissionGate</c>
    /// itself is unchanged by this flag.
    /// </summary>
    public bool Unattended { get; init; }

    /// <summary>
    /// Roadmap §3.4 durable execution resume seam. When true, <c>AgentRunner.RunAsync</c> skips its
    /// normal prologue append of <see cref="Prompt"/> as a new user message — a resumed run's
    /// transcript is already valid (healed by <c>NativeToolStrategy.BuildMessages</c>'s dangling-call
    /// healing) and must not gain a second "new turn" on top of where it left off.
    /// <see cref="Prompt"/> is still used every turn to render the "## Current task" section of the
    /// system prompt (see <c>PromptBuilder.Build</c>), so <c>ConversationOrchestrator.ResumeAsync</c>
    /// still populates it — with the job's current prompt if <see cref="JobName"/> resolves, or the
    /// session's original first user message otherwise — it just isn't re-appended to history.
    /// Default <see langword="false"/>, so every existing call site (which never sets this) keeps
    /// today's append-then-run behavior unchanged.
    /// </summary>
    public bool ResumeExisting { get; init; }
}
