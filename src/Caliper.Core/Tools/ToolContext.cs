// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Tools;

/// <summary>Ambient services injected into every tool invocation.</summary>
public sealed class ToolContext(
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    string skillsRootPath,
    string workingRoot,
    bool allowOutsideWorkingRoot,
    CancellationToken cancellationToken,
    string sessionId = "",
    string callId = "",
    int subagentDepth = 0,
    SubagentRunState? subagentState = null,
    PermissionsOptions? permissionsOverlay = null,
    bool unattended = false)
{
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public ILogger Logger { get; } = logger;
    public string SkillsRootPath { get; } = skillsRootPath;
    public string WorkingRoot { get; } = workingRoot;
    public bool AllowOutsideWorkingRoot { get; } = allowOutsideWorkingRoot;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>The id of the session running this dispatch (roadmap §3.1: subagent child titling).</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>The current tool call's id, so a tool can correlate events it emits (see <see cref="Emit"/>).</summary>
    public string CallId { get; } = callId;

    /// <summary>0 for a top-level run; N for a run nested N subagent levels deep.</summary>
    public int SubagentDepth { get; } = subagentDepth;

    /// <summary>
    /// Per-run child-spawn counter threaded through every dispatch in one <c>AgentRunner.RunAsync</c>
    /// call (see <see cref="SubagentRunState"/> for why this — not a process-lifetime dictionary —
    /// is the child-count guard's home).
    /// </summary>
    public SubagentRunState SubagentState { get; } = subagentState ?? new SubagentRunState();

    /// <summary>
    /// This run's permission overlay (<c>RunSpec.PermissionsOverlay</c>), or null to mean "use
    /// <c>runtimeSettings.Permissions</c>". Exposed so a tool that spawns its own nested run (the
    /// subagent tool) can compute a restrict-only child overlay from the parent's *actual*
    /// effective mode, not just the process-global default — required for non-escalation to hold
    /// across nested spawns (a child of a Plan-mode child must itself stay Plan).
    /// </summary>
    public PermissionsOptions? PermissionsOverlay { get; } = permissionsOverlay;

    /// <summary>
    /// True when this dispatch belongs to an unattended run (<c>RunSpec.Unattended</c>). Exposed
    /// so a tool that spawns nested runs (the subagent tool) can propagate the flag to its child
    /// spec — a subagent spawned by a scheduled job must keep routing its permission prompts to
    /// the deny+report path, even when the host's registered prompt is interactive.
    /// </summary>
    public bool Unattended { get; } = unattended;

    private readonly List<AgentEvent> _emittedEvents = [];

    /// <summary>
    /// Lets a tool surface extra <see cref="AgentEvent"/>s from inside <c>InvokeAsync</c> even
    /// though a tool's signature only returns a <c>ToolResult</c> (it isn't an async-enumerable
    /// like <c>AgentRunner.RunAsync</c>). Buffered here and drained/yielded by
    /// <c>AgentRunner</c> immediately after the dispatch that owns this context returns — see the
    /// <c>SubagentStarted</c>/<c>SubagentCompleted</c> events raised by the <c>task</c> tool. This
    /// is the blessed general mechanism (over a one-off special case in <c>AgentRunner</c> for the
    /// subagent tool specifically) so any future tool needing to raise ad hoc events can reuse it.
    /// </summary>
    public void Emit(AgentEvent evt) => _emittedEvents.Add(evt);

    /// <summary>Drains and clears the events buffered by <see cref="Emit"/> since the last drain.</summary>
    public IReadOnlyList<AgentEvent> DrainEmittedEvents()
    {
        if (_emittedEvents.Count == 0)
            return [];

        var events = _emittedEvents.ToArray();
        _emittedEvents.Clear();
        return events;
    }
}

/// <summary>
/// Counts children spawned by one run so <c>SubagentTool</c> can enforce
/// <c>SubagentsOptions.MaxChildrenPerRun</c> without process-lifetime bookkeeping. A fresh
/// instance is created once per <c>AgentRunner.RunAsync(RunSpec, CancellationToken)</c> call and
/// threaded through every <see cref="ToolContext"/> built for that run's tool dispatches (see
/// <see cref="ToolContext.SubagentState"/>), so the count is scoped exactly to "this run" — a
/// nested child run gets its own fresh instance from its own <c>RunAsync</c> call — and is
/// garbage-collected along with the run itself once it completes; nothing accumulates for the
/// life of the (singleton) tool or the process.
/// </summary>
public sealed class SubagentRunState
{
    private int _childCount;

    /// <summary>Increments and returns the new count; the caller compares it against the configured limit.</summary>
    public int IncrementAndGetChildCount() => Interlocked.Increment(ref _childCount);

    /// <summary>
    /// Hands back a slot reserved by <see cref="IncrementAndGetChildCount"/> when a spawn attempt
    /// fails <b>before the child run actually starts</b> — the over-limit rejection itself, or the
    /// child session-creation throwing (A2). The increment deliberately happens before any
    /// expensive work so concurrent attempts can only transiently overshoot the limit; releasing
    /// the slot on those early failures keeps the count meaning "children that actually started",
    /// so a failed attempt doesn't permanently consume one of the run's <c>MaxChildrenPerRun</c>
    /// slots. A child run that starts and then fails/times out must <b>not</b> be decremented —
    /// it really ran (and may have done work) as one of this run's children.
    /// </summary>
    public void DecrementChildCount() => Interlocked.Decrement(ref _childCount);
}
