// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Evals.Suites;

internal static class CompactionSuite
{
    internal static IReadOnlyList<EvalCase> Cases() =>
    [
        new EvalCase(
            Id: "auto-compaction-preserves-tail",
            UserMessage: "Answer after compacting old context.",
            ScriptedTurns:
            [
                """{"rationale":"The current request survived compaction.","action":"respond","content":"Answer survived compaction."}""",
            ],
            MockToolResponses: null,
            RuntimeOptions: OptionsFor(autoCompact: true),
            Assert: events =>
            {
                var compacted = events.OfType<ContextCompacted>().Any();
                var answered = events.OfType<AssistantMessage>().Any(m => m.Content == "Answer survived compaction.");
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return compacted && answered && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"compacted={compacted} answered={answered} completed={completed}");
            },
            Capabilities: new ModelCapabilities(true, true, true, 600),
            ContextFactory: tokens => new AutoCompactingContextManager(
                tokens,
                new EvalSummarizer("Earlier eval history summary."),
                new RuntimeSettings(
                    Options.Create(new CaliperOptions
                    {
                        Context = new ContextOptions
                        {
                            AutoCompact = true,
                            CompactAtFraction = 0.45,
                            KeepRecentTurns = 1,
                            ReservedOutputTokens = 64,
                        },
                    }),
                    Options.Create(new PermissionsOptions())),
                NullLogger<AutoCompactingContextManager>.Instance),
            SeedSessionAsync: async (store, sessionId, ct) =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await store.AppendAsync(sessionId, new ChatMessage(ChatRole.User, MessageKind.Text, $"old user {i} {new string('u', 480)}"), ct).ConfigureAwait(false);
                    await store.AppendAsync(sessionId, new ChatMessage(ChatRole.Assistant, MessageKind.Text, $"old assistant {i} {new string('a', 480)}"), ct).ConfigureAwait(false);
                }
            },
            CompactionSafe: events =>
                events.OfType<ContextCompacted>().Any(c => c.AfterTokens <= c.BeforeTokens)
                && events.OfType<AssistantMessage>().Any(m => m.Content == "Answer survived compaction.")),

        new EvalCase(
            Id: "compaction-disabled-trims-without-summary",
            UserMessage: "Answer with compaction disabled.",
            ScriptedTurns:
            [
                """{"rationale":"No summary should be inserted when compaction is disabled.","action":"respond","content":"Answered without compaction."}""",
            ],
            MockToolResponses: null,
            RuntimeOptions: OptionsFor(autoCompact: false),
            Assert: events =>
            {
                var compacted = events.OfType<ContextCompacted>().Any();
                var answered = events.OfType<AssistantMessage>().Any(m => m.Content == "Answered without compaction.");
                var completed = events.OfType<RunCompleted>().Any(c => c.Reason == CompletionReason.Completed);
                return !compacted && answered && completed
                    ? EvalOutcome.Ok()
                    : EvalOutcome.Fail($"compacted={compacted} answered={answered} completed={completed}");
            },
            Capabilities: new ModelCapabilities(true, true, true, 600),
            ContextFactory: tokens => new AutoCompactingContextManager(
                tokens,
                new EvalSummarizer("This should not be used."),
                new RuntimeSettings(
                    Options.Create(new CaliperOptions
                    {
                        Context = new ContextOptions
                        {
                            AutoCompact = false,
                            CompactAtFraction = 0.45,
                            KeepRecentTurns = 1,
                            ReservedOutputTokens = 64,
                        },
                    }),
                    Options.Create(new PermissionsOptions())),
                NullLogger<AutoCompactingContextManager>.Instance),
            SeedSessionAsync: async (store, sessionId, ct) =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await store.AppendAsync(sessionId, new ChatMessage(ChatRole.User, MessageKind.Text, $"old user {i} {new string('u', 480)}"), ct).ConfigureAwait(false);
                    await store.AppendAsync(sessionId, new ChatMessage(ChatRole.Assistant, MessageKind.Text, $"old assistant {i} {new string('a', 480)}"), ct).ConfigureAwait(false);
                }
            },
            CompactionSafe: events =>
                !events.OfType<ContextCompacted>().Any()
                && events.OfType<AssistantMessage>().Any(m => m.Content == "Answered without compaction.")),
    ];

    private static CaliperOptions OptionsFor(bool autoCompact) =>
        new()
        {
            Model = "eval-fake",
            MaxSteps = 8,
            DuplicateCallLimit = 5,
            Seed = 42,
            Temperature = 0.0,
            Context = new ContextOptions
            {
                AutoCompact = autoCompact,
                CompactAtFraction = 0.45,
                KeepRecentTurns = 1,
                ReservedOutputTokens = 64,
            },
        };

    private sealed class EvalSummarizer(string summary) : ISummarizer
    {
        public Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
            Task.FromResult(summary);
    }
}
