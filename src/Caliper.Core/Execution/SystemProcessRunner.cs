// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Caliper.Core.Execution;

/// <summary>
/// The real <see cref="IProcessRunner"/> — a faithful extraction of the process-launch logic that
/// used to live directly in <c>ShellTool</c> (roadmap §3.3). Behavior is unchanged: environment
/// scrub, stdin close so a command waiting on input fails fast instead of hanging to timeout,
/// bounded output buffering so a runaway logger can't consume unbounded memory before the caller's
/// own display truncation, drain-after-exit (<c>WaitForExitAsync</c> returns on process exit but
/// does not guarantee the async output readers have drained), and kill-the-process-tree on
/// cancellation. Used by both <see cref="HostExecutionBackend"/> (for <c>bash</c>/
/// <c>powershell.exe</c>) and <see cref="ContainerExecutionBackend"/> (for the local <c>docker</c>
/// CLI process) — the container backend additionally fires an explicit <c>docker kill</c> on
/// cancellation, because killing the local <c>docker run</c> client here does not stop the
/// container it started.
/// </summary>
internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(spec.FileName)
            {
                WorkingDirectory = spec.WorkingDirectory ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in spec.Arguments)
                startInfo.ArgumentList.Add(argument);

            // Don't leak Caliper's own secrets (API keys) into arbitrary shell/docker commands.
            foreach (var key in startInfo.Environment.Keys
                         .Where(name => spec.EnvironmentScrubPrefixes.Any(prefix =>
                             name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                         .ToList())
            {
                startInfo.Environment.Remove(key);
            }

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Stop buffering once we are safely past the reported cap so a `cat` of a huge file or a
            // runaway logger can't consume arbitrary memory before the caller's own display
            // truncation. The two async readers can fire concurrently, so the StringBuilder is
            // guarded by a lock; stderr lines are labelled so the model can tell interleaved streams
            // apart.
            var output = new StringBuilder();
            var outputLock = new object();
            void Append(string line, bool isError)
            {
                lock (outputLock)
                {
                    if (output.Length > spec.OutputBufferCapChars)
                        return;
                    if (isError)
                        output.Append("stderr: ");
                    output.AppendLine(line);
                }
            }

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data, isError: false); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(e.Data, isError: true); };

            if (!process.Start())
                throw new InvalidOperationException("Process failed to start.");

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

            return new ProcessRunResult(process.ExitCode, body);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void KillProcessTree(Process? process)
    {
        if (process is null || process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
        }
    }
}
