// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Execution;

namespace Caliper.Core.Tests.Execution;

public sealed class HostExecutionBackendTests
{
    [Fact]
    public async Task ExecuteAsync_bash_shell_passes_dash_c_and_command_via_argument_list()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, "hello\n"));
        var backend = new HostExecutionBackend(runner);

        var result = await backend.ExecuteAsync(
            new ShellExecutionRequest("bash", "echo hello", "/work", "/work", TimeSpan.FromSeconds(60), ["CALIPER_"], 4096),
            CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("bash", call.FileName);
        Assert.Equal(["-c", "echo hello"], call.Arguments);
        Assert.Equal("/work", call.WorkingDirectory);
        Assert.Equal(["CALIPER_"], call.EnvironmentScrubPrefixes);
        Assert.Equal(4096, call.OutputBufferCapChars);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello\n", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_powershell_shell_passes_noprofile_command_flags()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(0, "hi\n"));
        var backend = new HostExecutionBackend(runner);

        await backend.ExecuteAsync(
            new ShellExecutionRequest("powershell", "Write-Output hi", "/work", "/work", null, ["CALIPER_"], 4096),
            CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-NoProfile", "-Command", "Write-Output hi"], call.Arguments);
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh", call.FileName);
    }

    [Fact]
    public async Task ExecuteAsync_returns_the_runners_exit_code_and_output_unchanged()
    {
        var runner = new FakeProcessRunner();
        runner.Enqueue(new ProcessRunResult(7, "stderr: boom\n"));
        var backend = new HostExecutionBackend(runner);

        var result = await backend.ExecuteAsync(
            new ShellExecutionRequest("bash", "false", "/work", "/work", null, [], 100),
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("stderr: boom\n", result.Output);
    }
}
