// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Tests.Models;

public sealed class GeminiCapabilityProviderTests
{
    [Fact]
    public async Task Known_model_returns_curated_capabilities()
    {
        var provider = new GeminiCapabilityProvider();

        var capabilities = await provider.GetAsync("gemini-2.5-flash", CancellationToken.None);

        Assert.True(capabilities.SupportsTools);
        Assert.True(capabilities.SupportsReasoning);
        Assert.True(capabilities.SupportsStructuredOutputs);
        Assert.Equal(1_048_576, capabilities.ContextWindowTokens);
    }

    [Fact]
    public async Task Known_model_lookup_is_case_insensitive()
    {
        var provider = new GeminiCapabilityProvider();

        var capabilities = await provider.GetAsync("GEMINI-2.5-PRO", CancellationToken.None);

        Assert.True(capabilities.SupportsTools);
        Assert.Equal(1_048_576, capabilities.ContextWindowTokens);
    }

    [Fact]
    public async Task Unknown_model_returns_optimistic_fallback()
    {
        var provider = new GeminiCapabilityProvider();

        // A slug from a newer Gemini family not yet added to the curated map.
        var capabilities = await provider.GetAsync("gemini-not-a-real-model", CancellationToken.None);

        Assert.True(capabilities.SupportsTools);
        Assert.True(capabilities.SupportsReasoning);
        Assert.True(capabilities.SupportsStructuredOutputs);
        Assert.Equal(1_048_576, capabilities.ContextWindowTokens);
    }

    [Fact]
    public async Task ListAsync_returns_curated_models_sorted_by_id()
    {
        var provider = new GeminiCapabilityProvider();

        var entries = await provider.ListAsync(CancellationToken.None);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.Id == "gemini-2.5-flash");
        Assert.Equal(
            entries.Select(entry => entry.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            entries.Select(entry => entry.Id));
    }
}
