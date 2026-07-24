// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Models;

/// <summary>
/// Resolves the selected provider on every call, so runtime provider switches remain live. Unlike
/// the former two-provider router, unknown identifiers fail explicitly instead of silently
/// falling through to OpenRouter.
/// </summary>
internal sealed class ModelProviderRouter(
    IRuntimeSettings runtimeSettings,
    IEnumerable<IModelProvider> providers)
    : IChatClientProvider, IModelCapabilityProvider, IModelCatalog
{
    private readonly Dictionary<string, IModelProvider> _providers = providers.ToDictionary(
        provider => provider.Id,
        StringComparer.OrdinalIgnoreCase);

    public IChatClient GetClient(string modelSlug) =>
        Resolve(runtimeSettings.Caliper.Provider).GetClient(modelSlug);

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Resolve(runtimeSettings.Caliper.Provider).GetAsync(modelSlug, ct);

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
        Resolve(runtimeSettings.Caliper.Provider).ListAsync(ct);

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct) =>
        Resolve(provider).ListAsync(ct);

    private IModelProvider Resolve(string provider)
    {
        if (_providers.TryGetValue(provider, out var resolved))
            return resolved;

        throw new InvalidOperationException(
            $"Unknown model provider '{provider}'. Supported providers: {string.Join(", ", ProviderIds.All)}.");
    }
}
