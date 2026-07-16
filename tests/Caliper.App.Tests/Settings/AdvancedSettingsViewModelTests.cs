// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class AdvancedSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_raw_json_and_persistence_path()
    {
        var files = new FakeConfigFileStore("""{"Caliper":{}}""");
        var configWriter = new FakeConfigWriter { Persistence = new PersistenceOptions { SqlitePath = "/db.sqlite" } };
        var viewModel = new AdvancedSettingsViewModel(files, configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("""{"Caliper":{}}""", viewModel.RawJson);
        Assert.Equal("/db.sqlite", viewModel.PersistencePath);
    }

    [Fact]
    public async Task SavePersistenceAsync_persists_via_config_writer()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter)
        {
            PersistencePath = "/new.sqlite",
        };

        await viewModel.SavePersistenceCommand.ExecuteAsync(null);

        Assert.Equal("/new.sqlite", configWriter.SavedPersistence!.SqlitePath);
    }

    [Fact]
    public async Task SaveRawAsync_writes_through_config_file_store()
    {
        var files = new FakeConfigFileStore("{}");
        var viewModel = new AdvancedSettingsViewModel(files, new FakeConfigWriter())
        {
            RawJson = """{"Caliper":{"Model":"x"}}""",
        };

        await viewModel.SaveRawCommand.ExecuteAsync(null);

        Assert.Equal("""{"Caliper":{"Model":"x"}}""", files.Content);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SavePersistenceAsync_sets_restart_required_from_config_writer_result()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter)
        {
            PersistencePath = "/new.sqlite",
        };

        await viewModel.SavePersistenceCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SavePersistenceAsync_failed_save_does_not_set_restart_required()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter)
        {
            PersistencePath = "/new.sqlite",
        };

        await viewModel.SavePersistenceCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveRawAsync_always_sets_restart_required_on_success()
    {
        // A raw write bypasses ConfigWriter entirely — any section could have changed — so this is
        // unconditionally restart-required rather than diffing an untyped JSON blob.
        var files = new FakeConfigFileStore("{}");
        var viewModel = new AdvancedSettingsViewModel(files, new FakeConfigWriter())
        {
            RawJson = """{"Caliper":{"Model":"x"}}""",
        };

        await viewModel.SaveRawCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SaveRawAsync_invalid_json_does_not_set_restart_required()
    {
        var files = new FakeConfigFileStore("{}");
        var viewModel = new AdvancedSettingsViewModel(new ThrowingConfigFileStore(), new FakeConfigWriter())
        {
            RawJson = "not json",
        };

        await viewModel.SaveRawCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    private sealed class ThrowingConfigFileStore : IConfigFileStore
    {
        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult("{}");

        public Task WriteAsync(string json, CancellationToken ct) =>
            throw new System.Text.Json.JsonException("invalid JSON");
    }

    private sealed class FakeConfigFileStore(string content) : IConfigFileStore
    {
        public string Content { get; private set; } = content;

        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(Content);

        public Task WriteAsync(string json, CancellationToken ct)
        {
            Content = json;
            return Task.CompletedTask;
        }
    }
}
