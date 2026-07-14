// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Scheduling;

/// <summary>
/// Hermetic scheduler-loop tests (roadmap §3.2b): all timing through
/// <see cref="FakeTimeProvider"/>, jobs run against a fake orchestrator + fake session store.
/// The service's internal <c>DelayArmed</c> seam signals when the loop has armed its tick delay,
/// so each <c>Advance</c> is guaranteed to be observed by an armed timer instead of racing loop
/// startup.
/// </summary>
public sealed class SchedulerHostedServiceTests
{
    // Jobs are pinned to UTC so occurrence math never depends on the machine's local zone.
    private static readonly DateTimeOffset s_start = new(2026, 7, 14, 0, 30, 0, TimeSpan.Zero);

    private static ScheduleOptions HourlyJob(string name = "hourly", bool enabled = true) =>
        new()
        {
            Name = name,
            Cron = "0 * * * *",
            TimeZone = "UTC",
            Prompt = "tick",
            Enabled = enabled,
        };

    [Fact]
    public async Task Job_fires_at_the_cron_boundary_exactly_once()
    {
        using var fixture = Fixture.Start(HourlyJob());
        await fixture.WaitForArmedAsync();

        // 00:30 → 01:00 is the next boundary; advancing to it fires the tick timer.
        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await fixture.WaitForArmedAsync();

        Assert.Equal(1, fixture.Orchestrator.RunCount);
        var spec = Assert.Single(fixture.Orchestrator.Specs);
        Assert.True(spec.Unattended);
        Assert.Equal("hourly", spec.JobName);

        // Mid-interval time passes without another occurrence.
        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, fixture.Orchestrator.RunCount);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Overlapping_occurrence_is_skipped_while_previous_run_is_still_going()
    {
        using var fixture = Fixture.Start(HourlyJob("slow"));
        fixture.Orchestrator.BlockRuns = true;
        await fixture.WaitForArmedAsync();

        fixture.Time.Advance(TimeSpan.FromMinutes(30)); // 01:00 — run 1 starts and blocks
        await fixture.WaitForArmedAsync();
        Assert.Equal(1, fixture.Orchestrator.RunCount);

        fixture.Time.Advance(TimeSpan.FromHours(1));    // 02:00 — run 1 still running ⇒ skip
        await fixture.WaitForArmedAsync();

        Assert.Equal(1, fixture.Orchestrator.RunCount);
        Assert.Contains(fixture.RunnerLogger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("skipping", StringComparison.OrdinalIgnoreCase));

        fixture.Orchestrator.ReleaseAll();
        await fixture.StopAsync();
    }

    [Fact]
    public async Task No_catch_up_after_a_downtime_gap_at_most_the_next_occurrence_runs()
    {
        // Simulate downtime: five hourly occurrences elapse before the scheduler ever starts.
        // Misfire policy is next-from-now, so none of them replay.
        using var fixture = Fixture.Build(HourlyJob());
        fixture.Time.Advance(TimeSpan.FromHours(5));    // 05:30, no scheduler running yet
        await fixture.StartAsync();
        await fixture.WaitForArmedAsync();

        Assert.Equal(0, fixture.Orchestrator.RunCount);

        fixture.Time.Advance(TimeSpan.FromMinutes(30)); // 06:00 — the single next occurrence
        await fixture.WaitForArmedAsync();

        Assert.Equal(1, fixture.Orchestrator.RunCount);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Disabled_job_never_runs()
    {
        using var fixture = Fixture.Start(HourlyJob(enabled: false));
        await fixture.WaitForArmedAsync();

        // With no runnable schedule the loop parks on an infinite delay (armed with null).
        Assert.Null(fixture.LastArmedDeadline);

        fixture.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, fixture.Orchestrator.RunCount);

        await fixture.StopAsync();
    }

