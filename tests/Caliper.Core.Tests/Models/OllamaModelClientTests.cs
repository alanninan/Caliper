// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Text;
using System.Text.Json;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Models;

public sealed class OllamaModelClientTests
{
    [Fact]
    public async Task CompleteAsync_validates_and_returns_extracted_envelope()
    {
        const string raw = """
            ```json
            {"rationale":"ok","action":"respond","content":"done"}
            ```
            """;
        var client = BuildClient(raw);

        var turn = await client.CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("""{"rationale":"ok","action":"respond","content":"done"}""", turn.RawJson);
        Assert.Equal(7, turn.PromptTokens);
        Assert.Equal(8, turn.OutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_throws_on_invalid_envelope()
    {
        var client = BuildClient("not json");

        await Assert.ThrowsAnyAsync<JsonException>(
            () => client.CompleteAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_can_skip_agent_turn_validation_for_decision_schema()
    {
        var client = BuildClient("""{"rationale":"choose search","action":"call_tool","tool":"search"}""");

        var turn = await client.CompleteAsync(Request(validateAgentTurn: false), CancellationToken.None);

        Assert.Equal("""{"rationale":"choose search","action":"call_tool","tool":"search"}""", turn.RawJson);
    }

    [Fact]
    public async Task CompleteAsync_prefixes_system_prompt_with_no_think_when_configured()
    {
        var handler = CreateHandler("""{"rationale":"ok","action":"respond","content":"done"}""");
        var client = BuildClient(handler, disableThinking: true);

        _ = await client.CompleteAsync(Request(), CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.StartsWith("/no_think\n", system);
    }

    [Fact]
    public async Task CompleteAsync_does_not_prefix_no_think_by_default()
    {
        var handler = CreateHandler("""{"rationale":"ok","action":"respond","content":"done"}""");
        var client = BuildClient(handler);

        _ = await client.CompleteAsync(Request(), CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.RequestBody!);
        var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("system", system);
    }

    private static OllamaModelClient BuildClient(string content)
    {
        var handler = CreateHandler(content);
        return BuildClient(handler);
    }

    private static OllamaModelClient BuildClient(SingleResponseHandler handler, bool disableThinking = false)
    {
        return new OllamaModelClient(
            new StaticHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }),
            Options.Create(new AgentOptions { ModelName = "local-q1", DisableThinking = disableThinking }),
            NullLogger<OllamaModelClient>.Instance);
    }

    private static SingleResponseHandler CreateHandler(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content },
            done = true,
            prompt_eval_count = 7,
            eval_count = 8,
        });
        return new SingleResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
    }

    private static ModelRequest Request(bool validateAgentTurn = true) =>
        new(
            "system",
            [],
            ProtocolBuilder.BuildSchema([], []),
            new GenerationParameters(),
            validateAgentTurn);
}

sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return response;
    }
}

file sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
