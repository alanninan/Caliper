// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Evals.Suites;

internal static class SkillsSuite
{
    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        new EvalCase(
            Id:          "load-and-respond",
            UserMessage: "Use the pdf-processing skill to help me extract text from a PDF.",
            ScriptedTurns:
            [
                """{"rationale":"The request is about PDFs, so I should load the matching skill.","action":"load_skill","skill":"pdf-processing"}""",
                """{"rationale":"The skill body is now available, so I can answer.","action":"respond","content":"I can help extract text from the PDF."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
            {
                var skillLoaded = events.OfType<SkillLoaded>().Any(s => s.Skill == "pdf-processing");
                var responded = events.OfType<AssistantMessage>().Any();
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return skillLoaded && responded && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"skillLoaded={skillLoaded} responded={responded} completed={completed}");
            }),

        new EvalCase(
            Id:          "skill-menu-present",
            UserMessage: "Give me a short answer about keeping documents organized.",
            ScriptedTurns:
            [
                """{"rationale":"I can answer directly without loading a skill.","action":"respond","content":"Keep document names consistent and store related files together."}""",
            ],
            MockToolResponses: null,
            Assert: events =>
                !events.OfType<RunFailed>().Any()
                    && events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed)
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail("Expected no RunFailed and RunCompleted(Completed).")),
    ];
}