    [Fact]
    public async Task Never_firing_cron_is_logged_once_and_does_not_spin()
    {
        var job = new ScheduleOptions
        {
            Name = "leap-only",
            Cron = "0 0 30 2 *", // Feb 30 — Cronos returns null: never fires
            TimeZone = "UTC",
            Prompt = "never",
        };
        using var fixture = Fixture.Start(job);
        await fixture.WaitForArmedAsync();

        Assert.Null(fixture.LastArmedDeadline);
        Assert.Equal(1, WarningCount(fixture));

        // Wake the loop via a live settings change: the dead job must not be re-logged and the
        // loop must park again instead of spinning.
        fixture.Runtime.SetModel("still-test");
        await fixture.WaitForArmedAsync();

        Assert.Equal(1, WarningCount(fixture));
        Assert.Equal(0, fixture.Orchestrator.RunCount);

        await fixture.StopAsync();

        static int WarningCount(Fixture fixture) =>
            fixture.ServiceLogger.Entries.Count(entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("no next occurrence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Live_schedule_edit_wakes_the_loop_and_new_job_is_served()
    {
        using var fixture = Fixture.Start(); // no schedules at all: loop parks
        await fixture.WaitForArmedAsync();
        Assert.Null(fixture.LastArmedDeadline);

        fixture.Runtime.UpdateCaliper(options => options.Schedules = [HourlyJob("added")]);
        await fixture.WaitForArmedAsync();

        Assert.NotNull(fixture.LastArmedDeadline); // re-armed against the new job's occurrence

        fixture.Time.Advance(TimeSpan.FromMinutes(30));
        await fixture.WaitForArmedAsync();
        Assert.Equal(1, fixture.Orchestrator.RunCount);

        await fixture.StopAsync();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly TimeSpan s_waitTimeout = TimeSpan.FromSeconds(10);

        private readonly SemaphoreSlim _armed = new(0);

        public required SchedulerHostedService Service { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required RuntimeSettings Runtime { get; init; }
        public required FakeOrchestrator Orchestrator { get; init; }
        public required RecordingLogger<SchedulerHostedService> ServiceLogger { get; init; }
        public required RecordingLogger<ScheduleJobRunner> RunnerLogger { get; init; }

        public DateTimeOffset? LastArmedDeadline { get; private set; }

        public static Fixture Build(params ScheduleOptions[] schedules)
        {
            var time = new FakeTimeProvider(s_start);
            var runtime = new RuntimeSettings(
                Options.Create(new CaliperOptions { Schedules = [.. schedules] }),
                Options.Create(new PermissionsOptions()));
            var orchestrator = new FakeOrchestrator();
            var runnerLogger = new RecordingLogger<ScheduleJobRunner>();
            var runner = new ScheduleJobRunner(orchestrator, new NullSessionStore(), time, runnerLogger);
            var serviceLogger = new RecordingLogger<SchedulerHostedService>();
            var service = new SchedulerHostedService(runner, runtime, time, serviceLogger);
            var fixture = new Fixture
            {
                Service = service,
                Time = time,
                Runtime = runtime,
                Orchestrator = orchestrator,
                ServiceLogger = serviceLogger,
                RunnerLogger = runnerLogger,
            };
            service.DelayArmed = deadline =>
            {
                fixture.LastArmedDeadline = deadline;
                fixture._armed.Release();
            };
            return fixture;
        }

        public static Fixture Start(params ScheduleOptions[] schedules)
        {
            var fixture = Build(schedules);
            fixture.Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return fixture;
        }

        public Task StartAsync() => Service.StartAsync(CancellationToken.None);

        public async Task WaitForArmedAsync()
        {
            Assert.True(
                await _armed.WaitAsync(s_waitTimeout),
                "Timed out waiting for the scheduler loop to arm its tick delay.");
        }

        public async Task StopAsync()
        {
            Orchestrator.ReleaseAll();
            using var cts = new CancellationTokenSource(s_waitTimeout);
            await Service.StopAsync(cts.Token);
        }

        public void Dispose()
        {
            Orchestrator.ReleaseAll();
            Service.Dispose();
            _armed.Dispose();
        }
    }

    private sealed class FakeOrchestrator : IConversationOrchestrator
    {
        private readonly List<RunSpec> _specs = [];
        private readonly List<TaskCompletionSource> _blockers = [];
        private readonly object _gate = new();

        public bool BlockRuns { get; set; }

        public int RunCount
        {
            get
            {
                lock (_gate)
                    return _specs.Count;
            }
        }

        public IReadOnlyList<RunSpec> Specs
        {
            get
            {
                lock (_gate)
                    return [.. _specs];
            }
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var blocker in _blockers)
                    blocker.TrySetResult();
                _blockers.Clear();
            }
        }

        public Task<ConversationRunResult> RunToCompletionAsync(
            string sessionId,
            string prompt,
            Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
            CancellationToken ct) =>
            RunToCompletionAsync(new RunSpec(sessionId, prompt), onEvent, ct);

        public async Task<ConversationRunResult> RunToCompletionAsync(
            RunSpec spec,
            Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
            CancellationToken ct)
        {
            TaskCompletionSource? blocker = null;
            lock (_gate)
            {
                _specs.Add(spec);
                if (BlockRuns)
                {
                    blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _blockers.Add(blocker);
                }
            }

            if (blocker is not null)
                await blocker.Task.WaitAsync(ct);

            return new ConversationRunResult("done", null, CompletionReason.Completed, []);
        }

        public Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NullSessionStore : ISessionStore
    {
        public Task<string> CreateAsync(string? title, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([]);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

        public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        private readonly object _gate = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
