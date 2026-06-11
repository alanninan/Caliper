// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Models;
using Caliper.Core.Protocol;

namespace Caliper.Core.Tests.Agents;

public sealed class TwoPhaseStrategyTests
{
    [Fact]
    public async Task NextAsync_streams_respond_phase_after_decision()
    {
        var model = new ScriptedModelClient(
            completeTurns: ["""{"rationale":"Answer directly.","action":"respond"}"""],
            streamTurns: ["""{"rationale":"Final answer.","action":"respond","content":"Hello."}"""]);
        var strategy = new TwoPhaseStrategy(model);

        var updates = await CollectAsync(strategy.NextAsync(Context(), CancellationToken.None));

        var rationale = string.Concat(updates.OfType<RationaleDelta>().Select(u => u.Text));
        Assert.Equal("Final answer.", rationale);
        Assert.DoesNotContain("Answer directly.", rationale, StringComparison.Ordinal);
        Assert.Equal("Hello.", string.Concat(updates.OfType<ContentDelta>().Select(u => u.Text)));
        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        var response = Assert.IsType<RespondTurn>(completed.Turn);
        Assert.Equal("Hello.", response.Content);
    }

    [Fact]
    public async Task NextAsync_validates_selected_tool_branch()
    {
        var model = new ScriptedModelClient(
            completeTurns:
            [
                """{"rationale":"Search first.","action":"call_tool","tool":"search"}""",
                """{"query":"caliper"}""",
            ],
            streamTurns: []);
        var strategy = new TwoPhaseStrategy(model);
        using var argSchemaDoc = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}""");

        var updates = await CollectAsync(strategy.NextAsync(
            Context(Tools: [("search", argSchemaDoc.RootElement.Clone())]),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        var call = Assert.IsType<CallToolTurn>(completed.Turn);
        Assert.Equal("search", call.Tool);
        Assert.Equal("caliper", call.Arguments.GetProperty("query").GetString());
        var phaseTwoSchema = model.CompleteRequests[1].ResponseSchema;
        Assert.Equal("object", phaseTwoSchema.GetProperty("type").GetString());
        Assert.True(phaseTwoSchema.GetProperty("properties").TryGetProperty("query", out _));
        Assert.False(phaseTwoSchema.GetProperty("properties").TryGetProperty("action", out _));
    }

    [Fact]
    public async Task NextAsync_retries_tool_arguments_once_with_validation_error()
    {
        var model = new ScriptedModelClient(
            completeTurns:
            [
                """{"rationale":"Fetch the page.","action":"call_tool","tool":"fetch_url"}""",
                """{"url":["https://example.com"]}""",
                """{"url":"https://example.com"}""",
            ],
            streamTurns: []);
        var strategy = new TwoPhaseStrategy(model);
        using var argSchemaDoc = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["url"],"properties":{"url":{"type":"string","maxLength":2048}}}""");

        var updates = await CollectAsync(strategy.NextAsync(
            Context(Tools: [("fetch_url", argSchemaDoc.RootElement.Clone())]),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        var call = Assert.IsType<CallToolTurn>(completed.Turn);
        Assert.Equal("fetch_url", call.Tool);
        Assert.Equal("https://example.com", call.Arguments.GetProperty("url").GetString());
        Assert.Equal(3, model.CompleteRequests.Count);
        Assert.Contains("$.url must be string, got array", model.CompleteRequests[2].System);
    }

    private static TurnContext Context(
        IReadOnlyList<(string Name, JsonElement ArgumentSchema)>? Tools = null) =>
        new(
            System: "system",
            Messages: [],
            ResponseSchema: ProtocolBuilder.BuildSchema(Tools ?? [], []),
            Parameters: new GenerationParameters(),
            Tools: Tools,
            Skills: []);

    private static async Task<List<TurnUpdate>> CollectAsync(IAsyncEnumerable<TurnUpdate> updates)
    {
        var list = new List<TurnUpdate>();
        await foreach (var update in updates)
            list.Add(update);
        return list;
    }
}

file sealed class ScriptedModelClient(
    IReadOnlyList<string> completeTurns,
    IReadOnlyList<string> streamTurns) : IModelClient
{
    private int _completeIndex;
    private int _streamIndex;
    public List<ModelRequest> CompleteRequests { get; } = [];

    public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        CompleteRequests.Add(request);
        return Task.FromResult(new ModelTurn(completeTurns[_completeIndex++], 11, 13));
    }

    public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var json = streamTurns[_streamIndex++];
        yield return new ModelStreamChunk(json, Done: false, PromptTokens: null, OutputTokens: null);
        yield return new ModelStreamChunk("", Done: true, PromptTokens: 17, OutputTokens: json.Length);
        await Task.CompletedTask;
    }
}
