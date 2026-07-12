// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;

namespace Caliper.App.Tests.Settings;

public sealed class ModelsProvidersSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_provider_fields()
    {
        var configWriter = new FakeConfigWriter
        {
            Providers = new ProvidersOptions
            {
                OpenRouter = new OpenRouterOptions { Endpoint = "https://example/v1", AppTitle = "Caliper" },
            },
        };
        var viewModel = new ModelsProvidersSettingsViewModel(new FakeModelCatalog(), new TestRuntimeSettings(), configWriter, new FakeCredentialStore());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("https://example/v1", viewModel.OpenRouterEndpoint);
        Assert.Equal("Caliper", viewModel.OpenRouterAppTitle);
    }

    [Fact]
    public async Task LoadModelsAsync_populates_models_sorted_by_id()
    {
        var catalog = new FakeModelCatalog(
            new ModelCatalogEntry("beta/model", new ModelCapabilities(true, false, false, 1000)),
            new ModelCatalogEntry("alpha/model", new ModelCapabilities(true, false, false, 1000)));
        var viewModel = new ModelsProvidersSettingsViewModel(catalog, new TestRuntimeSettings(), new FakeConfigWriter(), new FakeCredentialStore());

        await viewModel.LoadModelsCommand.ExecuteAsync(null);

        Assert.Equal(["alpha/model", "beta/model"], viewModel.Models.Select(m => m.Id));
        Assert.True(viewModel.HasModels);
    }

    [Fact]
    public void FilterModels_matches_substring_case_insensitive()
    {
        var viewModel = new ModelsProvidersSettingsViewModel(new FakeModelCatalog(), new TestRuntimeSettings(), new FakeConfigWriter(), new FakeCredentialStore());
        viewModel.Models.Add(new ModelItemViewModel(new ModelCatalogEntry("openrouter/GPT-Five", new ModelCapabilities(true, false, false, 1000))));
        viewModel.Models.Add(new ModelItemViewModel(new ModelCatalogEntry("openrouter/other", new ModelCapabilities(true, false, false, 1000))));

        viewModel.FilterModels("gpt");

        Assert.Equal("openrouter/GPT-Five", Assert.Single(viewModel.FilteredModels).Id);
    }

    [Fact]
    public async Task SaveAsync_writes_caliper_and_providers_sections()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ModelsProvidersSettingsViewModel(new FakeModelCatalog(), new TestRuntimeSettings(), configWriter, new FakeCredentialStore())
        {
            CurrentProvider = "OpenRouter",
            CurrentModel = "openrouter/model",
            OpenRouterEndpoint = "https://example/v1",
            OpenRouterAppTitle = "Caliper",
            GeminiEndpoint = "https://gemini/v1",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("openrouter/model", configWriter.SavedCaliper!.Model);
        Assert.Equal("https://example/v1", configWriter.SavedProviders!.OpenRouter.Endpoint);
        Assert.Equal("https://gemini/v1", configWriter.SavedProviders.Gemini.Endpoint);
    }

    [Fact]
    public async Task SaveAsync_routes_api_keys_through_credential_store_not_config_file()
    {
        var configWriter = new FakeConfigWriter();
        var credentials = new FakeCredentialStore();
        var viewModel = new ModelsProvidersSettingsViewModel(new FakeModelCatalog(), new TestRuntimeSettings(), configWriter, credentials)
        {
            OpenRouterApiKey = "or-secret",
            GeminiApiKey = "gemini-secret",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(configWriter.SavedProviders!.OpenRouter.ApiKey);
        Assert.Null(configWriter.SavedProviders.Gemini.ApiKey);
        Assert.True(credentials.TryRead("Caliper/Providers/OpenRouter/ApiKey", out var storedOpenRouterKey));
        Assert.Equal("or-secret", storedOpenRouterKey);
        Assert.True(credentials.TryRead("Caliper/Providers/Gemini/ApiKey", out var storedGeminiKey));
        Assert.Equal("gemini-secret", storedGeminiKey);
    }

    [Fact]
    public async Task LoadAsync_reads_api_key_from_credential_store()
    {
        var credentials = new FakeCredentialStore();
        credentials.Save("Caliper/Providers/OpenRouter/ApiKey", "stored-key");
        var viewModel = new ModelsProvidersSettingsViewModel(
            new FakeModelCatalog(), new TestRuntimeSettings(), new FakeConfigWriter(), credentials);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("stored-key", viewModel.OpenRouterApiKey);
    }

    private sealed class FakeModelCatalog(params ModelCatalogEntry[] entries) : IModelCatalog
    {
        public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>(entries);
    }
}
