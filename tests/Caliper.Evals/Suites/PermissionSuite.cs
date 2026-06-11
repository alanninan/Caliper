// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Models;

namespace Caliper.Evals.Suites;

internal static class PermissionSuite
{
    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        new EvalCase(
            Id: "auto-shell-allowlist-no-prompt",
            UserMessage: "Run the safe verification command.",
            ScriptedTurns:
            [
                """{"rationale":"A verification command is allowed in auto mode.","action":"call_tool","tool":"powershell","arguments":{"command":"dotnet test tests"}}""",
                """{"rationale":"The verification completed.","action":"respond","content":"Tests completed."}""",
            ],
            MockToolResponses: null,
            Assert: CompletedAfterTool("powershell"),
            ToolSpecs:
            [
                new MockToolSpec(
                    "powershell",
                    "Run a PowerShell command.",
                    MockToolRegistry.SchemaFor("powershell"),
                    "tests passed",
                    SideEffect.Execute),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [],
            PermissionCorrect: (events, prompts) =>
                prompts == 0
                && events.OfType<ToolSucceeded>().Any(t => t.Tool == "powershell")),

        new EvalCase(
            Id: "auto-shell-prompts-unknown",
            UserMessage: "Run a command that is not in the auto allowlist.",
            ScriptedTurns:
            [
                """{"rationale":"This shell command needs confirmation.","action":"call_tool","tool":"powershell","arguments":{"command":"echo hello"}}""",
                """{"rationale":"The command completed.","action":"respond","content":"Done."}""",
            ],
            MockToolResponses: null,
            Assert: CompletedAfterTool("powershell"),
            ToolSpecs:
            [
                new MockToolSpec(
                    "powershell",
                    "Run a PowerShell command.",
                    MockToolRegistry.SchemaFor("powershell"),
                    "hello",
                    SideEffect.Execute),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [PermissionDecision.Allow],
            PermissionCorrect: (events, prompts) =>
                prompts == 1
                && events.OfType<PermissionResolved>().Any(p => p.Decision == PermissionDecision.Allow)
                && events.OfType<ToolSucceeded>().Any(t => t.Tool == "powershell")),

        new EvalCase(
            Id: "denylist-not-remembered",
            UserMessage: "Try a risky command twice.",
            ScriptedTurns:
            [
                """{"rationale":"This destructive command needs approval.","action":"call_tool","tool":"powershell","arguments":{"command":"Remove-Item -Recurse temp"}}""",
                """{"rationale":"I am retrying the same command.","action":"call_tool","tool":"powershell","arguments":{"command":"Remove-Item -Recurse temp"}}""",
                """{"rationale":"The second attempt was denied.","action":"respond","content":"I stopped after denial."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var succeeded = events.OfType<ToolSucceeded>().Count(t => t.Tool == "powershell");
                var failed = events.OfType<ToolFailed>().Count(t => t.Tool == "powershell");
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return succeeded == 1 && failed == 1 && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"succeeded={succeeded} failed={failed} completed={completed}");
            },
            ToolSpecs:
            [
                new MockToolSpec(
                    "powershell",
                    "Run a PowerShell command.",
                    MockToolRegistry.SchemaFor("powershell"),
                    "removed",
                    SideEffect.Execute),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [PermissionDecision.AllowForSession, PermissionDecision.Deny],
            PermissionCorrect: (events, prompts) =>
                prompts == 2
                && events.OfType<PermissionResolved>().Select(p => p.Decision)
                    .SequenceEqual([PermissionDecision.Allow, PermissionDecision.Deny])),

        new EvalCase(
            Id: "plan-refuses-write",
            UserMessage: "Write a file while in plan mode.",
            ScriptedTurns:
            [
                """{"rationale":"A write is requested.","action":"call_tool","tool":"write_file","arguments":{"path":"out.txt","content":"nope"}}""",
                """{"rationale":"Plan mode denied the write.","action":"respond","content":"I cannot write in plan mode."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var invoked = events.OfType<ToolInvoked>().Any(t => t.Tool == "write_file");
                var denied = events.OfType<ToolFailed>().Any(t =>
                    t.Tool == "write_file"
                    && t.Error.Contains("Denied", StringComparison.OrdinalIgnoreCase));
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return !invoked && denied && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"invoked={invoked} denied={denied} completed={completed}");
            },
            ToolSpecs:
            [
                new MockToolSpec(
                    "write_file",
                    "Write a file.",
                    MockToolRegistry.SchemaFor("write_file"),
                    "written",
                    SideEffect.Write),
            ],
            PermissionMode: PermissionMode.Plan,
            ScriptedPromptDecisions: [],
            PermissionCorrect: (events, prompts) =>
                prompts == 0
                && events.OfType<PermissionResolved>().Any(p => p.Decision == PermissionDecision.Deny)),

        new EvalCase(
            Id: "destructive-mcp-prompts-auto",
            UserMessage: "Invoke a destructive MCP tool.",
            ScriptedTurns:
            [
                """{"rationale":"This MCP tool has side effects.","action":"call_tool","tool":"local__delete","arguments":{"target":"tmp"}}""",
                """{"rationale":"The MCP tool completed.","action":"respond","content":"Deleted."}""",
            ],
            MockToolResponses: null,
            Assert: CompletedAfterTool("local__delete"),
            ToolSpecs:
            [
                new MockToolSpec(
                    "local__delete",
                    "Delete a target through MCP.",
                    MockToolRegistry.SchemaFor("local__delete"),
                    "deleted",
                    SideEffect.Execute,
                    IsMcp: true),
            ],
            PermissionMode: PermissionMode.Auto,
            ScriptedPromptDecisions: [PermissionDecision.Allow],
            PermissionCorrect: (events, prompts) =>
                prompts == 1
                && events.OfType<ToolSucceeded>().Any(t => t.Tool == "local__delete")),

        new EvalCase(
            Id: "allow-session-not-reprompted",
            UserMessage: "Write the same file twice.",
            ScriptedTurns:
            [
                """{"rationale":"The first write needs approval.","action":"call_tool","tool":"write_file","arguments":{"path":"out.txt","content":"one"}}""",
                """{"rationale":"The session approval should cover the same write.","action":"call_tool","tool":"write_file","arguments":{"path":"out.txt","content":"one"}}""",
                """{"rationale":"Both writes completed.","action":"respond","content":"Done."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var succeeded = events.OfType<ToolSucceeded>().Count(t => t.Tool == "write_file");
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return succeeded == 2 && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"succeeded={succeeded} completed={completed}");
            },
            ToolSpecs:
            [
                new MockToolSpec(
                    "write_file",
                    "Write a file.",
                    MockToolRegistry.SchemaFor("write_file"),
                    "written",
                    SideEffect.Write),
            ],
            PermissionMode: PermissionMode.AskAlways,
            ScriptedPromptDecisions: [PermissionDecision.AllowForSession],
            PermissionCorrect: (events, prompts) =>
                prompts == 1
                && events.OfType<ToolSucceeded>().Count(t => t.Tool == "write_file") == 2),
    ];

    private static Func<IReadOnlyList<AgentEvent>, EvalOutcome> CompletedAfterTool(string tool) =>
        events =>
        {
            var succeeded = events.OfType<ToolSucceeded>().Any(t => t.Tool == tool);
            var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
            return succeeded && completed
                ? EvalOutcome.Ok()
                : EvalOutcome.Fail($"succeeded={succeeded} completed={completed}");
        };
}
