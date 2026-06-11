// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Events;
using Spectre.Console;

namespace Caliper.Evals;

internal static class EvalReporter
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    internal static void Print(SuiteResult result)
    {
        var mode = result.ModelName is null ? "[dim](hermetic)[/]" : $"[dim](model: {Markup.Escape(result.ModelName)})[/]";
        AnsiConsole.MarkupLine($"\n[bold cyan]Caliper Eval — {Markup.Escape(result.SuiteName)}[/] {mode}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Case")
            .AddColumn("Pass")
            .AddColumn("Perm")
            .AddColumn("Compact")
            .AddColumn("Steps")
            .AddColumn("Completion")
            .AddColumn("Note");

        foreach (var r in result.Results)
        {
            var pass       = r.Outcome.Pass ? "[green]✓[/]" : "[red]✗[/]";
            var steps      = r.Events.Count(e => e is TurnStarted).ToString();
            var completion = r.Events.OfType<RunCompleted>().FirstOrDefault() is RunCompleted rc
                          ? rc.Reason.ToString()
                          : r.Events.OfType<RunFailed>().Any()
                              ? "[red]Error[/]"
                              : "—";
            var note       = r.Outcome.Reason is { } reason
                ? Markup.Escape(reason.Length > 60 ? reason[..60] + "…" : reason)
                : string.Empty;

            table.AddRow(
                Markup.Escape(r.CaseId),
                pass,
                Metric(r.PermissionCorrect),
                Metric(r.CompactionSafe),
                steps,
                completion,
                note);
        }

        AnsiConsole.Write(table);

        // Metrics summary
        var total    = result.Results.Count;
        var passed   = result.Results.Count(r => r.Outcome.Pass);
        var looped   = result.Results.Count(r => r.Events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.LoopDetected));
        var stepLimit = result.Results.Count(r => r.Events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.StepLimit));
        var failed   = result.Results.Count(r => r.Events.OfType<RunFailed>().Any());
        var toolCases = result.Results.Count(r => r.Events.OfType<ToolInvoked>().Any());
        var successfulToolCases = result.Results.Count(r =>
            r.Events.OfType<ToolInvoked>().Any()
            && !r.Events.OfType<ToolFailed>().Any());
        var timeoutCases = result.Results.Count(r => r.Events.OfType<ToolFailed>().Any(t =>
            t.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
        var correctToolCases = result.Results.Count(r => r.CorrectTool is not null);
        var correctTool      = result.Results.Count(r => r.CorrectTool == true);
        var validArgCases    = result.Results.Count(r => r.ValidArgs is not null);
        var validArgs        = result.Results.Count(r => r.ValidArgs == true);
        var permissionCases  = result.Results.Count(r => r.PermissionCorrect is not null);
        var permissionCorrect = result.Results.Count(r => r.PermissionCorrect == true);
        var compactionCases  = result.Results.Count(r => r.CompactionSafe is not null);
        var compactionSafe   = result.Results.Count(r => r.CompactionSafe == true);
        var meanSteps = total == 0 ? 0.0
            : result.Results.Average(r => (double)r.Events.Count(e => e is TurnStarted));

        AnsiConsole.MarkupLine($"[bold]Task completion:[/] {passed}/{total}  " +
                               $"[bold]Correct-tool:[/] {correctTool}/{correctToolCases}  " +
                               $"[bold]Valid-args:[/] {validArgs}/{validArgCases}  " +
                               $"[bold]Permission:[/] {permissionCorrect}/{permissionCases}  " +
                               $"[bold]Compaction:[/] {compactionSafe}/{compactionCases}  " +
                               $"[bold]Tool success:[/] {successfulToolCases}/{toolCases}  " +
                               $"[bold]Mean steps:[/] {meanSteps:F1}  " +
                               $"[bold]Loop incidence:[/] {looped}/{total}  " +
                               $"[bold]Step-limit:[/] {stepLimit}/{total}  " +
                               $"[bold]Timeouts:[/] {timeoutCases}/{total}  " +
                               $"[bold]Schema/parse failures:[/] {failed}/{total}");
    }

    internal static void WriteJson(SuiteResult result, string path)
    {
        var total    = result.Results.Count;
        var passed   = result.Results.Count(r => r.Outcome.Pass);
        var looped   = result.Results.Count(r => r.Events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.LoopDetected));
        var stepLimit = result.Results.Count(r => r.Events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.StepLimit));
        var schemaFail = result.Results.Count(r => r.Events.OfType<RunFailed>().Any());
        var toolCases = result.Results.Count(r => r.Events.OfType<ToolInvoked>().Any());
        var successfulToolCases = result.Results.Count(r =>
            r.Events.OfType<ToolInvoked>().Any()
            && !r.Events.OfType<ToolFailed>().Any());
        var timeoutCases = result.Results.Count(r => r.Events.OfType<ToolFailed>().Any(t =>
            t.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase)));
        var correctToolCases = result.Results.Count(r => r.CorrectTool is not null);
        var correctTool      = result.Results.Count(r => r.CorrectTool == true);
        var validArgCases    = result.Results.Count(r => r.ValidArgs is not null);
        var validArgs        = result.Results.Count(r => r.ValidArgs == true);
        var permissionCases  = result.Results.Count(r => r.PermissionCorrect is not null);
        var permissionCorrect = result.Results.Count(r => r.PermissionCorrect == true);
        var compactionCases  = result.Results.Count(r => r.CompactionSafe is not null);
        var compactionSafe   = result.Results.Count(r => r.CompactionSafe == true);
        var meanSteps  = total == 0 ? 0.0
            : result.Results.Average(r => (double)r.Events.Count(e => e is TurnStarted));

        var report = new
        {
            suiteName = result.SuiteName,
            modelName = result.ModelName,
            runAt     = result.RunAt,
            metrics   = new
            {
                totalCases          = total,
                passCount           = passed,
                taskCompletionRate  = total > 0 ? (double)passed / total : 0.0,
                schemaValidRate     = total > 0 ? (double)(total - schemaFail) / total : 0.0,
                correctToolRate     = correctToolCases > 0 ? (double)correctTool / correctToolCases : (double?)null,
                validArgumentRate   = validArgCases > 0 ? (double)validArgs / validArgCases : (double?)null,
                permissionCorrectRate = permissionCases > 0 ? (double)permissionCorrect / permissionCases : (double?)null,
                compactionSafetyRate = compactionCases > 0 ? (double)compactionSafe / compactionCases : (double?)null,
                meanSteps           = meanSteps,
                loopIncidence       = total > 0 ? (double)looped / total : 0.0,
                stepLimitIncidence  = total > 0 ? (double)stepLimit / total : 0.0,
                toolSuccessRate     = toolCases > 0 ? (double)successfulToolCases / toolCases : 1.0,
                timeoutIncidence    = total > 0 ? (double)timeoutCases / total : 0.0,
            },
            cases = result.Results.Select(r => new
            {
                id         = r.CaseId,
                pass       = r.Outcome.Pass,
                reason     = r.Outcome.Reason,
                steps      = r.Events.Count(e => e is TurnStarted),
                completion = r.Events.OfType<RunCompleted>().FirstOrDefault()?.Reason.ToString()
                          ?? (r.Events.OfType<RunFailed>().Any() ? "Error" : "Unknown"),
                elapsedMs  = (long)r.Elapsed.TotalMilliseconds,
                permissionCorrect = r.PermissionCorrect,
                compactionSafe = r.CompactionSafe,
                promptCount = r.PromptCount,
            }).ToList(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, s_json));
    }

    private static string Metric(bool? value) =>
        value switch
        {
            true => "[green]✓[/]",
            false => "[red]✗[/]",
            null => "[dim]-[/]",
        };
}
