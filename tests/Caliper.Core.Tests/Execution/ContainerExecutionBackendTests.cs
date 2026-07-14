// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Execution;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Execution;

public sealed class ContainerExecutionBackendTests
{
    private static readonly string s_workingRoot = Path.Combine(Path.GetTempPath(), "caliper-container-root");

    [Fact]
    public async Task ExecuteAsync_builds_docker_run_arguments_from_execution_options()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, "")); // docker info probe
        runner.Enqueue(new ProcessRunResult(0, "ok\n")); // docker run
        var execution = new ExecutionOptions
        {
            Image = "mcr.microsoft.com/dotnet/sdk:10.0",
            Network = ExecutionNetworkKind.None,
            Cpus = 1.5,
            MemoryMb = 2048,
            User = "1000",
        };
        var backend = Build(runner, execution);

        var result = await backend.ExecuteAsync(
            Request("echo hi", s_workingRoot, s_workingRoot),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok\n", result.Output);
        var args = runner.Calls[1].Arguments.ToList();
        Assert.Equal("docker", runner.Calls[1].FileName);
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Equal("--network", args[4]);
        Assert.Equal("none", args[5]);
        Assert.Equal("2048m", args[args.IndexOf("--memory") + 1]);
        Assert.Equal("1.5", args[args.IndexOf("--cpus") + 1]);
        Assert.Equal("1000", args[args.IndexOf("--user") + 1]);
        Assert.Contains($"{s_workingRoot}:/workspace", args);
        Assert.Equal("/workspace", args[args.IndexOf("-w") + 1]);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", args[^4]);
        Assert.Equal("bash", args[^3]);
        Assert.Equal("-lc", args[^2]);
        Assert.Equal("echo hi", args[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_maps_bridge_network()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        runner.Enqueue(new ProcessRunResult(0, ""));
        var backend = Build(runner, new ExecutionOptions { Network = ExecutionNetworkKind.Bridge });

        await backend.ExecuteAsync(Request("true", s_workingRoot, s_workingRoot), CancellationToken.None);

        var args = runner.Calls[1].Arguments.ToList();
        Assert.Equal("bridge", args[args.IndexOf("--network") + 1]);
    }

    [Fact]
    public async Task ExecuteAsync_maps_a_subdirectory_cwd_relative_to_the_workspace_mount()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        runner.Enqueue(new ProcessRunResult(0, ""));
        var backend = Build(runner, new ExecutionOptions());
        var cwd = Path.Combine(s_workingRoot, "sub", "dir");

        await backend.ExecuteAsync(Request("true", cwd, s_workingRoot), CancellationToken.None);

        var args = runner.Calls[1].Arguments.ToList();
        Assert.Equal("/workspace/sub/dir", args[args.IndexOf("-w") + 1]);
    }

    [Fact]
    public void MapCwdToContainer_rejects_a_cwd_outside_the_working_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "caliper-outside-root");

        var ex = Assert.Throws<InvalidOperationException>(() => ContainerExecutionBackend.MapCwdToContainer(s_workingRoot, outside));
        Assert.Contains("outside the mounted working root", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_cwd_that_escapes_the_working_root()
    {
        var runner = new FakeProcessRunner();
        var backend = Build(runner, new ExecutionOptions());
        var outside = Path.Combine(Path.GetTempPath(), "caliper-outside-root-2");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.ExecuteAsync(Request("true", outside, s_workingRoot), CancellationToken.None));

        // The escape is caught before any process (including the docker probe) is launched.
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_powershell_without_probing_or_running_docker()
    {
        var runner = new FakeProcessRunner();
        var backend = Build(runner, new ExecutionOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.ExecuteAsync(
                new ShellExecutionRequest("powershell", "Write-Output hi", s_workingRoot, s_workingRoot, null, [], 4096),
                CancellationToken.None));

        Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_fails_closed_when_docker_is_unavailable_and_never_runs_docker_run()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(1, "Cannot connect to the Docker daemon"));
        var backend = Build(runner, new ExecutionOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.ExecuteAsync(Request("echo hi", s_workingRoot, s_workingRoot), CancellationToken.None));

        Assert.Contains("container backend unavailable", ex.Message, StringComparison.Ordinal);
        // Only the probe ran ("docker info") — never "docker run".
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["info"], call.Arguments);
    }

    [Fact]
    public async Task ExecuteAsync_kills_the_container_on_cancellation()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, "")); // probe succeeds
        runner.Enqueue(new OperationCanceledException()); // docker run is cancelled
        var backend = Build(runner, new ExecutionOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.ExecuteAsync(Request("sleep 100", s_workingRoot, s_workingRoot), cts.Token));

        Assert.Equal(3, runner.Calls.Count);
        var killCall = runner.Calls[2];
        Assert.Equal("docker", killCall.FileName);
        var killArgs = killCall.Arguments.ToList();
        Assert.Equal("kill", killArgs[0]);
        var runContainerName = runner.Calls[1].Arguments.ToList()[runner.Calls[1].Arguments.ToList().IndexOf("--name") + 1];
        Assert.Equal(runContainerName, killArgs[1]);
    }

    [Fact]
    public async Task ExecuteAsync_generates_a_unique_container_name_per_invocation()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, ""));
        runner.Enqueue(new ProcessRunResult(0, ""));
        runner.Enqueue(new ProcessRunResult(0, ""));
        var backend = Build(runner, new ExecutionOptions());

        await backend.ExecuteAsync(Request("true", s_workingRoot, s_workingRoot), CancellationToken.None);
        await backend.ExecuteAsync(Request("true", s_workingRoot, s_workingRoot), CancellationToken.None);

        var args1 = runner.Calls[1].Arguments.ToList();
        var args2 = runner.Calls[2].Arguments.ToList();
        var name1 = args1[args1.IndexOf("--name") + 1];
        var name2 = args2[args2.IndexOf("--name") + 1];
        Assert.NotEqual(name1, name2);
    }

    private static ContainerExecutionBackend Build(FakeProcessRunner runner, ExecutionOptions execution)
    {
        IRuntimeSettings runtimeSettings = new RuntimeSettings(
            Options.Create(new CaliperOptions { Execution = execution }),
            Options.Create(new PermissionsOptions()));
        return new ContainerExecutionBackend(runtimeSettings, runner, new FakeTimeProvider());
    }

    private static ShellExecutionRequest Request(string command, string cwd, string workingRoot) =>
        new("bash", command, cwd, workingRoot, TimeSpan.FromSeconds(60), ["CALIPER_"], 4096);
}
