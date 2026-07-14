// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.ComponentModel;
using System.Globalization;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.Core.Execution;

/// <summary>
/// Runs shell commands inside a disposable Linux container via the <c>docker</c> CLI (roadmap
/// §3.3) — never <c>Docker.DotNet</c> (forbidden; AOT/trim risk), always <c>Process</c> +
/// <c>ArgumentList</c> so the command is never string-concatenated into a shell-parsed argument.
/// Bash only in v1: a <c>powershell</c>-kind request is rejected outright (Windows containers are
/// out of scope). Fails closed: if <c>docker info</c> doesn't succeed, every call fails with a
/// clear "container backend unavailable" message — this backend never falls back to the host.
/// Reads <see cref="ExecutionOptions"/> fresh from <see cref="IRuntimeSettings"/> on every call
/// (Image/Network/Cpus/MemoryMb/User are a live seam, same as <c>Backend</c> itself), so a config
/// change applies to the very next invocation.
/// </summary>
public sealed class ContainerExecutionBackend : IExecutionBackend
{
    private const string ContainerWorkspace = "/workspace";

    private readonly IRuntimeSettings _runtimeSettings;
    private readonly IProcessRunner _processRunner;
    private readonly DockerAvailabilityProbe _probe;

    public ContainerExecutionBackend(IRuntimeSettings runtimeSettings, TimeProvider timeProvider)
        : this(runtimeSettings, new SystemProcessRunner(), timeProvider)
    {
    }

    /// <summary>Test seam — see <see cref="IProcessRunner"/>'s doc comment for why this is internal, not public.</summary>
    internal ContainerExecutionBackend(IRuntimeSettings runtimeSettings, IProcessRunner processRunner, TimeProvider timeProvider)
    {
        _runtimeSettings = runtimeSettings;
        _processRunner = processRunner;
        _probe = new DockerAvailabilityProbe(processRunner, timeProvider);
    }

    public async Task<ShellExecutionResult> ExecuteAsync(ShellExecutionRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.ShellKind, "bash", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The '{request.ShellKind}' shell is unsupported by the container execution backend in v1 " +
                "(bash only). Switch Execution.Backend to Host to run PowerShell, or run this command through bash.");
        }

        var workingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkingRoot));
        var cwd = Path.GetFullPath(request.Cwd);
        var containerWorkdir = MapCwdToContainer(workingRoot, cwd);

        var (available, probeError) = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        if (!available)
            throw new InvalidOperationException($"container backend unavailable: {probeError}");

        var execution = _runtimeSettings.Caliper.Execution;
        var containerName = $"caliper-{Guid.NewGuid():N}";
        var arguments = BuildRunArguments(execution, containerName, workingRoot, containerWorkdir, request.Command);
        var spec = new ProcessRunSpec("docker", arguments, WorkingDirectory: null, request.EnvScrubPrefixes, request.OutputBufferCapChars);

        try
        {
            var result = await _processRunner.RunAsync(spec, ct).ConfigureAwait(false);
            return new ShellExecutionResult(result.ExitCode, result.Output);
        }
        catch (OperationCanceledException)
        {
            // The local `docker run` CLI client is already being killed by the runner's own
            // cancellation handling (SystemProcessRunner's kill-tree), but killing that client does
            // not stop the detached container it started — fire an explicit `docker kill` so the
            // workload actually stops, mirroring today's host kill-tree-on-cancel semantics.
            await KillContainerBestEffortAsync(containerName).ConfigureAwait(false);
            throw;
        }
        catch (Win32Exception ex)
        {
            // docker itself could not be launched (e.g. not on PATH) even though the probe just
            // succeeded — a race with Docker Desktop shutting down, or a probe/run PATH mismatch.
            throw new InvalidOperationException($"container backend unavailable: failed to launch docker ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Best-effort: the container may already be gone (`--rm` removes it once it exits on its own)
    /// or the docker daemon may be unreachable. Uses a fresh, short-lived token — the caller's own
    /// token is already cancelled, so a kill request built on it would never actually run.
    /// </summary>
    private async Task KillContainerBestEffortAsync(string containerName)
    {
        try
        {
            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var killSpec = new ProcessRunSpec("docker", ["kill", containerName], WorkingDirectory: null, EnvironmentScrubPrefixes: [], OutputBufferCapChars: 4096);
            await _processRunner.RunAsync(killSpec, killCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            // Nothing more we can do from here; --rm cleans up eventually if the container is still
            // alive at all, and Caliper is not the only guard against a runaway container
            // (Network:none + resource limits bound the damage regardless).
        }
    }

    /// <summary>
    /// Maps an already-resolved absolute host <paramref name="cwd"/> to a path inside the
    /// container's <c>/workspace</c> mount (<paramref name="workingRoot"/> bind-mounted there).
    /// Internal, not private, so it's directly unit-testable via
    /// <c>InternalsVisibleTo("Caliper.Core.Tests")</c>, matching <c>SubagentTool</c>'s
    /// <c>BuildChildOverlay</c> precedent. In practice <c>ShellTool</c> never passes a
    /// <paramref name="cwd"/> outside <paramref name="workingRoot"/> — <c>FileToolHelpers.ResolvePath</c>
    /// already rejects that for the shell tools (they are not in <c>FileAccessPolicy.IsFileTool</c>'s
    /// allow-outside-root list) — but this backend re-checks anyway: <see cref="IExecutionBackend"/>
    /// is a general seam other callers could use directly, and the check documents+enforces the
    /// invariant the container mount relies on rather than assuming an upstream guard forever holds.
    /// </summary>
    internal static string MapCwdToContainer(string workingRoot, string cwd)
    {
        var relative = Path.GetRelativePath(workingRoot, cwd);
        if (relative == ".")
            return ContainerWorkspace;

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"Cannot run in the container backend: working directory '{cwd}' is outside the mounted working root '{workingRoot}'.");
        }

        var unixRelative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return $"{ContainerWorkspace}/{unixRelative}";
    }

    private static List<string> BuildRunArguments(
        ExecutionOptions execution, string containerName, string workingRoot, string containerWorkdir, string command)
    {
        var network = execution.Network == ExecutionNetworkKind.Bridge ? "bridge" : "none";
        return
        [
            "run", "--rm",
            "--name", containerName,
            "--network", network,
            "--memory", $"{execution.MemoryMb}m",
            "--cpus", execution.Cpus.ToString(CultureInfo.InvariantCulture),
            "--user", execution.User,
            "-v", $"{workingRoot}:{ContainerWorkspace}",
            "-w", containerWorkdir,
            execution.Image,
            "bash", "-lc", command,
        ];
    }
}
