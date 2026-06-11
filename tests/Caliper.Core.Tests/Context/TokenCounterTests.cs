// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Context;

public sealed class TokenCounterTests
{
    [Fact]
    public void Count_is_monotonic_for_longer_text()
    {
        var counter = BuildMissingFileCounter();

        Assert.True(counter.Count("short") < counter.Count("short text with more words"));
    }

    [Fact]
    public void Count_messages_includes_message_overhead()
    {
        var counter = BuildMissingFileCounter();
        var contentOnly = counter.Count("hello");
        var messageCount = counter.Count([new ChatMessage(ChatRole.User, "hello")]);

        Assert.True(messageCount > contentOnly);
    }

    [Fact]
    public void Calibration_factor_increases_future_estimates()
    {
        var counter = BuildMissingFileCounter();
        var before = counter.Count("calibration sample text");

        counter.Calibrate(before, before * 2);
        var after = counter.Count("calibration sample text");

        Assert.True(after > before);
    }

    [Fact]
    public void Missing_tokenizer_file_uses_heuristic_without_throwing()
    {
        var counter = BuildMissingFileCounter();

        Assert.True(counter.Count("hello world") > 0);
    }

    [Fact]
    public void Bpe_tokenizer_fixture_counts_known_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "caliper-tokenizer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vocabPath = Path.Combine(root, "vocab.json");
        var mergesPath = Path.Combine(root, "merges.txt");
        File.WriteAllText(vocabPath, """{"h":0,"e":1,"l":2,"o":3,"he":4,"hel":5,"hell":6,"hello":7,"w":8,"wo":9,"r":10,"wor":11,"worl":12,"d":13,"world":14}""");
        File.WriteAllText(mergesPath, "#version: 0.2\nh e\nhe l\nhel l\nhell o\nw o\nwo r\nwor l\nworl d\n");

        var counter = new TokenCounter(
            Options.Create(new TokenizerOptions
            {
                Kind = TokenizerKind.Bpe,
                VocabPath = vocabPath,
                MergesPath = mergesPath,
            }),
            NullLogger<TokenCounter>.Instance);

        Assert.InRange(counter.Count("hello"), 1, 2);
    }

    private static TokenCounter BuildMissingFileCounter() =>
        new(
            Options.Create(new TokenizerOptions
            {
                ModelPath = Path.Combine(Path.GetTempPath(), "missing-tokenizer-" + Guid.NewGuid().ToString("N") + ".json"),
            }),
            NullLogger<TokenCounter>.Instance);
}
