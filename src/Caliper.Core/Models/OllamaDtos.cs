// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caliper.Core.Models;

// ── /api/chat request ─────────────────────────────────────────────────────────

public sealed class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("format")]
    public JsonElement Format { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OllamaChatMessage> Messages { get; init; }

    [JsonPropertyName("options")]
    public required OllamaRequestOptions Options { get; init; }
}

public sealed class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

public sealed class OllamaRequestOptions
{
    [JsonPropertyName("num_ctx")]
    public int NumCtx { get; init; }

    [JsonPropertyName("num_predict")]
    public int NumPredict { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; init; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Stop { get; init; }
}

// ── /api/chat response chunk (streaming + non-streaming) ──────────────────────

public sealed class OllamaChatChunk
{
    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; init; }

    [JsonPropertyName("done")]
    public bool Done { get; init; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; init; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; init; }
}
