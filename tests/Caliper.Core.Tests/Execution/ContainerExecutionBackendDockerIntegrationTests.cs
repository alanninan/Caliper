// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Execution;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Execution;

/// <summary>
/// Opt-in integration test: exercises the real <c>docker</c> CLI end to end — the public
/// (non-test) <see cref="ContainerExecutionBackend"/> constructor, the real
/// <see cref="SystemProcessRunner"/>, and an actual <c>docker run</c>. Automatically skipped (a
/// trivial pass, no assertions run) when Docker isn't available in the environment running the
/// tests. This repo has no SkippableFact-style package (xUnit v2, per CLAUDE.md — no new NuGet
/// packages for this either), so this follows the roadmap's documented fallback: "a runtime skip
/// via the probe," reusing the exact <see cref="DockerAvailabilityProbe"/> the production backend
/// uses, rather than inventing a second way to detect Docker.
/// </summary>
public sealed class ContainerExecutionBackendDockerIntegrationTests
{
    // A small, official, bash-preinstalled image (unlike the Caliper.Options default
    // mcr.microsoft.com/dotnet/sdk:10.0, which is large) so this test pulls quickly on a machine
    // that has Docker but not that image cached.
    private const string TestImage = "bash:latest";

    [Fact]
    public async Task Real_docker_run_echoes_a_command_round_trip()
    {
        var probe = new DockerAvailabilityProbe(new SystemProcessRunner(), TimeProvider.System);
        var (available, _) = await probe.ProbeAsync(CancellationToken.None);
        if (!available)
            return; // Docker isn't available in this environment; nothing to verify.

        var root = Directory.CreateTempSubdirectory("caliper-docker-it-").FullName;
        try
        {
            var runtimeSettings = new RuntimeSettings(
                Options.Create(new CaliperOptions
                {
                    Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Container, Image = TestImage },
                }),
                Options.Create(new PermissionsOptions()));
            var backend = new ContainerExecutionBackend(runtimeSettings, TimeProvider.System);

            var result = await backend.ExecuteAsync(
                new ShellExecutionRequest("bash", "echo caliper-roundtrip", root, root, TimeSpan.FromSeconds(120), ["CALIPER_"], 8192),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("caliper-roundtrip", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
