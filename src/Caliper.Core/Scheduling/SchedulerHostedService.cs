// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Scheduling;

/// <summary>
/// The cron scheduler (roadmap §3.2b), registered only by headless entry points (the console's
/// <c>--serve</c> flag). Loop shape per tick: run whatever came due while sleeping, recompute
/// every enabled job's next occurrence <b>from now</b> (misfire policy: no catch-up — occurrences
/// missed while the process was down or busy are skipped, never replayed), then sleep until the
/// earliest occurrence. All time math goes through <see cref="TimeProvider"/> (the house rule for
/// new time-sensitive code): <c>GetUtcNow()</c> plus the
/// <c>Task.Delay(delay, timeProvider, ct)</c> overload, so tests drive the whole loop with
/// <c>FakeTimeProvider</c>.
/// </summary>
/// <remarks>
/// Liveness of config: the job list is re-read from <see cref="IRuntimeSettings"/> at the top of
/// every tick, and the tick sleep subscribes to <see cref="IRuntimeSettings.SettingsChanged"/> —
/// a live settings mutation (e.g. <c>SaveSchedulesAsync</c>) cancels the sleep so the new list is
/// picked up immediately instead of after the old earliest-occurrence delay expires. Two Cronos
/// behaviors handled explicitly: a null next occurrence (expression can never fire again, e.g.
/// <c>0 0 30 2 *</c>) is logged once per job and treated as disabled without spinning; DST
/// fall-back double-fires of interval expressions are accepted (Cronos's documented semantics —
/// the per-job overlap guard in <see cref="ScheduleJobRunner"/> serializes the job regardless).
/// Cancellation chains host shutdown ⇒ this loop ⇒ in-flight job runs ⇒ child runs/tools, and
/// shutdown awaits in-flight jobs so they unwind through the normal cancelled-run path.
/// </remarks>
public sealed class SchedulerHostedService(
    ScheduleJobRunner jobRunner,
    IRuntimeSettings runtimeSettings,
    TimeProvider timeProvider,
    ILogger<SchedulerHostedService> logger) : BackgroundService
{
    // A cap on any single sleep so a far-future occurrence (a yearly cron can be ~366 days out,
    // past Task.Delay's ~49-day limit) never overflows the timer; the loop just re-arms.
    private static readonly TimeSpan s_maxSleep = TimeSpan.FromDays(1);

    /// <summary>
    /// Test seam: invoked with the wake deadline (null = idle, no runnable schedules) right before
    /// the loop awaits its tick delay, so hermetic tests know exactly when the FakeTimeProvider
    /// timer is armed and an <c>Advance</c> will be observed. Never used in production wiring.
    /// </summary>
    internal Action<DateTimeOffset?>? DelayArmed { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupOptions = runtimeSettings.Caliper;
        // Bound once at service start (restart-required; see SchedulerOptions doc comment).
        var maxConcurrentJobs = Math.Max(1, startupOptions.Scheduler.MaxConcurrentJobs);
        using var concurrencyGate = new SemaphoreSlim(maxConcurrentJobs, maxConcurrentJobs);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Scheduler started; serving {Count} enabled schedule(s); MaxConcurrentJobs={Max}.",
                startupOptions.Schedules.Count(schedule => schedule.Enabled),
                maxConcurrentJobs);
        }

        var deadJobLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningJobs = new List<Task>();
        // Occurrences computed on the previous tick; a job is due when its recorded occurrence
        // has been reached by the time we wake.
        var pendingOccurrences = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                runningJobs.RemoveAll(static task => task.IsCompleted);

                var now = timeProvider.GetUtcNow();
                // Fresh clone every tick — the live seam that makes saved schedule edits apply
                // without a restart.
                var schedules = runtimeSettings.Caliper.Schedules;

                // 1) Run whatever came due while we slept.
                foreach (var job in schedules)
                {
                    if (job.Enabled &&
                        pendingOccurrences.TryGetValue(job.Name, out var dueAt) &&
                        dueAt <= now)
                    {
                        runningJobs.Add(RunJobGuardedAsync(job, concurrencyGate, stoppingToken));
                    }
                }

                // 2) Recompute next occurrences strictly from now — this is the no-catch-up
                //    misfire policy: on startup or after any gap, only future occurrences count.
                pendingOccurrences.Clear();
                DateTimeOffset? earliest = null;
                foreach (var job in schedules)
                {
                    if (!job.Enabled)
                        continue;

                    var next = ScheduleCron.GetNextOccurrence(job, now, out var error);
                    if (next is null)
                    {
                        // Log once per job (invalid config or an expression that can never fire
                        // again); the job is treated as disabled — no spin, the idle sleep below
                        // still parks the loop.
                        if (deadJobLogged.Add(job.Name))
                        {
                            logger.LogWarning(
                                "[job] {Job} has no next occurrence ({Detail}); treating it as disabled.",
                                job.Name,
                                error ?? "the cron expression never fires again");
                        }

                        continue;
                    }

                    deadJobLogged.Remove(job.Name);
                    pendingOccurrences[job.Name] = next.Value;
                    if (earliest is null || next.Value < earliest.Value)
                        earliest = next.Value;
                }

                // 3) Sleep until the earliest occurrence (capped), or park indefinitely when
                //    nothing can run; a SettingsChanged wake re-enters the loop for either case.
                var delay = earliest is { } wakeAt
                    ? Clamp(wakeAt - timeProvider.GetUtcNow())
                    : Timeout.InfiniteTimeSpan;

                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                void OnSettingsChanged(object? sender, EventArgs args)
                {
                    try
                    {
                        wake.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // A change raced the end of this tick; the next tick re-reads anyway.
                    }
                }

                runtimeSettings.SettingsChanged += OnSettingsChanged;
                // Signal the test seam only once the SettingsChanged wake is wired, so "armed"
                // reliably means both the timer and the live-config wake path are active.
                DelayArmed?.Invoke(earliest);
                try
                {
                    if (delay != TimeSpan.Zero)
                        await Task.Delay(delay, timeProvider, wake.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Woken by a live settings change; loop to re-read the schedule list now.
                }
                finally
                {
                    runtimeSettings.SettingsChanged -= OnSettingsChanged;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        finally
        {
            // Host shutdown: wait for in-flight jobs — their runs observe stoppingToken and
            // unwind through the agent loop's cancelled-run handling (synthetic tool results,
            // Cancelled completion), so we must not abandon them mid-flight.
            try
            {
                await Task.WhenAll(runningJobs).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunJobGuardedAsync(
        ScheduleOptions job,
        SemaphoreSlim concurrencyGate,
        CancellationToken ct)
    {
        try
        {
            await jobRunner.RunJobAsync(job, concurrencyGate, onEvent: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown while queued/running; nothing to record.
        }
        catch (Exception ex)
        {
            // RunJobAsync already records and logs run-level failures; this is the last-resort
            // guard so one broken job can never take the scheduler loop down.
            logger.LogError(ex, "[job] {Job}: scheduling wrapper failed unexpectedly.", job.Name);
        }
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : delay > s_maxSleep ? s_maxSleep : delay;
}
