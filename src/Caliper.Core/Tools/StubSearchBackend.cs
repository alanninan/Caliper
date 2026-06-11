// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Tools;

/// <summary>
/// Deterministic stub backend for offline dev and hermetic tests.
/// Returns canned results so the agent can complete a search → answer cycle
/// without a Tavily API key.
/// </summary>
public sealed class StubSearchBackend : ISearchBackend
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        IReadOnlyList<SearchResult> results =
        [
            new SearchResult(
                Title:   $"Result 1 for: {query}",
                Url:     "https://example.com/1",
                Snippet: $"Stub result: '{query}' was found here. Configure Tavily for live results."),
            new SearchResult(
                Title:   $"Result 2 for: {query}",
                Url:     "https://example.com/2",
                Snippet: $"Another stub match for '{query}'."),
        ];
        return Task.FromResult(results);
    }
}
