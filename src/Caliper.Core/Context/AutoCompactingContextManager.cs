// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Context;

internal sealed class AutoCompactingContextManager(
    ITokenCounter tokens,
    ISummarizer summarizer,
    IRuntimeSettings runtimeSettings,
    ILogger<AutoCompactingContextManager> logger) : IContextManager
{
    private const double SafetyMargin = 1.10;
    private readonly DropOldestContextManager _dropOldest = new(tokens);

    public async Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var contextOptions = runtimeSettings.Caliper.Context;
        var beforeRaw = EstimateRaw(frame);
        var before = ApplyMargin(beforeRaw);
        var hardLimit = HardLimit(budget);
        var compactThreshold = Math.Max(0, (int)Math.Floor(budget.ContextWindowTokens * budget.CompactAtFraction) - budget.ReservedOutputTokens);

        if (!contextOptions.AutoCompact)
        {
            return new ContextFit(
                frame.History,
                Compacted: false,
                before,
                before,
                EstimatedPromptTokens: before,
                RawEstimatedPromptTokens: beforeRaw);
        }

        if (!budget.Force && before <= compactThreshold)
            return new ContextFit(
                frame.History,
                Compacted: false,
                before,
                before,
                EstimatedPromptTokens: before,
                RawEstimatedPromptTokens: beforeRaw);

        var split = SplitForCompaction(frame.History, contextOptions.KeepRecentTurns);
        if (split.Older.Count == 0)
            return TrimOnly(frame, budget, before, compacted: false, ct);

        IReadOnlyList<ChatMessage> compacted;
        try
        {
            var summary = await summarizer.SummarizeAsync(split.Older, ct).ConfigureAwait(false);
            compacted =
            [
                new ChatMessage(ChatRole.System, MessageKind.Summary, $"Earlier conversation summary:\n{summary}"),
                .. split.Tail,
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Falling back to oldest-message trimming after summarization failure: {Message}", ex.Message);
            return TrimOnly(frame, budget, before, compacted: false, ct);
        }

        var afterSummaryRaw = EstimateRaw(frame with { History = compacted });
        var afterSummary = ApplyMargin(afterSummaryRaw);
        if (afterSummary <= hardLimit)
            return new ContextFit(compacted, Compacted: true, before, afterSummary, afterSummary, afterSummaryRaw);

        var trimmed = _dropOldest.FitMessages(compacted, HistoryBudgetWithMargin(frame, hardLimit), ct);
        var afterTrimRaw = EstimateRaw(frame with { History = trimmed });
        var afterTrim = ApplyMargin(afterTrimRaw);
        return new ContextFit(trimmed, Compacted: true, before, afterTrim, afterTrim, afterTrimRaw);
    }

    private ContextFit TrimOnly(PromptFrame frame, ContextBudget budget, int before, bool compacted, CancellationToken ct)
    {
        var hardLimit = HardLimit(budget);
        var trimmed = _dropOldest.FitMessages(frame.History, HistoryBudgetWithMargin(frame, hardLimit), ct);
        var afterRaw = EstimateRaw(frame with { History = trimmed });
        var after = ApplyMargin(afterRaw);
        return new ContextFit(trimmed, Compacted: compacted, before, after, after, afterRaw);
    }

    private int EstimateRaw(PromptFrame frame)
    {
        // SystemPrompt already includes memory and loaded skills. Count it once, then
        // add tool schemas because native mode sends them separately in ChatOptions.Tools.
        // Constrained mode also embeds tool hints in the system prompt; counting schemas
        // here is a conservative overestimate, not a send-time exact token model.
        return EstimateStatic(frame) + tokens.Count(frame.History);
    }

    private static int ApplyMargin(int raw) =>
        Math.Max(1, (int)Math.Ceiling(raw * SafetyMargin));

    private int EstimateStatic(PromptFrame frame) =>
        tokens.Count(frame.SystemPrompt) + CountToolSchemas(frame.ToolSchemas);

    private int CountToolSchemas(IReadOnlyList<JsonElement> toolSchemas) =>
        toolSchemas.Sum(schema => tokens.Count(schema.GetRawText()));

    private static int HardLimit(ContextBudget budget) =>
        Math.Max(0, budget.ContextWindowTokens - budget.ReservedOutputTokens);

    private int HistoryBudgetWithMargin(PromptFrame frame, int hardLimit)
    {
        var rawTarget = (int)Math.Floor(hardLimit / SafetyMargin);
        return Math.Max(0, rawTarget - EstimateStatic(frame));
    }

    private static (IReadOnlyList<ChatMessage> Older, IReadOnlyList<ChatMessage> Tail) SplitForCompaction(
        IReadOnlyList<ChatMessage> history,
        int keepRecentTurns)
    {
        if (history.Count == 0)
            return ([], []);

        var userTurnsSeen = 0;
        var start = history.Count;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User)
            {
                userTurnsSeen++;
                if (userTurnsSeen >= keepRecentTurns)
                {
                    start = i;
                    break;
                }
            }
        }

        if (userTurnsSeen < keepRecentTurns)
            start = Math.Max(0, history.Count - Math.Max(1, keepRecentTurns));

        start = Math.Clamp(start, 0, history.Count);
        if (start == 0)
            return ([], history);

        return (history.Take(start).ToList(), history.Skip(start).ToList());
    }
}
