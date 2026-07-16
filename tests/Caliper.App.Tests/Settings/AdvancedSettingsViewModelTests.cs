// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.App.Tests.Settings;

public sealed class AdvancedSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_raw_json_and_persistence_path()
    {
        var files = new FakeConfigFileStore("""{"Caliper":{}}""");
        var configWriter = new FakeConfigWriter { Persistence = new PersistenceOptions { SqlitePath = "/db.sqlite" } };
        var viewModel = new AdvancedSettingsViewModel(files, configWriter, new FakeTimeProvider());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("""{"Caliper":{}}""", viewModel.RawJson);
        Assert.Equal("/db.sqlite", viewModel.PersistencePath);
    }

    [Fact]
    public async Task SavePersistenceAsync_persists_via_config_writer()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter, new FakeTimeProvider())
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
        var viewModel = new AdvancedSettingsViewModel(files, new FakeConfigWriter(), new FakeTimeProvider())
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
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter, new FakeTimeProvider())
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
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), configWriter, new FakeTimeProvider())
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
        var viewModel = new AdvancedSettingsViewModel(files, new FakeConfigWriter(), new FakeTimeProvider())
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
        var viewModel = new AdvancedSettingsViewModel(new ThrowingConfigFileStore(), new FakeConfigWriter(), new FakeTimeProvider())
        {
            RawJson = "not json",
        };

        await viewModel.SaveRawCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    [Fact]
    public async Task Editing_invalid_json_sets_HasJsonError_after_the_debounce_elapses()
    {
        var time = new FakeTimeProvider();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), new FakeConfigWriter(), time);

        viewModel.RawJson = "{ not valid json";
        Assert.False(viewModel.HasJsonError); // nothing validated yet — still debouncing

        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        await viewModel.JsonValidationTask;

        Assert.True(viewModel.HasJsonError);
        Assert.NotEmpty(viewModel.JsonValidationMessage);
    }

    [Fact]
    public async Task Editing_back_to_valid_json_clears_the_error()
    {
        var time = new FakeTimeProvider();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), new FakeConfigWriter(), time);

        viewModel.RawJson = "not json";
        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        await viewModel.JsonValidationTask;
        Assert.True(viewModel.HasJsonError);

        viewModel.RawJson = """{"Caliper":{}}""";
        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        await viewModel.JsonValidationTask;

        Assert.False(viewModel.HasJsonError);
        Assert.Equal(string.Empty, viewModel.JsonValidationMessage);
    }

    [Fact]
    public async Task Rapid_successive_edits_only_validate_the_last_text()
    {
        var time = new FakeTimeProvider();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), new FakeConfigWriter(), time);

        viewModel.RawJson = "{ unclosed";
        time.Advance(TimeSpan.FromMilliseconds(200)); // well short of the 500ms debounce
        Assert.False(viewModel.HasJsonError); // no premature validation of the first edit

        viewModel.RawJson = """{"Caliper":{}}"""; // cancels the first edit's pending validation
        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        await viewModel.JsonValidationTask;

        Assert.False(viewModel.HasJsonError);
    }

    [Fact]
    public async Task A_stale_cancelled_validation_never_overwrites_the_newer_result()
    {
        var time = new FakeTimeProvider();
        var viewModel = new AdvancedSettingsViewModel(new FakeConfigFileStore("{}"), new FakeConfigWriter(), time);

        viewModel.RawJson = "{ unclosed"; // would be invalid if it ever got validated
        time.Advance(TimeSpan.FromMilliseconds(100));
        viewModel.RawJson = """{"Caliper":{}}"""; // valid — cancels the first edit
        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        await viewModel.JsonValidationTask;
        Assert.False(viewModel.HasJsonError);

        // Even if more time passes, the cancelled first validation can't resurface and flip the
        // result back to an error.
        time.Advance(AdvancedSettingsViewModel.JsonValidationDebounce);
        Assert.False(viewModel.HasJsonError);
        Assert.Equal(string.Empty, viewModel.JsonValidationMessage);
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
