// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Execution;

/// <summary>
/// The fakeable process-launch seam behind <see cref="HostExecutionBackend"/> and
/// <see cref="ContainerExecutionBackend"/> (both of which spawn a local OS process — <c>bash</c>/
/// <c>powershell.exe</c> directly for the host backend, <c>docker</c> for the container one).
/// Deliberately <see langword="internal"/>: both backends expose only a public,
/// zero-argument-beyond-DI constructor, with an internal overload accepting an
/// <see cref="IProcessRunner"/> for tests (see their internal constructors) — the seam exists for
/// hermetic unit testing, not as something a Caliper host needs to know about or replace.
/// </summary>
internal interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken ct);
}

/// <summary>
/// Everything a backend needs to launch one process. <see cref="WorkingDirectory"/> is null for
/// processes that don't care about it (the docker probe, <c>docker kill</c>).
/// <see cref="EnvironmentScrubPrefixes"/> and <see cref="OutputBufferCapChars"/> mirror
/// <see cref="ShellExecutionRequest"/>'s fields of the same purpose.
/// </summary>
internal sealed record ProcessRunSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyList<string> EnvironmentScrubPrefixes,
    int OutputBufferCapChars);

/// <summary>Exit code plus the combined (buffer-capped) stdout/stderr text, "stderr: "-prefixed per stderr line.</summary>
internal sealed record ProcessRunResult(int ExitCode, string Output);
