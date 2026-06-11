// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Tools.Mcp;
using ModelContextProtocol.Protocol;

namespace Caliper.Evals.Suites;

internal static class McpSuite
{
    internal static IReadOnlyList<EvalCase> Cases()
    {
        var failureIsolationRegistry = MockToolRegistry.FromSpecs(
        [
            new MockToolSpec(
                "healthy__status",
                "Read status from the healthy MCP server.",
                MockToolRegistry.SchemaFor("healthy__status"),
                "ok",
                McpClassifier.Classify(new ToolAnnotations { ReadOnlyHint = true }),
                IsMcp: true),
        ]);

        return
        [
            new EvalCase(
            Id: "mcp-readonly-dispatches",
            UserMessage: "Read MCP data.",
            ScriptedTurns:
            [
                """{"rationale":"The readonly MCP tool can answer this.","action":"call_tool","tool":"docs__read","arguments":{"id":"intro"}}""",
                """{"rationale":"The MCP read returned content.","action":"respond","content":"Read complete."}""",
            ],
            MockToolResponses: null,
            Assert: CompletedWithTool("docs__read"),
            ExpectedTools: ["docs__read"],
            ToolSpecs:
            [
                new MockToolSpec(
                    "docs__read",
                    "Read documentation from an MCP server.",
                    MockToolRegistry.SchemaFor("docs__read"),
                    "documentation",
                    McpClassifier.Classify(new ToolAnnotations { ReadOnlyHint = true }),
                    IsMcp: true),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [],
            PermissionCorrect: (events, prompts) =>
                prompts == 0
                && events.OfType<ToolSucceeded>().Any(t => t.Tool == "docs__read")),

        new EvalCase(
            Id: "mcp-destructive-through-gate",
            UserMessage: "Delete through MCP.",
            ScriptedTurns:
            [
                """{"rationale":"The destructive MCP tool needs permission.","action":"call_tool","tool":"fs__delete","arguments":{"path":"tmp.txt"}}""",
                """{"rationale":"The deletion completed after approval.","action":"respond","content":"Deleted."}""",
            ],
            MockToolResponses: null,
            Assert: CompletedWithTool("fs__delete"),
            ExpectedTools: ["fs__delete"],
            ToolSpecs:
            [
                new MockToolSpec(
                    "fs__delete",
                    "Delete a file through MCP.",
                    MockToolRegistry.SchemaFor("fs__delete"),
                    "deleted",
                    McpClassifier.Classify(new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }),
                    IsMcp: true),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [PermissionDecision.Allow],
            PermissionCorrect: (events, prompts) =>
                prompts == 1
                && events.OfType<ToolSucceeded>().Any(t => t.Tool == "fs__delete")),

        new EvalCase(
            Id: "mcp-failure-isolation",
            UserMessage: "Use the MCP tool that remains available.",
            ScriptedTurns:
            [
                """{"rationale":"Only the healthy MCP tool should be invoked.","action":"call_tool","tool":"healthy__status","arguments":{"verbose":true}}""",
                """{"rationale":"The healthy server returned status.","action":"respond","content":"Healthy."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var succeeded = events.OfType<ToolSucceeded>().Any(t => t.Tool == "healthy__status");
                var failedToolAbsent = failureIsolationRegistry.Find("failed__status") is null
                    && !failureIsolationRegistry.Enabled.Any(t => t.Name.StartsWith("failed__", StringComparison.Ordinal));
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return succeeded && failedToolAbsent && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"succeeded={succeeded} failedToolAbsent={failedToolAbsent} completed={completed}");
            },
            ExpectedTools: ["healthy__status"],
            ToolRegistryFactory: () => failureIsolationRegistry,
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: []),
        ];
    }

    private static Func<IReadOnlyList<AgentEvent>, EvalOutcome> CompletedWithTool(string tool) =>
        events =>
        {
            var succeeded = events.OfType<ToolSucceeded>().Any(t => t.Tool == tool);
            var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
            return succeeded && completed
                ? EvalOutcome.Ok()
                : EvalOutcome.Fail($"succeeded={succeeded} completed={completed}");
        };
}
