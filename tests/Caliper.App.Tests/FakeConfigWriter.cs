// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests;

/// <summary>
/// Shared across settings view-model tests (mirrors the shared TestRuntimeSettings pattern in
/// ApprovalServiceTests.cs) — most settings pages depend on IConfigWriter, so one fake avoids
/// redeclaring the same six-section Load/Save surface in every test file.
/// </summary>
internal sealed class FakeConfigWriter : IConfigWriter
{
    public CaliperOptions Caliper { get; set; } = new();
    public PermissionsOptions Permissions { get; set; } = new();
    public ProvidersOptions Providers { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public SubagentsOptions Subagents { get; set; } = new();

    public bool NextSuccess { get; set; } = true;
    public string? NextError { get; set; }
    public bool NextRestartRequired { get; set; }

    public CaliperOptions? SavedCaliper { get; private set; }
    public PermissionsOptions? SavedPermissions { get; private set; }
    public ProvidersOptions? SavedProviders { get; private set; }
    public McpOptions? SavedMcp { get; private set; }
    public SearchOptions? SavedSearch { get; private set; }
    public PersistenceOptions? SavedPersistence { get; private set; }
    public SubagentsOptions? SavedSubagents { get; private set; }

    public Task<CaliperOptions> LoadCaliperAsync(CancellationToken ct) => Task.FromResult(Caliper);
    public Task<PermissionsOptions> LoadPermissionsAsync(CancellationToken ct) => Task.FromResult(Permissions);
    public Task<ProvidersOptions> LoadProvidersAsync(CancellationToken ct) => Task.FromResult(Providers);
    public Task<McpOptions> LoadMcpAsync(CancellationToken ct) => Task.FromResult(Mcp);
    public Task<SearchOptions> LoadSearchAsync(CancellationToken ct) => Task.FromResult(Search);
    public Task<PersistenceOptions> LoadPersistenceAsync(CancellationToken ct) => Task.FromResult(Persistence);
    public Task<SubagentsOptions> LoadSubagentsAsync(CancellationToken ct) => Task.FromResult(Subagents);

    public Task<ConfigWriteResult> SaveCaliperAsync(CaliperOptions value, CancellationToken ct)
    {
        SavedCaliper = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SavePermissionsAsync(PermissionsOptions value, CancellationToken ct)
    {
        SavedPermissions = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SaveProvidersAsync(ProvidersOptions value, CancellationToken ct)
    {
        SavedProviders = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SaveMcpAsync(McpOptions value, CancellationToken ct)
    {
        SavedMcp = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SaveSearchAsync(SearchOptions value, CancellationToken ct)
    {
        SavedSearch = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SavePersistenceAsync(PersistenceOptions value, CancellationToken ct)
    {
        SavedPersistence = value;
        return Task.FromResult(Result());
    }

    public Task<ConfigWriteResult> SaveSubagentsAsync(SubagentsOptions value, CancellationToken ct)
    {
        SavedSubagents = value;
        return Task.FromResult(Result());
    }

    private ConfigWriteResult Result() => new(NextSuccess, NextError, NextRestartRequired);
}
