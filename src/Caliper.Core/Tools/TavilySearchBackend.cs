// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools;

public sealed class TavilySearchBackend(
    IHttpClientFactory httpClientFactory,
    IOptions<SearchOptions> opts,
    ILogger<TavilySearchBackend> logger) : ISearchBackend
{
    private const string ClientName = "tavily";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var options = opts.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "search");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Content = JsonContent.Create(
            new TavilySearchRequest(query, options.SearchDepth, options.MaxResults, options.Topic),
            CaliperJsonContext.Default.TavilySearchRequest);

        using var http = httpClientFactory.CreateClient(ClientName);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Tavily rejected the API key.",
                (System.Net.HttpStatusCode)429 => "Tavily rate limit exceeded.",
                (System.Net.HttpStatusCode)432 => "Tavily plan limit exceeded.",
                (System.Net.HttpStatusCode)433 => "Tavily pay-as-you-go limit exceeded.",
                _ => $"Tavily search failed with HTTP {(int)response.StatusCode}.",
            };
            logger.LogWarning("{Message}", message);
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        var body = await response.Content
            .ReadFromJsonAsync(CaliperJsonContext.Default.TavilySearchResponse, ct)
            .ConfigureAwait(false);

        return body?.Results?
            .Where(result => !string.IsNullOrWhiteSpace(result.Url))
            .Select(result => new SearchResult(
                result.Title ?? result.Url ?? "Untitled",
                result.Url ?? "",
                result.Content ?? ""))
            .ToList()
            ?? [];
    }
}
