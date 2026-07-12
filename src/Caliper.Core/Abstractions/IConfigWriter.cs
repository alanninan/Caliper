// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Core.Abstractions;

/// <summary>
/// Typed, per-section writer for config.json. Each method validates, persists, and — where a
/// live seam exists — applies the section immediately via <see cref="IRuntimeSettings"/>, without
/// callers needing to know config.json's JSON layout.
/// </summary>
public interface IConfigWriter
{
    // Load* always reads the current file, not IRuntimeSettings — the runtime clone only tracks
    // the "live" subset of Caliper (see ConfigWriter.SaveCaliperAsync), so a page that merges its
    // own edits onto runtimeSettings.Caliper before saving would silently revert whatever another
    // page most recently wrote for a restart-required field (e.g. EnabledTools). Callers should
    // Load immediately before Save, mutate only the fields their page owns, then Save the result.
    Task<CaliperOptions> LoadCaliperAsync(CancellationToken ct);
    Task<PermissionsOptions> LoadPermissionsAsync(CancellationToken ct);
    Task<ProvidersOptions> LoadProvidersAsync(CancellationToken ct);
    Task<McpOptions> LoadMcpAsync(CancellationToken ct);
    Task<SearchOptions> LoadSearchAsync(CancellationToken ct);
    Task<PersistenceOptions> LoadPersistenceAsync(CancellationToken ct);

    Task<ConfigWriteResult> SaveCaliperAsync(CaliperOptions value, CancellationToken ct);
    Task<ConfigWriteResult> SavePermissionsAsync(PermissionsOptions value, CancellationToken ct);
    Task<ConfigWriteResult> SaveProvidersAsync(ProvidersOptions value, CancellationToken ct);
    Task<ConfigWriteResult> SaveMcpAsync(McpOptions value, CancellationToken ct);
    Task<ConfigWriteResult> SaveSearchAsync(SearchOptions value, CancellationToken ct);
    Task<ConfigWriteResult> SavePersistenceAsync(PersistenceOptions value, CancellationToken ct);
}

public sealed record ConfigWriteResult(bool Success, string? Error, bool RestartRequired);
