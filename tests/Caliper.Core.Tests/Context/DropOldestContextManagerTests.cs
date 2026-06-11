// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Context;
using Caliper.Core.Models;

namespace Caliper.Core.Tests.Context;

public sealed class DropOldestContextManagerTests
{
    [Fact]
    public void Fits_within_budget_by_evicting_oldest_tool_result()
    {
        var manager = new DropOldestContextManager(new CharacterTokenCounter());
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "u1"),
            new ChatMessage(ChatRole.Tool, new string('x', 100), MessageKind.ToolResult),
            new ChatMessage(ChatRole.Assistant, "a1"),
            new ChatMessage(ChatRole.User, "latest"),
        };

        var fitted = manager.FitMessages(history, 40, CancellationToken.None);

        Assert.True(new CharacterTokenCounter().Count(fitted) <= 40);
        Assert.DoesNotContain(fitted, message => message.Content == new string('x', 100));
        Assert.Contains(fitted, message => message.Content == "[earlier tool results omitted]");
    }

    [Fact]
    public void Evicts_oldest_tool_results_first()
    {
        var manager = new DropOldestContextManager(new CharacterTokenCounter());
        var newerToolResult = new string('n', 10);
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "u1"),
            new ChatMessage(ChatRole.Tool, new string('o', 80), MessageKind.ToolResult),
            new ChatMessage(ChatRole.Tool, newerToolResult, MessageKind.ToolResult),
            new ChatMessage(ChatRole.User, "latest"),
        };

        var fitted = manager.FitMessages(history, 50, CancellationToken.None);

        Assert.DoesNotContain(fitted, message => message.Content == new string('o', 80));
        Assert.Contains(fitted, message => message.Content == newerToolResult);
    }

    [Fact]
    public void Inserts_one_placeholder_for_contiguous_evicted_tool_results()
    {
        var manager = new DropOldestContextManager(new CharacterTokenCounter());
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "u1"),
            new ChatMessage(ChatRole.Tool, new string('x', 60), MessageKind.ToolResult),
            new ChatMessage(ChatRole.Tool, new string('y', 60), MessageKind.ToolResult),
            new ChatMessage(ChatRole.User, "latest"),
        };

        var fitted = manager.FitMessages(history, 40, CancellationToken.None);

        Assert.Single(fitted, message => message.Content == "[earlier tool results omitted]");
    }

    [Fact]
    public void Never_drops_latest_user_message()
    {
        var manager = new DropOldestContextManager(new CharacterTokenCounter());
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "old user"),
            new ChatMessage(ChatRole.Assistant, "old assistant"),
            new ChatMessage(ChatRole.User, "latest user message"),
        };

        var fitted = manager.FitMessages(history, 1, CancellationToken.None);

        Assert.Contains(fitted, message => message.Role == ChatRole.User && message.Content == "latest user message");
    }

}

file sealed class CharacterTokenCounter : ITokenCounter
{
    public int Count(string text) => text.Length;

    public int Count(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message => Count(message.Content));

    public void Calibrate(int estimated, int actual)
    {
    }
}
