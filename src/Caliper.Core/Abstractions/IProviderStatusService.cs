// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Abstractions;

public interface IProviderStatusService
{
    Task<IReadOnlyList<ProviderStatus>> ListAsync(CancellationToken ct);
    Task<ProviderStatus> GetAsync(string provider, CancellationToken ct);
}

public sealed record ProviderStatus(
    string Id,
    string DisplayName,
    ProviderAuthenticationKind AuthenticationKind,
    bool IsReady,
    string Status,
    string? Account = null);
