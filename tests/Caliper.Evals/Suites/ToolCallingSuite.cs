// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;
using System.Text.Json;

namespace Caliper.Evals.Suites;

internal static class ToolCallingSuite
{
    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        // ── Case 1: Respond only ─────────────────────────────────────────
        new EvalCase(
            Id:               "respond-only",
            UserMessage:      "What is 2 + 2?",
            ScriptedTurns:    [
                """{"rationale":"This is basic arithmetic.","action":"respond","content":"4"}"""
            ],
            MockToolResponses: null,
            Assert: events =>
                events.OfType<AssistantMessage>().Any()
                    && events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed)
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail("Expected AssistantMessage + RunCompleted(Completed)")
        ),

        // ── Case 2: Search then respond ───────────────────────────────────
        new EvalCase(
            Id:            "search-and-respond",
            UserMessage:   "Search for the capital of France and report the result.",
            ScriptedTurns: [
                """{"rationale":"I should use the search tool to look this up.","action":"call_tool","tool":"search","arguments":{"query":"capital of France"}}""",
                """{"rationale":"The search result confirms Paris is the capital.","action":"respond","content":"The capital of France is Paris."}"""
            ],
            MockToolResponses: new Dictionary<string, string>
            {
                ["search"] = "1. Capital of France — Paris is the capital and largest city of France."
            },
            Assert: events =>
            {
                var toolInvoked    = events.OfType<ToolInvoked>().Any(t => t.Tool == "search");
                var toolSucceeded  = events.OfType<ToolSucceeded>().Any();
                var responded      = events.OfType<AssistantMessage>().Any();
                var completed      = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return toolInvoked && toolSucceeded && responded && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail(
                        $"toolInvoked={toolInvoked} toolSucceeded={toolSucceeded} responded={responded} completed={completed}");
            },
            ExpectedTools: ["search"],
            ValidateArgs: args =>
                args.TryGetProperty("query", out var query)
                && query.ValueKind == JsonValueKind.String
                && query.GetString() == "capital of France"
        ),

        // ── Case 3: Load skill then respond ───────────────────────────────
        new EvalCase(
            Id:            "load-skill",
            UserMessage:   "Load the pdf-processing skill.",
            ScriptedTurns: [
                """{"rationale":"I need to load the pdf-processing skill first.","action":"load_skill","skill":"pdf-processing"}""",
                """{"rationale":"The skill is now loaded. I can assist.","action":"respond","content":"The pdf-processing skill has been loaded."}"""
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var skillLoaded = events.OfType<SkillLoaded>().Any(s => s.Skill == "pdf-processing");
                var responded   = events.OfType<AssistantMessage>().Any();
                var completed   = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return skillLoaded && responded && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail(
                        $"skillLoaded={skillLoaded} responded={responded} completed={completed}");
            }
        ),

        new EvalCase(
            Id:            "wrong-tool-avoidance",
            UserMessage:   "What is 3 + 5? Answer directly.",
            ScriptedTurns: [
                """{"rationale":"No external lookup is needed.","action":"respond","content":"8"}"""
            ],
            MockToolResponses: new Dictionary<string, string>
            {
                ["search"] = "This should not be called."
            },
            Assert: events =>
            {
                var noTool = !events.OfType<ToolInvoked>().Any();
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return noTool && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"noTool={noTool} completed={completed}");
            },
            ExpectedTools: []
        ),

        new EvalCase(
            Id:            "duplicate-tool-loop-detected",
            UserMessage:   "Keep searching for the same thing.",
            ScriptedTurns: Enumerable.Repeat(
                """{"rationale":"I am repeating the same search.","action":"call_tool","tool":"search","arguments":{"query":"same query"}}""",
                8).ToList(),
            MockToolResponses: new Dictionary<string, string>
            {
                ["search"] = "same result"
            },
            Assert: events =>
            {
                var looped = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.LoopDetected);
                var toolCalls = events.OfType<ToolInvoked>().Count();
                return looped && toolCalls == 5
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"looped={looped} toolCalls={toolCalls}");
            }
        ),

        new EvalCase(
            Id:            "invalid-tool-args-fail-closed",
            UserMessage:   "Fetch https://example.com and summarize it.",
            ScriptedTurns: [
                """{"rationale":"I should fetch the URL.","action":"call_tool","tool":"fetch_url","arguments":{"url":["https://example.com"]}}""",
                """{"rationale":"The tool call failed because the URL argument was malformed.","action":"respond","content":"I could not fetch the page because the tool arguments were invalid."}"""
            ],
            MockToolResponses: new Dictionary<string, string>
            {
                ["fetch_url"] = "This should not be returned because arguments are invalid."
            },
            Assert: events =>
            {
                var failedClosed = events.OfType<ToolFailed>().Any(t =>
                    t.Tool == "fetch_url"
                    && t.Error.Contains("$.url must be string, got array", StringComparison.Ordinal));
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return failedClosed && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"failedClosed={failedClosed} completed={completed}");
            },
            ExpectedTools: ["fetch_url"]
        ),
    ];
}
