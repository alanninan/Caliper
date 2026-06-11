// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Protocol;
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.AI;

namespace Caliper.Evals.Suites;

internal static class EditFileSuite
{
    internal static IReadOnlyList<EvalCase> Cases()
    {
        var unique = TempFile("unique", "alpha beta gamma");
        var missing = TempFile("missing", "alpha beta gamma");
        var duplicate = TempFile("duplicate", "dup middle dup");

        return
        [
            new EvalCase(
                Id: "edit-file-unique-replace",
                UserMessage: "Replace beta with BETA.",
                ScriptedTurns:
                [
                    EditTurn("old_str appears once.", unique, "beta", "BETA"),
                    """{"rationale":"The edit succeeded.","action":"respond","content":"Edited."}""",
                ],
                MockToolResponses: null,
                Assert: events =>
                {
                    var succeeded = events.OfType<ToolSucceeded>().Any(t => t.Tool == "edit_file");
                    var content = File.ReadAllText(unique);
                    var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                    return succeeded && content == "alpha BETA gamma" && completed
                        ? EvalOutcome.Ok()
                        : EvalOutcome.Fail($"succeeded={succeeded} content={content} completed={completed}");
                },
                ExpectedTools: ["edit_file"],
                ValidateArgs: HasEditArgs,
                ToolRegistryFactory: () => new EditFileEvalRegistry(),
                PermissionMode: PermissionMode.Auto,
                ScriptedPromptDecisions: [PermissionDecision.Allow],
                PermissionCorrect: (events, prompts) =>
                    prompts == 1
                    && events.OfType<ToolSucceeded>().Any(t => t.Tool == "edit_file")),

            new EvalCase(
                Id: "edit-file-zero-match",
                UserMessage: "Replace a missing string.",
                ScriptedTurns:
                [
                    EditTurn("The requested text is missing.", missing, "absent", "ABSENT"),
                    """{"rationale":"The edit failed closed.","action":"respond","content":"No edit made."}""",
                ],
                MockToolResponses: null,
                Assert: events =>
                {
                    var failed = events.OfType<ToolFailed>().Any(t =>
                        t.Tool == "edit_file"
                        && t.Error.Contains("found 0", StringComparison.OrdinalIgnoreCase));
                    var unchanged = File.ReadAllText(missing) == "alpha beta gamma";
                    var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                    return failed && unchanged && completed
                        ? EvalOutcome.Ok()
                        : EvalOutcome.Fail($"failed={failed} unchanged={unchanged} completed={completed}");
                },
                ExpectedTools: ["edit_file"],
                ValidateArgs: HasEditArgs,
                ToolRegistryFactory: () => new EditFileEvalRegistry(),
                PermissionMode: PermissionMode.Auto,
                ScriptedPromptDecisions: [PermissionDecision.Allow],
                PermissionCorrect: (events, prompts) =>
                    prompts == 1
                    && events.OfType<ToolFailed>().Any(t => t.Tool == "edit_file")),

            new EvalCase(
                Id: "edit-file-multiple-match",
                UserMessage: "Replace a duplicate string.",
                ScriptedTurns:
                [
                    EditTurn("old_str appears more than once.", duplicate, "dup", "DUP"),
                    """{"rationale":"The edit failed closed.","action":"respond","content":"No edit made."}""",
                ],
                MockToolResponses: null,
                Assert: events =>
                {
                    var failed = events.OfType<ToolFailed>().Any(t =>
                        t.Tool == "edit_file"
                        && t.Error.Contains("found 2", StringComparison.OrdinalIgnoreCase));
                    var unchanged = File.ReadAllText(duplicate) == "dup middle dup";
                    var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                    return failed && unchanged && completed
                        ? EvalOutcome.Ok()
                        : EvalOutcome.Fail($"failed={failed} unchanged={unchanged} completed={completed}");
                },
                ExpectedTools: ["edit_file"],
                ValidateArgs: HasEditArgs,
                ToolRegistryFactory: () => new EditFileEvalRegistry(),
                PermissionMode: PermissionMode.Auto,
                ScriptedPromptDecisions: [PermissionDecision.Allow],
                PermissionCorrect: (events, prompts) =>
                    prompts == 1
                    && events.OfType<ToolFailed>().Any(t => t.Tool == "edit_file")),
        ];
    }

    private static string TempFile(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "caliper-eval-edit-file");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static string EditTurn(string rationale, string path, string oldStr, string newStr) =>
        JsonSerializer.Serialize(new
        {
            rationale,
            action = "call_tool",
            tool = "edit_file",
            arguments = new
            {
                path,
                old_str = oldStr,
                new_str = newStr,
            },
        });

    private static bool HasEditArgs(JsonElement args) =>
        args.TryGetProperty("path", out var path)
        && path.ValueKind == JsonValueKind.String
        && args.TryGetProperty("old_str", out var oldStr)
        && oldStr.ValueKind == JsonValueKind.String
        && args.TryGetProperty("new_str", out var newStr)
        && newStr.ValueKind == JsonValueKind.String;

    private sealed class EditFileEvalRegistry : IToolRegistry
    {
        private readonly EditFileTool _tool = new();

        public IReadOnlyList<ITool> Enabled => [_tool];
        public ITool? Find(string name) => string.Equals(name, _tool.Name, StringComparison.Ordinal) ? _tool : null;
        public IReadOnlyList<AIFunction> AsAIFunctions() => [];
        public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) =>
            ProtocolBuilder.BuildSchema([(_tool.Name, _tool.ParameterSchema)], skillMenu);
        public string BuildToolMenu() => $"- {_tool.Name}: {_tool.Description}";
    }
}
