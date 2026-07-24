// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Models;

internal sealed class CompositeModelProvider(
    string id,
    string displayName,
    ProviderAuthenticationKind authenticationKind,
    IChatClientProvider chat,
    IModelCapabilityProvider capabilities,
    IModelCatalog catalog) : IModelProvider
{
    public string Id => id;
    public string DisplayName => displayName;
    public ProviderAuthenticationKind AuthenticationKind => authenticationKind;

    public IChatClient GetClient(string modelSlug) => chat.GetClient(modelSlug);

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        capabilities.GetAsync(modelSlug, ct);

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
        catalog.ListAsync(ct);
}
