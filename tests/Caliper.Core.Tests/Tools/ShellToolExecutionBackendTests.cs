// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Execution;
using Caliper.Core.Tests.Execution;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Tools;

/// <summary>
/// Covers ShellTool's per-call backend selection (roadmap §3.3 item 7) and the fail-closed rule —
/// LocalToolTests.Shell_tool_captures_output still exercises the real HostExecutionBackend/process
/// path unchanged (proving Backend=Host has no observable behavior change from before this feature).
/// </summary>
public sealed class ShellToolExecutionBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "caliper-shelltool-exec-" + Guid.NewGuid().ToString("N"));

    public ShellToolExecutionBackendTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Container_backend_selected_when_configured_and_host_backend_never_invoked()
    {
        var hostRunner = new FakeProcessRunner();
        var containerRunner = new FakeProcessRunner();
        containerRunner.Enqueue(new ProcessRunResult(0, "")); // docker info probe
        containerRunner.Enqueue(new ProcessRunResult(0, "hi\n")); // docker run
        var runtimeSettings = RuntimeSettingsWithBackend(ExecutionBackendKind.Container);
        var tool = new ShellTool(
            Options.Create(new CaliperOptions { ToolOutputMaxChars = 4000 }),
            "bash",
            new HostExecutionBackend(hostRunner),
            new ContainerExecutionBackend(runtimeSettings, containerRunner, new FakeTimeProvider()),
            runtimeSettings);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "echo hi" }),
            Context(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("hi", result.Output, StringComparison.Ordinal);
        Assert.Empty(hostRunner.Calls);
        Assert.Equal(2, containerRunner.Calls.Count);
    }

    [Fact]
    public async Task Host_backend_selected_by_default_and_container_backend_never_invoked()
    {
        var hostRunner = new FakeProcessRunner();
        hostRunner.Enqueue(new ProcessRunResult(0, "hi\n"));
        var containerRunner = new FakeProcessRunner();
        var runtimeSettings = RuntimeSettingsWithBackend(ExecutionBackendKind.Host);
        var tool = new ShellTool(
            Options.Create(new CaliperOptions { ToolOutputMaxChars = 4000 }),
            "bash",
            new HostExecutionBackend(hostRunner),
            new ContainerExecutionBackend(runtimeSettings, containerRunner, new FakeTimeProvider()),
            runtimeSettings);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "echo hi" }),
            Context(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(hostRunner.Calls);
        Assert.Empty(containerRunner.Calls);
    }

    [Fact]
    public async Task Container_backend_unavailable_fails_closed_with_no_fallback_to_host()
    {
        var hostRunner = new FakeProcessRunner();
        var containerRunner = new FakeProcessRunner();
        containerRunner.Enqueue(new ProcessRunResult(1, "Cannot connect to the Docker daemon"));
        var runtimeSettings = RuntimeSettingsWithBackend(ExecutionBackendKind.Container);
        var tool = new ShellTool(
            Options.Create(new CaliperOptions { ToolOutputMaxChars = 4000 }),
            "bash",
            new HostExecutionBackend(hostRunner),
            new ContainerExecutionBackend(runtimeSettings, containerRunner, new FakeTimeProvider()),
            runtimeSettings);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "echo hi" }),
            Context(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("container backend unavailable", result.Output, StringComparison.Ordinal);
        // Fail-closed: never silently falls back to running the command on the host.
        Assert.Empty(hostRunner.Calls);
    }

    [Fact]
    public async Task Powershell_tool_under_container_backend_fails_closed_with_clear_message()
    {
        var hostRunner = new FakeProcessRunner();
        var containerRunner = new FakeProcessRunner();
        containerRunner.Enqueue(new ProcessRunResult(0, "")); // probe would succeed, never reached
        var runtimeSettings = RuntimeSettingsWithBackend(ExecutionBackendKind.Container);
        var tool = new ShellTool(
            Options.Create(new CaliperOptions { ToolOutputMaxChars = 4000 }),
            "powershell",
            new HostExecutionBackend(hostRunner),
            new ContainerExecutionBackend(runtimeSettings, containerRunner, new FakeTimeProvider()),
            runtimeSettings);

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { command = "Write-Output hi" }),
            Context(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unsupported", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(hostRunner.Calls);
        Assert.Empty(containerRunner.Calls);
    }

    private static RuntimeSettings RuntimeSettingsWithBackend(ExecutionBackendKind backend) =>
        new(
            Options.Create(new CaliperOptions { Execution = new ExecutionOptions { Backend = backend } }),
            Options.Create(new PermissionsOptions()));

    private ToolContext Context() =>
        new(new NullHttpClientFactory(), NullLogger.Instance, ".", _root, allowOutsideWorkingRoot: false, CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
