// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class SearchSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_fields_from_config_writer()
    {
        var configWriter = new FakeConfigWriter { Search = new SearchOptions { Backend = "Tavily", MaxResults = 8 } };
        var viewModel = new SearchSettingsViewModel(configWriter, new FakeCredentialStore());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Tavily", viewModel.Backend);
        Assert.Equal(8, viewModel.MaxResults);
    }

    [Fact]
    public async Task SaveAsync_persists_changes()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SearchSettingsViewModel(configWriter, new FakeCredentialStore())
        {
            Backend = "Tavily",
            ApiKey = "key-123",
            MaxResults = 3,
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Tavily", configWriter.SavedSearch!.Backend);
        Assert.Equal(3, configWriter.SavedSearch.MaxResults);
    }

    [Fact]
    public async Task SaveAsync_routes_api_key_through_credential_store_not_config_file()
    {
        var configWriter = new FakeConfigWriter();
        var credentials = new FakeCredentialStore();
        var viewModel = new SearchSettingsViewModel(configWriter, credentials)
        {
            Backend = "Tavily",
            ApiKey = "key-123",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(configWriter.SavedSearch!.ApiKey);
        Assert.True(credentials.TryRead("Caliper/Search/ApiKey", out var storedKey));
        Assert.Equal("key-123", storedKey);
    }

    [Fact]
    public async Task LoadAsync_reads_api_key_from_credential_store()
    {
        var credentials = new FakeCredentialStore();
        credentials.Save("Caliper/Search/ApiKey", "stored-key");
        var viewModel = new SearchSettingsViewModel(new FakeConfigWriter(), credentials);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("stored-key", viewModel.ApiKey);
    }

    [Fact]
    public async Task SaveAsync_surfaces_writer_failure()
    {
        var configWriter = new FakeConfigWriter { NextSuccess = false, NextError = "Backend requires an API key." };
        var viewModel = new SearchSettingsViewModel(configWriter, new FakeCredentialStore()) { Backend = "Tavily" };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Equal("Backend requires an API key.", viewModel.StatusMessage);
    }
}
