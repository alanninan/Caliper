// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;

namespace Caliper.Core.Models;

internal sealed record OpenRouterModelsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenRouterModel>? Data);

internal sealed record OpenRouterModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("supported_parameters")] IReadOnlyList<string>? SupportedParameters,
    [property: JsonPropertyName("context_length")] int? ContextLength);

internal sealed record OpenAIModelsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAIModel>? Data);

internal sealed record OpenAIModel(
    [property: JsonPropertyName("id")] string Id);

internal sealed record LoadSkillArguments(
    [property: JsonPropertyName("name")] string Name);
