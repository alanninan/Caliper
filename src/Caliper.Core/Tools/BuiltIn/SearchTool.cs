// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class SearchTool(ISearchBackend backend, IOptions<CaliperOptions> opts) : ITool
{
    private static readonly JsonElement ParameterSchemaValue =
        JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["query"],
              "properties": { "query": { "type": "string", "maxLength": 256 } }
            }
            """).RootElement.Clone();

    public string Name        => "search";
    public string Description => "Search the web for current information. Use when you need facts, news, or specific information not in your training data.";
    public JsonElement ParameterSchema => ParameterSchemaValue;
    public SideEffect SideEffect => SideEffect.Network;

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolContext ctx,
        CancellationToken ct)
    {
        if (!arguments.TryGetProperty("query", out var queryEl))
            return new ToolResult(false, "Missing required argument: query");

        var query = queryEl.GetString();
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult(false, "Argument 'query' must not be empty.");

        var results = await backend.SearchAsync(query, ct).ConfigureAwait(false);
        return new ToolResult(true, ToolOutput.Truncate(FormatResults(results), opts.Value.ToolOutputMaxChars));
    }

    private static string FormatResults(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0) return "No results found.";
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. {r.Title}");
            sb.AppendLine($"   {r.Url}");
            sb.AppendLine($"   {r.Snippet}");
        }
        return sb.ToString();
    }
}
