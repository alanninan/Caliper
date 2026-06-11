// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class ShellTool(IOptions<CaliperOptions> options, string shellName) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string"},"cwd":{"type":"string"}}}""").RootElement.Clone();

    public string Name => shellName;
    public string Description => shellName == "powershell" ? "Run a PowerShell command." : "Run a Bash command.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Execute;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        var command = FileToolHelpers.GetString(arguments, "command") ?? "";
        if (string.IsNullOrWhiteSpace(command))
            return new ToolResult(false, "Missing required argument: command");

        Process? process = null;
        try
        {
            var cwd = FileToolHelpers.ResolvePath(FileToolHelpers.GetString(arguments, "cwd", ".") ?? ".", ctx);
            if (!Directory.Exists(cwd))
                return new ToolResult(false, $"Working directory not found: {cwd}");

            var startInfo = CreateStartInfo(command, cwd);
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

            if (!process.Start())
                return new ToolResult(false, "Process failed to start.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = $"exit_code: {process.ExitCode}{Environment.NewLine}{output}";
            return new ToolResult(process.ExitCode == 0, ToolOutput.Truncate(text, options.Value.ToolOutputMaxChars));
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ToolResult(false, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private ProcessStartInfo CreateStartInfo(string command, string cwd)
    {
        var isPowerShell = shellName == "powershell";
        var fileName = isPowerShell
            ? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
            : "bash";
        var arguments = isPowerShell
            ? $"-NoProfile -Command {Quote(command)}"
            : $"-c {Quote(command)}";
        return new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void KillProcessTree(Process? process)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
