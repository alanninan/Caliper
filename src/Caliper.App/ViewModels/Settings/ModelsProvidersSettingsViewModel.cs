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
    ICredentialStore credentials,
    IOpenAICodexAuthService codexAuthService) : ObservableObject
{
    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<ModelItemViewModel> FilteredModels { get; } = [];
    public IReadOnlyList<ProviderOptionViewModel> ProviderOptions { get; } =
    [
        new(ProviderIds.OpenRouter, "OpenRouter"),
        new(ProviderIds.Gemini, "Google Gemini"),
        new(ProviderIds.OpenAI, "OpenAI"),
        new(ProviderIds.OpenAICodex, "OpenAI Codex"),
    ];

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
    public partial string OpenAIEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAIOrganization { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAIProject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAIApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAICodexEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenAICodexSignedOut))]
    public partial bool IsOpenAICodexAuthenticated { get; set; }

    public bool IsOpenAICodexSignedOut => !IsOpenAICodexAuthenticated;

    [ObservableProperty]
    public partial string OpenAICodexAccount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAICodexStatus { get; set; } = "Not signed in";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CatalogSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // Set when the last save changed something that provider clients only bind at startup, so the
    // page can offer a restart affordance (E3) instead of just a status line.
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    private string? _catalogProvider;
    private Snapshot? _snapshot;

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public bool CanSave => IsDirty && !IsBusy;

    public bool HasModels => Models.Count > 0;
    public string RuntimeSummary => $"{CurrentProvider} · {CurrentModel}";

    partial void OnCurrentProviderChanged(string value)
    {
        OnPropertyChanged(nameof(RuntimeSummary));
        UpdateDirty();
    }

    partial void OnCurrentModelChanged(string value)
    {
        OnPropertyChanged(nameof(RuntimeSummary));
        UpdateDirty();
    }
    partial void OnSummarizerModelChanged(string value) => UpdateDirty();
    partial void OnOpenRouterEndpointChanged(string value) => UpdateDirty();
    partial void OnOpenRouterAppTitleChanged(string value) => UpdateDirty();
    partial void OnOpenRouterAppRefererChanged(string value) => UpdateDirty();
    partial void OnOpenRouterApiKeyChanged(string value) => UpdateDirty();
    partial void OnGeminiEndpointChanged(string value) => UpdateDirty();
    partial void OnGeminiApiKeyChanged(string value) => UpdateDirty();
    partial void OnOpenAIEndpointChanged(string value) => UpdateDirty();
    partial void OnOpenAIOrganizationChanged(string value) => UpdateDirty();
    partial void OnOpenAIProjectChanged(string value) => UpdateDirty();
    partial void OnOpenAIApiKeyChanged(string value) => UpdateDirty();
    partial void OnOpenAICodexEndpointChanged(string value) => UpdateDirty();

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

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
        OpenAIEndpoint = providers.OpenAI.Endpoint;
        OpenAIOrganization = providers.OpenAI.Organization ?? string.Empty;
        OpenAIProject = providers.OpenAI.Project ?? string.Empty;
        OpenAIApiKey = credentials.TryRead(CredentialTargets.OpenAIApiKey, out var openAIKey)
            ? openAIKey
            : providers.OpenAI.ApiKey ?? string.Empty;
        OpenAICodexEndpoint = providers.OpenAICodex.Endpoint;
        await RefreshOpenAICodexStatusAsync(ct);
        RestartRequired = false;
        _snapshot = Capture();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SignInOpenAICodexAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusIsError = false;
        OpenAICodexStatus = "Opening your browser…";
        try
        {
            ApplyOpenAICodexStatus(await codexAuthService.LoginWithBrowserAsync(ct));
            StatusMessage = "OpenAI Codex sign-in completed.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            OpenAICodexStatus = "Sign-in cancelled";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
            OpenAICodexStatus = "Sign-in failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutOpenAICodexAsync(CancellationToken ct)
    {
        await codexAuthService.LogoutAsync(ct);
        ApplyOpenAICodexStatus(await codexAuthService.GetStatusAsync(ct));
        StatusIsError = false;
        StatusMessage = "Signed out of OpenAI Codex.";
    }

    [RelayCommand]
    public async Task LoadModelsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var entries = await modelCatalog.ListAsync(CurrentProvider, CancellationToken.None);
            var activeProvider = CurrentProvider;
            Models.Clear();
            foreach (var entry in entries.OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase))
                Models.Add(new ModelItemViewModel(entry, activeProvider, CurrentModel));
            FilterModels(string.Empty);
            StatusIsError = false;
            StatusMessage = string.Empty;
            CatalogSummaryText = $"{Models.Count:N0} models from {CurrentProvider}.";
            _catalogProvider = CurrentProvider;
            OnPropertyChanged(nameof(HasModels));
        }
        catch (Exception ex)
        {
            // A11: modelCatalog.ListAsync hits a live, provider-selected network endpoint
            // — the realistic failure set (HTTP, TLS, DNS, malformed JSON
            // response, provider-specific errors) spans multiple implementations and isn't safely
            // enumerable from here.
            StatusIsError = true;
            StatusMessage = ex.Message;
            CatalogSummaryText = string.Empty;
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
        CurrentModel = model;
        StatusMessage = $"Model {CurrentModel} is staged. Save to apply it.";
    }

    public void SetProvider(string provider)
    {
        CurrentProvider = provider;
        if (!string.Equals(_catalogProvider, provider, StringComparison.OrdinalIgnoreCase))
        {
            Models.Clear();
            FilteredModels.Clear();
            CatalogSummaryText = string.Empty;
            OnPropertyChanged(nameof(HasModels));
        }

        StatusMessage = $"Provider {CurrentProvider} is staged. Refresh the catalog to browse its models.";
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
        SaveOrDeleteSecret(CredentialTargets.OpenAIApiKey, OpenAIApiKey);

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
            OpenAI = new OpenAIOptions
            {
                Endpoint = OpenAIEndpoint,
                Organization = string.IsNullOrWhiteSpace(OpenAIOrganization) ? null : OpenAIOrganization,
                Project = string.IsNullOrWhiteSpace(OpenAIProject) ? null : OpenAIProject,
                ApiKey = null,
            },
            OpenAICodex = new OpenAICodexOptions
            {
                Endpoint = OpenAICodexEndpoint,
            },
        };
        var providersResult = await configWriter.SaveProvidersAsync(providers, CancellationToken.None);
        StatusIsError = !providersResult.Success;
        RestartRequired = providersResult.Success && providersResult.RestartRequired;
        StatusMessage = providersResult.Success
            ? RestartRequired
                ? "Saved. Provider/model changes are live; endpoint changes apply after restart."
                : "Saved."
            : providersResult.Error ?? "Save failed.";
        if (providersResult.Success)
        {
            runtimeSettings.SetProvider(CurrentProvider);
            runtimeSettings.SetModel(CurrentModel);
            _snapshot = Capture();
            IsDirty = false;
        }
    }

    [RelayCommand]
    private void Discard()
    {
        if (_snapshot is not { } snapshot)
            return;

        CurrentProvider = snapshot.Provider;
        CurrentModel = snapshot.Model;
        SummarizerModel = snapshot.Summarizer;
        OpenRouterEndpoint = snapshot.OpenRouterEndpoint;
        OpenRouterAppTitle = snapshot.OpenRouterTitle;
        OpenRouterAppReferer = snapshot.OpenRouterReferer;
        OpenRouterApiKey = snapshot.OpenRouterKey;
        GeminiEndpoint = snapshot.GeminiEndpoint;
        GeminiApiKey = snapshot.GeminiKey;
        OpenAIEndpoint = snapshot.OpenAIEndpoint;
        OpenAIOrganization = snapshot.OpenAIOrganization;
        OpenAIProject = snapshot.OpenAIProject;
        OpenAIApiKey = snapshot.OpenAIKey;
        OpenAICodexEndpoint = snapshot.OpenAICodexEndpoint;
        IsDirty = false;
        StatusMessage = "Changes discarded.";
    }

    public void MarkDirty() => UpdateDirty();

    private Snapshot Capture() => new(
        CurrentProvider, CurrentModel, SummarizerModel, OpenRouterEndpoint, OpenRouterAppTitle,
        OpenRouterAppReferer, OpenRouterApiKey, GeminiEndpoint, GeminiApiKey, OpenAIEndpoint,
        OpenAIOrganization, OpenAIProject, OpenAIApiKey, OpenAICodexEndpoint);

    private void UpdateDirty()
    {
        if (_snapshot is not null)
            IsDirty = Capture() != _snapshot;
    }

    private sealed record Snapshot(
        string Provider, string Model, string Summarizer, string OpenRouterEndpoint,
        string OpenRouterTitle, string OpenRouterReferer, string OpenRouterKey,
        string GeminiEndpoint, string GeminiKey, string OpenAIEndpoint,
        string OpenAIOrganization, string OpenAIProject, string OpenAIKey,
        string OpenAICodexEndpoint);

    private async Task RefreshOpenAICodexStatusAsync(CancellationToken ct) =>
        ApplyOpenAICodexStatus(await codexAuthService.GetStatusAsync(ct));

    private void ApplyOpenAICodexStatus(OpenAICodexAuthStatus status)
    {
        IsOpenAICodexAuthenticated = status.IsAuthenticated;
        OpenAICodexAccount = status.Account ?? string.Empty;
        OpenAICodexStatus = status.Status;
    }
}

public sealed class ModelItemViewModel(
    ModelCatalogEntry entry,
    string? activeProvider = null,
    string? currentModel = null)
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
    public string DefaultText { get; } =
        string.Equals(entry.Id, currentModel, StringComparison.OrdinalIgnoreCase) ? "Default" : string.Empty;
}

public sealed record ProviderOptionViewModel(string Id, string DisplayName);
