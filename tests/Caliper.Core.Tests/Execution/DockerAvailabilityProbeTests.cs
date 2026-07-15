// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Execution;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Execution;

public sealed class DockerAvailabilityProbeTests
{
    [Fact]
    public async Task ProbeAsync_reflects_docker_info_exit_code()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        var probe = new DockerAvailabilityProbe(runner, new FakeTimeProvider());

        var (available, error) = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(available);
        Assert.Null(error);
        Assert.Equal(["info"], Assert.Single(runner.Calls).Arguments);
    }

    [Fact]
    public async Task ProbeAsync_reports_unavailable_on_nonzero_exit()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(1, "Cannot connect to the Docker daemon"));
        var probe = new DockerAvailabilityProbe(runner, new FakeTimeProvider());

        var (available, error) = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(available);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ProbeAsync_reports_unavailable_when_docker_is_not_installed()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new System.ComponentModel.Win32Exception("The system cannot find the file specified."));
        var probe = new DockerAvailabilityProbe(runner, new FakeTimeProvider());

        var (available, error) = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(available);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ProbeAsync_caches_the_result_within_the_ttl()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        var time = new FakeTimeProvider();
        var probe = new DockerAvailabilityProbe(runner, time);

        await probe.ProbeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await probe.ProbeAsync(CancellationToken.None);

        // Second call within the TTL must not re-shell to docker.
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ProbeAsync_reprobes_after_the_ttl_expires()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        runner.Enqueue(new ProcessRunResult(0, ""));
        var time = new FakeTimeProvider();
        var probe = new DockerAvailabilityProbe(runner, time);

        await probe.ProbeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task ProbeAsync_a_stale_negative_result_self_heals_after_the_ttl()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(1, "daemon not running"));
        runner.Enqueue(new ProcessRunResult(0, ""));
        var time = new FakeTimeProvider();
        var probe = new DockerAvailabilityProbe(runner, time);

        var first = await probe.ProbeAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        var second = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(first.Available);
        Assert.True(second.Available);
    }

    /// <summary>
    /// A7: concurrent cold callers must single-flight onto exactly one <c>docker info</c> shell-out.
    /// Uses a <see cref="TaskCompletionSource{TResult}"/>-gated fake (not <c>Task.Delay</c>) so the
    /// "first caller is mid-probe, everyone else is queued behind it" state is reached
    /// deterministically: the gated fake's <c>RunAsync</c> records the call and then returns an
    /// uncompleted task, so the first <see cref="DockerAvailabilityProbe.ProbeAsync"/> call is
    /// guaranteed to have recorded its call — and suspended — before this method's synchronous
    /// continuation resumes, and every subsequent concurrent call is guaranteed to be parked on the
    /// probe's internal semaphore (never reaching the runner) until the gate is released.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_single_flights_concurrent_cold_calls()
    {
        var runner = new GatedProcessRunner();
        var probe = new DockerAvailabilityProbe(runner, new FakeTimeProvider());

        var first = probe.ProbeAsync(CancellationToken.None);
        await runner.Started;

        var others = new Task<(bool Available, string? Error)>[5];
        for (var i = 0; i < others.Length; i++)
            others[i] = probe.ProbeAsync(CancellationToken.None);

        // Every concurrent caller is either the in-flight first probe or parked behind the
        // single-flight semaphore — none of them may have reached the runner yet.
        Assert.Single(runner.Calls);

        runner.Complete(new ProcessRunResult(0, ""));
        var firstResult = await first;
        var otherResults = await Task.WhenAll(others);

        // Still exactly one shell-out total: the queued callers reused the now-cached result
        // instead of re-probing.
        Assert.Single(runner.Calls);
        Assert.True(firstResult.Available);
        Assert.All(otherResults, r => Assert.True(r.Available));
    }

    /// <summary>Records its call, signals <see cref="Started"/>, then blocks until <see cref="Complete"/>.</summary>
    private sealed class GatedProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<ProcessRunResult> _resultSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startedSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ProcessRunSpec> Calls { get; } = [];

        public Task Started => _startedSource.Task;

        public void Complete(ProcessRunResult result) => _resultSource.TrySetResult(result);

        public Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            _startedSource.TrySetResult();
            return _resultSource.Task;
        }
    }
}
