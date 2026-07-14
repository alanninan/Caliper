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
}
