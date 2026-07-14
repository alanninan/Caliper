// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Execution;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

/// <summary>
/// Runs a shell command (bash or PowerShell — <see cref="shellName"/> picks which). Owns
/// permission-adjacent concerns only: argument parsing, working-directory resolution/validation,
/// and result shaping (exit code header + <see cref="ToolOutput.Truncate"/> display truncation).
/// Process launch itself is delegated to an <see cref="IExecutionBackend"/> (roadmap §3.3) —
/// <paramref name="hostBackend"/> for <c>Execution.Backend = Host</c> (the only backend that
/// existed before this feature; behavior is unchanged), <paramref name="containerBackend"/> for
/// <c>Execution.Backend = Container</c>. <paramref name="runtimeSettings"/> is read fresh on every
/// call so a live <c>Backend</c> flip applies to the very next invocation without a restart — see
/// <c>ServiceCollectionExtensions</c> for why constructor-time (rather than live) backend selection
/// was rejected: both backends are always-constructed singletons regardless of the configured
/// value, so there is no unsafe "rewire mid-run" step, just a per-call branch.
/// </summary>
public sealed class ShellTool(
    IOptions<CaliperOptions> options,
    string shellName,
    IExecutionBackend? hostBackend = null,
    IExecutionBackend? containerBackend = null,
    IRuntimeSettings? runtimeSettings = null) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string"},"cwd":{"type":"string"}}}""").RootElement.Clone();

    private static readonly string[] s_envScrubPrefixes = ["CALIPER_"];

    private readonly IExecutionBackend _hostBackend = hostBackend ?? new HostExecutionBackend();

    public string Name => shellName;
    public string Description => shellName == "powershell" ? "Run a PowerShell command." : "Run a Bash command.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Execute;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        var command = FileToolHelpers.GetString(arguments, "command") ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return new ToolResult(false, "Missing required argument: command");

        try
        {
            var cwd = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "cwd", ".") ?? ".", ctx);
            if (!Directory.Exists(cwd))
                return new ToolResult(false, $"Working directory not found: {cwd}");

            // Stop buffering once the backend is safely past the reported cap so a `cat` of a huge
            // file or a runaway logger can't consume arbitrary memory before the final truncate. A
            // little slack over the cap keeps the truncation marker meaningful.
            var cap = options.Value.ToolOutputMaxChars;
            var request = new ShellExecutionRequest(
                shellName,
                command,
                cwd,
                ctx.WorkingRoot,
                TimeSpan.FromSeconds(options.Value.ToolTimeoutSeconds),
                s_envScrubPrefixes,
                cap + 4096);

            var result = await SelectBackend().ExecuteAsync(request, ct).ConfigureAwait(false);
            var text = $"exit_code: {result.ExitCode}{Environment.NewLine}{result.Output}";
            return new ToolResult(result.ExitCode == 0, ToolOutput.Truncate(text, cap));
        }
        catch (OperationCanceledException)
        {
            // Kill-on-cancel now lives in the backend (HostExecutionBackend's SystemProcessRunner
            // kill-tree; ContainerExecutionBackend's `docker kill`) — ShellTool only propagates.
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Covers both "Process failed to start.", ordinary I/O failures, and every
            // container-backend failure mode (fail-closed docker-unavailable, cwd-escapes-root,
            // powershell-unsupported-in-container) — those all surface as InvalidOperationException
            // from the container backend and land here exactly like a host-side failure would.
            return new ToolResult(false, ex.Message);
        }
    }

    private IExecutionBackend SelectBackend()
    {
        if (runtimeSettings is null || containerBackend is null)
            return _hostBackend;

        return runtimeSettings.Caliper.Execution.Backend == ExecutionBackendKind.Container
            ? containerBackend
            : _hostBackend;
    }
}
