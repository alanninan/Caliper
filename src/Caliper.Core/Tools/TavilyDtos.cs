// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;

namespace Caliper.Core.Tools;

internal sealed record TavilySearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("search_depth")] string SearchDepth,
    [property: JsonPropertyName("max_results")] int MaxResults,
    [property: JsonPropertyName("topic")] string Topic);

internal sealed record TavilySearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<TavilyResult>? Results);

internal sealed record TavilyResult(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("content")] string? Content);
