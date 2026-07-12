// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Context;

internal sealed class DropOldestContextManager(ITokenCounter tokens)
{
    private const string ToolResultsOmitted = "[earlier tool results omitted]";

    // A plain assistant text message, never a Tool-role message: the placeholder must be valid
    // to send standalone, since the tool call it replaced (and its id) are gone from history.
    private static readonly ChatMessage PlaceholderMessage =
        new(ChatRole.Assistant, MessageKind.Text, ToolResultsOmitted);

    internal IReadOnlyList<ChatMessage> FitMessages(
        IReadOnlyList<ChatMessage> history,
        int inputBudgetTokens,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (history.Count == 0)
            return [];

        var budget = Math.Max(0, inputBudgetTokens);
        if (tokens.Count(history) <= budget)
            return history;

        var removed = new bool[history.Count];
        var latestUserIndex = FindLatestUserIndex(history);
        var messageTokenCounts = history.Select(message => tokens.Count(new[] { message })).ToArray();
        var placeholderTokens = tokens.Count(new[] { PlaceholderMessage });

        // Pass 1 evicts whole tool call/result pairs oldest-first (they are the largest and
        // dropping them keeps the prose turns). Pass 2 evicts anything else still over budget.
        // Removing a call and its result together avoids leaving an orphan the API would reject.
        if (Evict(history, removed, latestUserIndex, messageTokenCounts, placeholderTokens, budget, toolPairsOnly: true, ct))
            return BuildFitted(history, removed);

        Evict(history, removed, latestUserIndex, messageTokenCounts, placeholderTokens, budget, toolPairsOnly: false, ct);
        return BuildFitted(history, removed);
    }

    private static bool Evict(
        IReadOnlyList<ChatMessage> history,
        bool[] removed,
        int latestUserIndex,
        int[] messageTokenCounts,
        int placeholderTokens,
        int budget,
        bool toolPairsOnly,
        CancellationToken ct)
    {
        for (var i = 0; i < history.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (removed[i] || i == latestUserIndex)
                continue;

            var isToolMessage = history[i].Kind is MessageKind.ToolCall or MessageKind.ToolResult;
            if (toolPairsOnly && !isToolMessage)
                continue;

            RemoveWithPair(history, removed, i);
            if (CurrentTokens(history, removed, messageTokenCounts, placeholderTokens) <= budget)
                return true;
        }

        return CurrentTokens(history, removed, messageTokenCounts, placeholderTokens) <= budget;
    }

    private static void RemoveWithPair(IReadOnlyList<ChatMessage> history, bool[] removed, int index)
    {
        removed[index] = true;

        // Tool calls and their results are stored adjacently; drop the sibling with the same id
        // so neither is left dangling.
        if (history[index].Kind == MessageKind.ToolResult &&
            index > 0 &&
            history[index - 1].Kind == MessageKind.ToolCall &&
            string.Equals(history[index - 1].ToolCallId, history[index].ToolCallId, StringComparison.Ordinal))
        {
            removed[index - 1] = true;
        }
        else if (history[index].Kind == MessageKind.ToolCall &&
            index < history.Count - 1 &&
            history[index + 1].Kind == MessageKind.ToolResult &&
            string.Equals(history[index + 1].ToolCallId, history[index].ToolCallId, StringComparison.Ordinal))
        {
            removed[index + 1] = true;
        }
    }

    private static int CurrentTokens(
        IReadOnlyList<ChatMessage> history,
        bool[] removed,
        int[] messageTokenCounts,
        int placeholderTokens)
    {
        var total = 0;
        var runStart = -1;
        for (var i = 0; i < history.Count; i++)
        {
            if (removed[i])
            {
                if (runStart < 0)
                    runStart = i;
                continue;
            }

            if (runStart >= 0)
            {
                if (RunHasToolMessage(history, runStart, i - 1))
                    total += placeholderTokens;
                runStart = -1;
            }

            total += messageTokenCounts[i];
        }

        if (runStart >= 0 && RunHasToolMessage(history, runStart, history.Count - 1))
            total += placeholderTokens;

        return total;
    }

    private static bool RunHasToolMessage(IReadOnlyList<ChatMessage> history, int start, int endInclusive)
    {
        for (var i = start; i <= endInclusive; i++)
        {
            if (history[i].Kind is MessageKind.ToolCall or MessageKind.ToolResult)
                return true;
        }

        return false;
    }

    private static int FindLatestUserIndex(IReadOnlyList<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User)
                return i;
        }

        return history.Count - 1;
    }

    private static List<ChatMessage> BuildFitted(
        IReadOnlyList<ChatMessage> history,
        bool[] removed)
    {
        var fitted = new List<ChatMessage>(history.Count);
        var runStart = -1;

        for (var i = 0; i < history.Count; i++)
        {
            if (removed[i])
            {
                if (runStart < 0)
                    runStart = i;
                continue;
            }

            // Only a run that dropped tool output gets a placeholder; dropped prose is elided
            // silently (a summary/user/assistant text leaves no dangling reference).
            if (runStart >= 0)
            {
                if (RunHasToolMessage(history, runStart, i - 1))
                    fitted.Add(PlaceholderMessage);
                runStart = -1;
            }

            fitted.Add(history[i]);
        }

        if (runStart >= 0 && RunHasToolMessage(history, runStart, history.Count - 1))
            fitted.Add(PlaceholderMessage);

        return fitted;
    }
}
