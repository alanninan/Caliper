// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.App.Security;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class ModelsProvidersSettingsViewModel(
    IModelCatalog modelCatalog,
    IRuntimeSettings runtimeSettings,
    IConfigWriter configWriter,
    ICredentialStore credentials) : ObservableObject
{
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<ModelItemViewModel> FilteredModels { get; } = [];
    public IReadOnlyList<string> ProviderOptions { get; } = ["OpenRouter", "Gemini"];

    [ObservableProperty]
    public partial string CurrentProvider { get; set; } = runtimeSettings.Caliper.Provider;

    [ObservableProperty]
    public partial string CurrentModel { get; set; } = runtimeSettings.Caliper.Model;

    [ObservableProperty]
    public partial string SummarizerModel { get; set; } = runtimeSettings.Caliper.SummarizerModel ?? string.Empty;

    [ObservableProperty]
    public partial string OpenRouterEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenRouterAppTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenRouterAppReferer { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenRouterApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GeminiEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GeminiApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // Set when the last save changed something that provider clients only bind at startup, so the
    // page can offer a restart affordance (E3) instead of just a status line.
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    // The API keys live in Credential Manager, not config.json, so ConfigWriter can't see whether
    // they changed. Remember what LoadAsync read so SaveAsync can tell if a key was edited and fold
    // that into the restart decision (bound provider clients only pick up a new key on restart).
    private string _loadedOpenRouterApiKey = string.Empty;
    private string _loadedGeminiApiKey = string.Empty;

    public bool HasModels => Models.Count > 0;
    public string RuntimeSummary => $"{CurrentProvider} · {CurrentModel}";

    partial void OnCurrentProviderChanged(string value) => OnPropertyChanged(nameof(RuntimeSummary));
    partial void OnCurrentModelChanged(string value) => OnPropertyChanged(nameof(RuntimeSummary));

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var providers = await configWriter.LoadProvidersAsync(ct);
        OpenRouterEndpoint = providers.OpenRouter.Endpoint;
        OpenRouterAppTitle = providers.OpenRouter.AppTitle;
        OpenRouterAppReferer = providers.OpenRouter.AppReferer ?? string.Empty;
        OpenRouterApiKey = credentials.TryRead(CredentialTargets.OpenRouterApiKey, out var openRouterKey)
            ? openRouterKey
            : providers.OpenRouter.ApiKey ?? string.Empty;
        GeminiEndpoint = providers.Gemini.Endpoint;
        GeminiApiKey = credentials.TryRead(CredentialTargets.GeminiApiKey, out var geminiKey)
            ? geminiKey
            : providers.Gemini.ApiKey ?? string.Empty;
        _loadedOpenRouterApiKey = OpenRouterApiKey;
        _loadedGeminiApiKey = GeminiApiKey;
        RestartRequired = false;
    }

    [RelayCommand]
    public async Task LoadModelsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var entries = await modelCatalog.ListAsync(CancellationToken.None);
            var activeProvider = runtimeSettings.Caliper.Provider;
            Models.Clear();
            foreach (var entry in entries.OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase))
                Models.Add(new ModelItemViewModel(entry, activeProvider));
            FilterModels(string.Empty);
            CurrentProvider = runtimeSettings.Caliper.Provider;
            CurrentModel = runtimeSettings.Caliper.Model;
            StatusMessage = $"{Models.Count:N0} models available from {CurrentProvider}.";
            OnPropertyChanged(nameof(HasModels));
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void FilterModels(string? query)
    {
        FilteredModels.Clear();
        foreach (var model in Models
                     .Where(item => string.IsNullOrWhiteSpace(query) ||
                         item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .Take(50))
        {
            FilteredModels.Add(model);
        }
    }

    public void SetModel(string model)
    {
        runtimeSettings.SetModel(model);
        CurrentModel = runtimeSettings.Caliper.Model;
        StatusMessage = $"Model changed to {CurrentModel}.";
    }

    public void SetProvider(string provider)
    {
        runtimeSettings.SetProvider(provider);
        CurrentProvider = runtimeSettings.Caliper.Provider;
        StatusMessage = $"Provider changed to {CurrentProvider}. Refresh the catalog to load provider-specific models.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var caliper = await configWriter.LoadCaliperAsync(CancellationToken.None);
        caliper.Provider = CurrentProvider;
        caliper.Model = CurrentModel;
        caliper.SummarizerModel = string.IsNullOrWhiteSpace(SummarizerModel) ? null : SummarizerModel;
        var caliperResult = await configWriter.SaveCaliperAsync(caliper, CancellationToken.None);
        if (!caliperResult.Success)
        {
            StatusIsError = true;
            StatusMessage = caliperResult.Error ?? "Save failed.";
            return;
        }

        void SaveOrDeleteSecret(string target, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                credentials.Delete(target);
            else
                credentials.Save(target, value);
        }

        SaveOrDeleteSecret(CredentialTargets.OpenRouterApiKey, OpenRouterApiKey);
        SaveOrDeleteSecret(CredentialTargets.GeminiApiKey, GeminiApiKey);
        var apiKeyChanged =
            !string.Equals(OpenRouterApiKey, _loadedOpenRouterApiKey, StringComparison.Ordinal) ||
            !string.Equals(GeminiApiKey, _loadedGeminiApiKey, StringComparison.Ordinal);
        _loadedOpenRouterApiKey = OpenRouterApiKey;
        _loadedGeminiApiKey = GeminiApiKey;

        var providers = new ProvidersOptions
        {
            OpenRouter = new OpenRouterOptions
            {
                Endpoint = OpenRouterEndpoint,
                AppTitle = OpenRouterAppTitle,
                AppReferer = string.IsNullOrWhiteSpace(OpenRouterAppReferer) ? null : OpenRouterAppReferer,
                ApiKey = null,
            },
            Gemini = new GeminiOptions
            {
                Endpoint = GeminiEndpoint,
                ApiKey = null,
            },
        };
        var providersResult = await configWriter.SaveProvidersAsync(providers, CancellationToken.None);
        StatusIsError = !providersResult.Success;
        RestartRequired = providersResult.Success && (providersResult.RestartRequired || apiKeyChanged);
        StatusMessage = providersResult.Success
            ? RestartRequired
                ? "Saved. Provider/model changes are live; provider endpoint and key changes apply after restart."
                : "Saved."
            : providersResult.Error ?? "Save failed.";
    }
}

public sealed class ModelItemViewModel(ModelCatalogEntry entry, string? activeProvider = null)
{
    public string Id { get; } = entry.Id;

    // Slash-prefixed OpenRouter ids carry their vendor (e.g. "google/gemini-…"); a bare id (Gemini's
    // native catalog) has none, so fall back to the active provider rather than a bare "custom".
    public string Provider { get; } = entry.Id.Contains('/', StringComparison.Ordinal)
        ? entry.Id.Split('/', 2)[0]
        : string.IsNullOrWhiteSpace(activeProvider) ? "custom" : activeProvider;
    public string Capabilities { get; } = string.Join("  ·  ", new[]
    {
        entry.Capabilities.SupportsTools ? "Tools" : null,
        entry.Capabilities.SupportsReasoning ? "Reasoning" : null,
        entry.Capabilities.SupportsStructuredOutputs ? "Structured output" : null,
        $"{entry.Capabilities.ContextWindowTokens:N0} context",
    }.Where(static value => value is not null));
}
