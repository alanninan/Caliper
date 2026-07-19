// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

/// <summary>
/// Roadmap §3.4 durable execution: tracks the lifecycle of orchestrator-driven runs in a SQLite
/// <c>runs</c> table so a crash or kill mid-run can be recovered from instead of silently lost.
/// <c>ConversationOrchestrator</c> owns every write (creation, per-step bumps, terminal status,
/// resume) — <c>AgentRunner</c> itself stays unaware of run tracking.
///
/// <para><b>Scope — which runs are tracked.</b> Only runs driven through
/// <see cref="IConversationOrchestrator"/>'s <c>RunToCompletionAsync</c>/<c>ResumeAsync</c>: one-shot
/// runs, <c>--unattended</c> runs, scheduler job ticks, <c>/schedule run</c> manual triggers, and
/// subagent child runs (the <c>task</c> tool resolves the orchestrator and calls it the same way).
/// The interactive REPL (<c>Caliper.Console</c>'s chat loop) and <c>Caliper.App</c>'s chat turns call
/// <see cref="IAgentRunner"/>.<c>RunAsync</c> directly and are deliberately <b>not</b> tracked — those
/// are short, human-supervised runs where a person watching the terminal/UI is the recovery
/// mechanism already; this feature targets long/unattended runs where nobody is watching.</para>
/// </summary>
public interface IRunStore
{
    /// <summary>Creates a new <see cref="RunStatus.Running"/> row and returns its generated run id.</summary>
    Task<string> StartAsync(string sessionId, string? jobName, int maxSteps, bool unattended, CancellationToken ct);

    /// <summary>Records the current step number and bumps <c>updated_at</c>. Called on every <c>TurnStarted</c>.</summary>
    Task UpdateStepAsync(string runId, int stepNumber, CancellationToken ct);

    /// <summary>Writes a terminal status and an optional reason (a <c>CompletionReason</c> name or an error message).</summary>
    Task CompleteAsync(string runId, RunStatus status, string? reason, CancellationToken ct);

    /// <summary>
    /// Flips an <see cref="RunStatus.Interrupted"/> row back to <see cref="RunStatus.Running"/> with
    /// the (typically smaller) remaining step budget, ahead of
    /// <c>ConversationOrchestrator.ResumeAsync</c> driving the run further.
    /// </summary>
    Task MarkResumedAsync(string runId, int maxSteps, CancellationToken ct);

    Task<RunRecord?> GetAsync(string runId, CancellationToken ct);

    /// <summary>Most recently updated runs first.</summary>
    Task<IReadOnlyList<RunRecord>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Most recently updated scheduled runs first. A scheduled run is identified by its non-null
    /// originating job name; filtering happens in the store so unrelated one-shot and subagent
    /// rows cannot consume the requested history limit.
    /// </summary>
    Task<IReadOnlyList<RunRecord>> ListRecentScheduledAsync(int limit, CancellationToken ct);
}
