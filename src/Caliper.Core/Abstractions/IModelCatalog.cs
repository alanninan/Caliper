// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

public interface IModelCatalog
{
    Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct);

    Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct) =>
        ListAsync(ct);
}

public sealed record ModelCatalogEntry(string Id, ModelCapabilities Capabilities);
