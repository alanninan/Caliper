// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Evals;
using Caliper.Evals.Suites;
using Spectre.Console;

// ── Parse CLI args ────────────────────────────────────────────────────────────
var suite     = "tool-calling";
string? model = null;
var outPath   = "eval-report.json";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--suite" when i + 1 < args.Length: suite   = args[++i]; break;
        case "--model" when i + 1 < args.Length: model   = args[++i]; break;
        case "--out"   when i + 1 < args.Length: outPath = args[++i]; break;
    }
}

if (!SuiteRegistry.TryGet(suite, out var cases))
{
    AnsiConsole.MarkupLine($"[red]Unknown suite: '{Markup.Escape(suite)}'[/]");
    AnsiConsole.MarkupLine($"Available: all, {string.Join(", ", SuiteRegistry.All.Keys)}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Run ───────────────────────────────────────────────────────────────────────
SuiteResult result;
if (model is null)
{
    AnsiConsole.MarkupLine("[dim]Mode: hermetic (no Ollama required)[/]");
    result = await EvalHarnessRunner.RunHermeticAsync(suite, cases, cts.Token);
}
else
{
    AnsiConsole.MarkupLine($"[dim]Mode: model-in-the-loop ({Markup.Escape(model)})[/]");
    result = await EvalHarnessRunner.RunWithModelAsync(suite, cases, model, cts.Token);
}

// ── Report ────────────────────────────────────────────────────────────────────
EvalReporter.Print(result);
EvalReporter.WriteJson(result, outPath);
AnsiConsole.MarkupLine($"\n[dim]Report written to:[/] {Markup.Escape(outPath)}");

return result.Results.All(r => r.Outcome.Pass) ? 0 : 1;
