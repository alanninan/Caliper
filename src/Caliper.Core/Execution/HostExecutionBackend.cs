// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Execution;

/// <summary>
/// Runs shell commands directly on the host — the default backend and the only one that existed
/// before roadmap §3.3. This is a faithful extraction of what used to be inline in
/// <c>ShellTool.CreateStartInfo</c>/<c>InvokeAsync</c>: same executable selection
/// (<c>powershell.exe</c>/<c>pwsh</c> for the <c>powershell</c> shell kind, <c>bash</c> otherwise),
/// same <c>ArgumentList</c>-based argument passing (never string-concatenated, so trailing
/// backslashes/embedded quotes in the command are handled correctly), same environment scrub. No
/// observable behavior change for Backend=Host — see <see cref="Tools.BuiltIn.ShellTool"/>'s own
/// doc comment for what stayed there (permission semantics, display truncation, result shaping).
/// </summary>
public sealed class HostExecutionBackend : IExecutionBackend
{
    private readonly IProcessRunner _processRunner;

    public HostExecutionBackend() : this(new SystemProcessRunner())
    {
    }

    /// <summary>Test seam — see <see cref="IProcessRunner"/>'s doc comment for why this is internal, not public.</summary>
    internal HostExecutionBackend(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<ShellExecutionResult> ExecuteAsync(ShellExecutionRequest request, CancellationToken ct)
    {
        var isPowerShell = string.Equals(request.ShellKind, "powershell", StringComparison.OrdinalIgnoreCase);
        var fileName = isPowerShell
            ? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
            : "bash";

        List<string> arguments = isPowerShell
            ? ["-NoProfile", "-Command", request.Command]
            : ["-c", request.Command];

        var spec = new ProcessRunSpec(fileName, arguments, request.Cwd, request.EnvScrubPrefixes, request.OutputBufferCapChars);
        var result = await _processRunner.RunAsync(spec, ct).ConfigureAwait(false);
        return new ShellExecutionResult(result.ExitCode, result.Output);
    }
}
