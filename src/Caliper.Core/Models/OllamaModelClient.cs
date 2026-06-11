// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Models;

public sealed class OllamaModelClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentOptions> agentOpts,
    ILogger<OllamaModelClient> logger) : IModelClient
{
    private const string ClientName = "ollama";
    private const string NoThink = "/no_think";

    public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildRequest(request, stream: true);
        using var http = httpClientFactory.CreateClient(ClientName);

        using var response = await http
            .PostAsJsonAsync("/api/chat", body, CaliperJsonContext.Default.OllamaChatRequest, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;                          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaChatChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(line, CaliperJsonContext.Default.OllamaChatChunk);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Malformed Ollama chunk: {Line} — {Message}", line, ex.Message);
                continue;
            }

            if (chunk is null) continue;

            var delta = chunk.Message?.Content ?? string.Empty;
            yield return new ModelStreamChunk(
                TextDelta:    delta,
                Done:         chunk.Done,
                PromptTokens: chunk.Done ? chunk.PromptEvalCount : null,
                OutputTokens: chunk.Done ? chunk.EvalCount       : null);

            if (chunk.Done) yield break;
        }
    }

    public async Task<RawModelCompletion> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var body = BuildRequest(request, stream: false);
        using var http = httpClientFactory.CreateClient(ClientName);

        using var response = await http
            .PostAsJsonAsync("/api/chat", body, CaliperJsonContext.Default.OllamaChatRequest, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var chunk = await response.Content
            .ReadFromJsonAsync(CaliperJsonContext.Default.OllamaChatChunk, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama returned null response.");

        var raw = chunk.Message?.Content ?? string.Empty;
        if (request.ValidateAgentTurn)
            _ = AgentTurnParser.Parse(raw);

        return new RawModelCompletion(
            RawJson:      AgentTurnParser.ExtractJsonObject(raw),
            PromptTokens: chunk.PromptEvalCount ?? 0,
            OutputTokens: chunk.EvalCount ?? 0);
    }

    private OllamaChatRequest BuildRequest(ModelRequest request, bool stream)
    {
        var opts = agentOpts.Value;
        return new OllamaChatRequest
        {
            Model    = opts.ModelName,
            Stream   = stream,
            Format   = request.ResponseSchema,
            Messages = BuildMessages(request, opts.DisableThinking),
            Options  = new OllamaRequestOptions
            {
                NumCtx      = request.Parameters.NumCtx,
                NumPredict  = request.Parameters.MaxOutputTokens,
                Temperature = request.Parameters.Temperature,
                Seed        = request.Parameters.Seed,
                Stop        = request.Parameters.Stop,
            },
        };
    }

    private static List<OllamaChatMessage> BuildMessages(ModelRequest request, bool disableThinking)
    {
        var msgs = new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = disableThinking ? ApplyNoThink(request.System) : request.System },
        };
        foreach (var m in request.Messages)
            msgs.Add(new OllamaChatMessage { Role = RoleName(m.Role), Content = m.Content });
        return msgs;
    }

    private static string ApplyNoThink(string system) =>
        system.TrimStart().StartsWith(NoThink, StringComparison.Ordinal)
            ? system
            : $"{NoThink}\n{system}";

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.User      => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool      => "tool",
        _                  => "user",
    };
}
