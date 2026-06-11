// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Text;
using Caliper.Core.Configuration;
using Caliper.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

public sealed class TavilySearchBackendTests
{
    [Fact]
    public async Task Maps_tavily_content_to_search_result_snippet()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"results":[{"title":"Title","url":"https://example.com","content":"Snippet text","score":0.9}]}""",
                Encoding.UTF8,
                "application/json"),
        });
        var backend = Build(handler);

        var results = await backend.SearchAsync("caliper search", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Title", result.Title);
        Assert.Equal("https://example.com", result.Url);
        Assert.Equal("Snippet text", result.Snippet);
    }

    [Fact]
    public async Task Sends_expected_request_body_and_authorization_header()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json"),
        });
        var backend = Build(handler);

        _ = await backend.SearchAsync("agent harness", CancellationToken.None);

        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal("tvly-test", handler.Request.Headers.Authorization?.Parameter);
        var body = handler.RequestBody;
        Assert.Contains("\"query\":\"agent harness\"", body, StringComparison.Ordinal);
        Assert.Contains("\"max_results\":3", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_success_status_throws()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var backend = Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => backend.SearchAsync("nope", CancellationToken.None));
    }

    private static TavilySearchBackend Build(HttpMessageHandler handler) =>
        new(
            new StaticHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.tavily.com/") }),
            Options.Create(new SearchOptions
            {
                Backend = "Tavily",
                ApiKey = "tvly-test",
                SearchDepth = "basic",
                MaxResults = 3,
                Topic = "general",
            }),
            NullLogger<TavilySearchBackend>.Instance);
}

file sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return response;
    }
}

file sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
