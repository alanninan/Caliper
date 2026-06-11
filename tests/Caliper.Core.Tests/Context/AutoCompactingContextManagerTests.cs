// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Context;

public sealed class AutoCompactingContextManagerTests
{
    [Fact]
    public async Task Under_threshold_does_not_compact()
    {
        var summarizer = new FakeSummarizer("summary");
        var manager = Build(summarizer);
        var history = new[] { new ChatMessage(ChatRole.User, "short") };

        var fit = await manager.FitAsync(Frame(history), Budget(window: 1000), CancellationToken.None);

        Assert.False(fit.Compacted);
        Assert.Equal(0, summarizer.Count);
        Assert.Equal(history, fit.Messages);
    }

    [Fact]
    public async Task Above_threshold_summarizes_older_span_and_preserves_recent_turns()
    {
        var summarizer = new FakeSummarizer("compressed facts");
        var manager = Build(summarizer, keepRecentTurns: 1);
        var history = new[]
        {
            new ChatMessage(ChatRole.User, new string('a', 300)),
            new ChatMessage(ChatRole.Assistant, new string('b', 300)),
            new ChatMessage(ChatRole.User, "latest task"),
            new ChatMessage(ChatRole.Assistant, "latest answer"),
        };

        var fit = await manager.FitAsync(Frame(history), Budget(window: 500), CancellationToken.None);

        Assert.True(fit.Compacted);
        Assert.Equal(1, summarizer.Count);
        Assert.Equal(MessageKind.Summary, fit.Messages[0].Kind);
        Assert.Contains("compressed facts", fit.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(fit.Messages, message => message.Content == "latest task");
    }

    [Fact]
    public async Task AutoCompact_false_does_not_call_summarizer()
    {
        var summarizer = new FakeSummarizer("summary");
        var manager = Build(summarizer, autoCompact: false);
        var history = new[]
        {
            new ChatMessage(ChatRole.User, new string('a', 500)),
            new ChatMessage(ChatRole.Assistant, new string('b', 500)),
            new ChatMessage(ChatRole.User, "latest"),
        };

        var fit = await manager.FitAsync(Frame(history), Budget(window: 100), CancellationToken.None);

        Assert.False(fit.Compacted);
        Assert.Equal(0, summarizer.Count);
        Assert.Equal(history, fit.Messages);
    }

    [Fact]
    public async Task Trim_fallback_targets_margin_adjusted_budget()
    {
        var manager = Build(new FakeSummarizer(new string('s', 500)), keepRecentTurns: 1);
        var history = new[]
        {
            new ChatMessage(ChatRole.User, new string('a', 500)),
            new ChatMessage(ChatRole.Assistant, new string('b', 500)),
            new ChatMessage(ChatRole.User, new string('c', 80)),
        };

        var fit = await manager.FitAsync(Frame(history), Budget(window: 120), CancellationToken.None);

        Assert.True(fit.Compacted);
        Assert.True(fit.EstimatedPromptTokens <= 120);
    }

    [Fact]
    public async Task Unfittable_static_prompt_returns_over_budget_estimate()
    {
        var manager = Build(new FakeSummarizer(new string('s', 1000)), keepRecentTurns: 1);
        var frame = new PromptFrame(new string('x', 1000), [new ChatMessage(ChatRole.User, "latest")], []);

        var fit = await manager.FitAsync(frame, Budget(window: 100), CancellationToken.None);

        Assert.True(fit.EstimatedPromptTokens > 100);
    }

    private static AutoCompactingContextManager Build(
        FakeSummarizer summarizer,
        bool autoCompact = true,
        int keepRecentTurns = 2) =>
        new(
            new AutoCompactCharacterTokenCounter(),
            summarizer,
            new RuntimeSettings(
                Options.Create(new CaliperOptions
                {
                    Context = new ContextOptions
                    {
                        AutoCompact = autoCompact,
                        KeepRecentTurns = keepRecentTurns,
                        CompactAtFraction = 0.5,
                        ReservedOutputTokens = 0,
                    },
                }),
                Options.Create(new PermissionsOptions())),
            NullLogger<AutoCompactingContextManager>.Instance);

    private static PromptFrame Frame(IReadOnlyList<ChatMessage> history) =>
        new("system", history, []);

    private static ContextBudget Budget(int window) =>
        new(window, ReservedOutputTokens: 0, CompactAtFraction: 0.5);
}

sealed class FakeSummarizer(string summary) : ISummarizer
{
    public int Count { get; private set; }

    public Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> olderSpan, CancellationToken ct)
    {
        Count++;
        return Task.FromResult(summary);
    }
}

sealed class AutoCompactCharacterTokenCounter : ITokenCounter
{
    public int Count(string text) => text.Length;

    public int Count(IEnumerable<ChatMessage> messages) =>
        messages.Sum(message => Count(message.Content));

    public void Calibrate(int estimated, int actual)
    {
    }
}
