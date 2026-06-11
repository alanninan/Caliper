// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Evals.Suites;

internal static class ContextSuite
{
    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        new EvalCase(
            Id:          "long-tool-history-completes",
            UserMessage: "Search several times, then summarize.",
            ScriptedTurns:
            [
                """{"rationale":"I should search for the first part.","action":"call_tool","tool":"search","arguments":{"query":"context budget first"}}""",
                """{"rationale":"I should search for the second part.","action":"call_tool","tool":"search","arguments":{"query":"context budget second"}}""",
                """{"rationale":"I should search for the third part.","action":"call_tool","tool":"search","arguments":{"query":"context budget third"}}""",
                """{"rationale":"I now have enough information to answer.","action":"respond","content":"The context manager keeps the latest useful history while older tool output can be omitted."}""",
            ],
            MockToolResponses: new Dictionary<string, string>
            {
                ["search"] = new string('x', 4_000),
            },
            Assert: events =>
            {
                var toolCalls = events.OfType<ToolSucceeded>().Count();
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                var failed = events.OfType<RunFailed>().Any();
                return toolCalls == 3 && completed && !failed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"toolCalls={toolCalls} completed={completed} failed={failed}");
            },
            ExpectedTools: ["search", "search", "search"]),
    ];
}
