// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

public sealed class SearchToolTests
{
    [Fact]
    public async Task Truncates_long_search_output()
    {
        var tool = new SearchTool(
            new FixedSearchBackend(),
            Options.Create(new CaliperOptions { Model = "test", ToolOutputMaxChars = 80 }));

        var result = await tool.InvokeAsync(
            System.Text.Json.JsonDocument.Parse("""{"query":"test"}""").RootElement,
            new ToolContext(new NullHttpClientFactory(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ".", ".", false, CancellationToken.None),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Output.Length <= 80);
        Assert.EndsWith("[truncated]", result.Output, StringComparison.Ordinal);
    }
}

file sealed class FixedSearchBackend : ISearchBackend
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SearchResult>>(
        [
            new("Title", "https://example.com", new string('s', 500)),
        ]);
}

file sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
