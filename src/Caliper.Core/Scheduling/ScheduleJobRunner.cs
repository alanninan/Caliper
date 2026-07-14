// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.Concurrent;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Scheduling;

/// <summary>
/// Runs one scheduled job occurrence end to end (roadmap §3.2b): creates a session titled
/// <c>[job] {name}</c>, builds the unattended <see cref="RunSpec"/> (prompt, model, step budget,
/// permissions overlay, working root, <c>JobName</c>, <c>Unattended = true</c>), drains the run
/// through <see cref="IConversationOrchestrator"/>, and records a one-line summary + denial count.
/// </summary>
/// <remarks>
/// This is the single execution path shared by <see cref="SchedulerHostedService"/> ticks under
/// <c>--serve</c> and the console's manual <c>/schedule run &lt;name&gt;</c> trigger, so a manual
/// run exercises exactly what the cron tick would (roadmap §7 Q5: the manual trigger doubles as a
/// dry-run harness for a job's allowlist). Because the spec sets <c>Unattended = true</c>, the
/// run's permission prompts are denied+recorded by <c>UnattendedPermissionPrompt</c> — directly
/// under <c>--serve</c>, or via <c>RoutingPermissionPrompt</c> inside the interactive REPL.
/// Overlap policy: a per-job <see cref="SemaphoreSlim"/>(1) taken with <c>Wait(0)</c> — if the
/// previous occurrence is still running (or queued on the cross-job gate), this occurrence is
/// logged and skipped, never queued.
/// </remarks>
public sealed class ScheduleJobRunner(
    IConversationOrchestrator orchestrator,
    ISessionStore sessions,
    TimeProvider timeProvider,
    ILogger<ScheduleJobRunner> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _jobLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScheduleRunOutcome> _lastResults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most recent (non-skipped) outcome for a job this process observed, or null.</summary>
    public ScheduleRunOutcome? GetLastResult(string jobName) =>
        _lastResults.TryGetValue(jobName, out var outcome) ? outcome : null;

    /// <summary>
    /// Runs one occurrence of <paramref name="job"/>. <paramref name="concurrencyGate"/> is the
    /// scheduler's cross-job <c>MaxConcurrentJobs</c> semaphore (null for manual triggers, which
    /// aren't subject to it — the scheduler isn't running in REPL mode). The per-job overlap check
    /// happens synchronously before any queuing on the gate, so an occurrence firing while its
    /// predecessor is merely *queued* still skips rather than piling up.
    /// </summary>
    public async Task<ScheduleRunOutcome> RunJobAsync(
        ScheduleOptions job,
        SemaphoreSlim? concurrencyGate,
        Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
        CancellationToken ct)
    {
        var jobLock = _jobLocks.GetOrAdd(job.Name, static _ => new SemaphoreSlim(1, 1));
        if (!jobLock.Wait(0, CancellationToken.None))
        {
            logger.LogWarning(
                "[job] {Job}: previous occurrence still running; skipping this occurrence (overlap policy: skip).",
                job.Name);
            return new ScheduleRunOutcome(
                job.Name, timeProvider.GetUtcNow(), Reason: null, Error: null, DenialCount: 0, Skipped: true);
        }

        try
        {
            if (concurrencyGate is not null)
                await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                return await RunLockedAsync(job, onEvent, ct).ConfigureAwait(false);
            }
            finally
            {
                concurrencyGate?.Release();
            }
        }
        finally
        {
            jobLock.Release();
        }
    }

    private async Task<ScheduleRunOutcome> RunLockedAsync(
        ScheduleOptions job,
        Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
        CancellationToken ct)
    {
        var sessionId = await sessions.CreateAsync($"[job] {job.Name}", ct).ConfigureAwait(false);
        var spec = new RunSpec(sessionId, job.Prompt)
        {
            Model = string.IsNullOrWhiteSpace(job.Model) ? null : job.Model,
            MaxSteps = job.MaxSteps,
            PermissionsOverlay = job.Permissions,
            WorkingRoot = job.WorkingRoot,
            JobName = job.Name,
            Unattended = true,
        };

        ConversationRunResult result;
        try
        {
            result = await orchestrator.RunToCompletionAsync(spec, onEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RunToCompletionAsync folds run-level failures/cancellation into its result, so this
            // only fires for something genuinely unexpected (e.g. a store error creating the
            // transcript). Record it so /schedule list can show what happened.
            logger.LogError(ex, "[job] {Job}: run failed unexpectedly.", job.Name);
            var failed = new ScheduleRunOutcome(
                job.Name, timeProvider.GetUtcNow(), Reason: null, Error: ex.Message, DenialCount: 0, Skipped: false);
            _lastResults[job.Name] = failed;
            return failed;
        }

        var outcome = new ScheduleRunOutcome(
            job.Name,
            timeProvider.GetUtcNow(),
            result.Reason,
            result.Error,
            result.Denials.Count,
            Skipped: false);
        _lastResults[job.Name] = outcome;

        // Unattended contract (roadmap §3.2a): deny + report, never silent-drop. Each denial was
        // already logged by UnattendedPermissionPrompt as it happened; this is the per-run summary.
        if (outcome.DenialCount > 0)
        {
            logger.LogWarning(
                "[job] {Job} finished: {Reason}; denied {Denials} action(s). Session {Session}. {Error}",
                job.Name, outcome.Reason?.ToString() ?? "Error", outcome.DenialCount, sessionId, outcome.Error ?? string.Empty);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[job] {Job} finished: {Reason}. Session {Session}. {Error}",
                job.Name, outcome.Reason?.ToString() ?? "Error", sessionId, outcome.Error ?? string.Empty);
        }

        return outcome;
    }
}
