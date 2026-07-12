// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Models;

/// <summary>
/// Dispatches chat-client, capability, and catalog calls to the concrete provider selected by
/// <see cref="Configuration.CaliperOptions.Provider"/>, read fresh on every call (not cached at
/// construction) so a runtime provider switch — e.g. the console's <c>/set provider Gemini</c> —
/// takes effect on the next model call without rebuilding this router or any of its dependents.
/// </summary>
internal sealed class ModelProviderRouter(
    IRuntimeSettings runtimeSettings,
    OpenRouterChatClientProvider openRouterChat,
    GeminiChatClientProvider geminiChat,
    OpenRouterCapabilityProvider openRouterCapabilities,
    GeminiCapabilityProvider geminiCapabilities)
    : IChatClientProvider, IModelCapabilityProvider, IModelCatalog
{
    public IChatClient GetClient(string modelSlug) =>
        IsGemini() ? geminiChat.GetClient(modelSlug) : openRouterChat.GetClient(modelSlug);

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        IsGemini() ? geminiCapabilities.GetAsync(modelSlug, ct) : openRouterCapabilities.GetAsync(modelSlug, ct);

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
        IsGemini() ? geminiCapabilities.ListAsync(ct) : openRouterCapabilities.ListAsync(ct);

    // Unrecognized or unset provider values fall through to OpenRouter, preserving today's only
    // behavior for existing configs.
    private bool IsGemini() =>
        string.Equals(runtimeSettings.Caliper.Provider, "Gemini", StringComparison.OrdinalIgnoreCase);
}
