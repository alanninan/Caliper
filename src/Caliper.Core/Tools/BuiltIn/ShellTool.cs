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

            // Stop buffering once we are safely past the reported cap so a `cat` of a huge file or a
            // runaway logger can't consume arbitrary memory before the final truncate. A little slack
            // over the cap keeps the truncation marker meaningful. The two async readers can fire
            // concurrently, so the StringBuilder is guarded by a lock; stderr lines are labelled so
            // the model can tell interleaved streams apart.
            var cap = options.Value.ToolOutputMaxChars;
            var bufferLimit = cap + 4096;
            var output = new StringBuilder();
            var outputLock = new object();
            void Append(string line, bool isError)
            {
                lock (outputLock)
                {
                    if (output.Length > bufferLimit)
                        return;
                    if (isError)
                        output.Append("stderr: ");
                    output.AppendLine(line);
                }
            }

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data, isError: false); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data, isError: true); };

            if (!process.Start())
                return new ToolResult(false, "Process failed to start.");

            // Close stdin so a command that waits for input fails fast instead of hanging to timeout.
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // WaitForExitAsync returns on process exit but does not guarantee the async output
            // readers have drained; the blocking overload flushes them so no tail lines are lost.
            process.WaitForExit();
            string body;
            lock (outputLock)
                body = output.ToString();
            var text = $"exit_code: {process.ExitCode}{Environment.NewLine}{body}";
            return new ToolResult(process.ExitCode == 0, ToolOutput.Truncate(text, cap));
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
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Pass arguments via ArgumentList so the shell receives the command verbatim; manual
        // quoting mishandles trailing backslashes and embedded quotes.
        if (isPowerShell)
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        // Don't leak Caliper's own secrets (API keys) into arbitrary shell commands.
        foreach (var key in startInfo.Environment.Keys
                     .Where(name => name.StartsWith("CALIPER_", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            startInfo.Environment.Remove(key);
        }

        return startInfo;
    }

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
