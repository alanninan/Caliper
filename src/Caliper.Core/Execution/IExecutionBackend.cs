// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Execution;

/// <summary>
/// Roadmap §3.3: the pluggable seam <c>ShellTool</c> dispatches through instead of owning process
/// launch directly. <see cref="Caliper.Core.Tools.BuiltIn.ShellTool"/> keeps permission semantics,
/// output truncation, and result formatting; a backend owns process start, environment scrubbing,
/// stdin close, kill-on-cancel, and drain-after-exit for whichever execution environment it
/// targets (host process vs. container).
/// </summary>
public interface IExecutionBackend
{
    Task<ShellExecutionResult> ExecuteAsync(ShellExecutionRequest request, CancellationToken ct);
}

/// <summary>
/// One shell invocation, backend-agnostic. <see cref="Cwd"/> and <see cref="WorkingRoot"/> are both
/// already-resolved, absolute host paths (<c>ToolContext.WorkingRoot</c> and the tool call's
/// validated command directory, respectively) — <see cref="WorkingRoot"/> is what a container
/// backend bind-mounts as <c>/workspace</c>, and <see cref="Cwd"/> is mapped to a path relative to
/// it. <see cref="Timeout"/> mirrors <c>CaliperOptions.ToolTimeoutSeconds</c> for backends that want
/// it (e.g. to pass through to the target process); it is informational only here — the actual
/// enforcement is (unchanged from before this feature existed) the <see cref="CancellationToken"/>
/// the dispatch layer (<c>AgentRunner.DispatchWithRetry</c>) already wraps every tool call in.
/// <see cref="EnvScrubPrefixes"/> lists environment-variable name prefixes (case-insensitive) a
/// backend must strip from the spawned process's environment before starting it — today just
/// <c>CALIPER_</c>, so a shell command can never read Caliper's own provider API keys.
/// <see cref="OutputBufferCapChars"/> is a soft memory cap on captured combined output while the
/// process is still running (a runaway logger can't grow the buffer unboundedly); it is not the
/// same as the tool's final display truncation, which stays in <c>ShellTool</c> and is applied to
/// the backend's already-capped <see cref="ShellExecutionResult.Output"/>.
/// </summary>
public sealed record ShellExecutionRequest(
    string ShellKind,
    string Command,
    string Cwd,
    string WorkingRoot,
    TimeSpan? Timeout,
    IReadOnlyList<string> EnvScrubPrefixes,
    int OutputBufferCapChars);

/// <summary>The backend's raw result: exit code plus the full (buffer-capped, not display-truncated) captured output.</summary>
public sealed record ShellExecutionResult(int ExitCode, string Output);
