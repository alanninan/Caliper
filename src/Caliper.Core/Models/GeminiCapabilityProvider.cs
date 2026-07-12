// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;

namespace Caliper.Core.Models;

/// <summary>
/// A curated, in-memory catalog of Gemini model capabilities. Unlike OpenRouter, Gemini's
/// OpenAI-compatible surface has no models-list endpoint worth polling for this, so known slugs
/// are hand-maintained here; anything not listed (including newer model families not yet added)
/// falls back to the same optimistic defaults Gemini's current lineup already satisfies.
/// </summary>
internal sealed class GeminiCapabilityProvider : IModelCapabilityProvider, IModelCatalog
{
    private const int GeminiContextWindow = 1_048_576;

    private static readonly Dictionary<string, ModelCapabilities> s_knownModels =
        new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-2.5-pro"] = new(SupportsTools: true, SupportsReasoning: true, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow),
            ["gemini-2.5-flash"] = new(SupportsTools: true, SupportsReasoning: true, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow),
            ["gemini-2.5-flash-lite"] = new(SupportsTools: true, SupportsReasoning: true, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow),
            ["gemini-2.0-flash"] = new(SupportsTools: true, SupportsReasoning: false, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow),
            ["gemini-2.0-flash-lite"] = new(SupportsTools: true, SupportsReasoning: false, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow),
        };

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(s_knownModels.TryGetValue(modelSlug, out var capabilities) ? capabilities : Fallback());

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelCatalogEntry>>(
        [
            .. s_knownModels
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new ModelCatalogEntry(entry.Key, entry.Value)),
        ]);

    // Optimistic, matching OpenRouterCapabilityProvider.Fallback(): assume tool/structured-output
    // support and a large context window rather than silently degrading an unlisted (e.g. newer)
    // Gemini model to respond-only mode.
    private static ModelCapabilities Fallback() =>
        new(SupportsTools: true, SupportsReasoning: true, SupportsStructuredOutputs: true, ContextWindowTokens: GeminiContextWindow);
}
