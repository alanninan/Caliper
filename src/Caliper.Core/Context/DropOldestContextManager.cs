// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Context;

internal sealed class DropOldestContextManager(ITokenCounter tokens)
{
    private const string ToolResultsOmitted = "[earlier tool results omitted]";

    internal IReadOnlyList<ChatMessage> FitMessages(
        IReadOnlyList<ChatMessage> history,
        int inputBudgetTokens,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (history.Count == 0)
            return [];

        var budget = Math.Max(0, inputBudgetTokens);
        var removed = new bool[history.Count];
        var latestUserIndex = FindLatestUserIndex(history);
        var messageTokenCounts = history.Select(message => tokens.Count(new[] { message })).ToArray();
        var placeholderTokens = tokens.Count(new[]
        {
            new ChatMessage(ChatRole.Tool, MessageKind.ToolResult, ToolResultsOmitted),
        });
        var totalTokens = tokens.Count(history);

        if (totalTokens <= budget)
            return history;

        foreach (var index in Enumerable.Range(0, history.Count)
            .Where(i => i != latestUserIndex && history[i].Kind == MessageKind.ToolResult))
        {
            ct.ThrowIfCancellationRequested();

            Remove(index, history, removed, messageTokenCounts, placeholderTokens, ref totalTokens);
            if (totalTokens <= budget)
                return BuildFitted(history, removed);
        }

        foreach (var index in Enumerable.Range(0, history.Count)
            .Where(i => i != latestUserIndex && !removed[i]))
        {
            ct.ThrowIfCancellationRequested();

            Remove(index, history, removed, messageTokenCounts, placeholderTokens, ref totalTokens);
            if (totalTokens <= budget)
                return BuildFitted(history, removed);
        }

        return BuildFitted(history, removed);
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

    private static void Remove(
        int index,
        IReadOnlyList<ChatMessage> history,
        bool[] removed,
        int[] messageTokenCounts,
        int placeholderTokens,
        ref int totalTokens)
    {
        removed[index] = true;
        totalTokens -= messageTokenCounts[index];

        if (history[index].Kind != MessageKind.ToolResult)
            return;

        var joinsLeftRun = index > 0
            && removed[index - 1]
            && history[index - 1].Kind == MessageKind.ToolResult;
        var joinsRightRun = index < history.Count - 1
            && removed[index + 1]
            && history[index + 1].Kind == MessageKind.ToolResult;

        if (!joinsLeftRun && !joinsRightRun)
            totalTokens += placeholderTokens;
        else if (joinsLeftRun && joinsRightRun)
            totalTokens -= placeholderTokens;
    }

    private static List<ChatMessage> BuildFitted(
        IReadOnlyList<ChatMessage> history,
        bool[] removed)
    {
        var fitted = new List<ChatMessage>(history.Count);
        var inRemovedToolRun = false;

        for (var i = 0; i < history.Count; i++)
        {
            if (!removed[i])
            {
                fitted.Add(history[i]);
                inRemovedToolRun = false;
                continue;
            }

            if (history[i].Kind != MessageKind.ToolResult)
            {
                inRemovedToolRun = false;
                continue;
            }

            if (inRemovedToolRun)
                continue;

            fitted.Add(new ChatMessage(ChatRole.Tool, MessageKind.ToolResult, ToolResultsOmitted));
            inRemovedToolRun = true;
        }

        return fitted;
    }
}
