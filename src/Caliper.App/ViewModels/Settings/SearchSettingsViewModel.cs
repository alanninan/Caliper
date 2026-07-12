// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Security;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class SearchSettingsViewModel(IConfigWriter configWriter, ICredentialStore credentials) : ObservableObject
{
    public IReadOnlyList<string> BackendOptions { get; } = ["Stub", "Tavily"];
    public IReadOnlyList<string> SearchDepthOptions { get; } = ["basic", "advanced"];
    public IReadOnlyList<string> TopicOptions { get; } = ["general", "news", "finance"];

    [ObservableProperty] public partial string Backend { get; set; } = "Stub";
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchDepth { get; set; } = "basic";
    [ObservableProperty] public partial double MaxResults { get; set; } = 5;
    [ObservableProperty] public partial string Topic { get; set; } = "general";
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var search = await configWriter.LoadSearchAsync(ct);
        Backend = search.Backend;
        ApiKey = credentials.TryRead(CredentialTargets.SearchApiKey, out var storedKey)
            ? storedKey
            : search.ApiKey ?? string.Empty;
        SearchDepth = search.SearchDepth;
        MaxResults = search.MaxResults;
        Topic = search.Topic;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            credentials.Delete(CredentialTargets.SearchApiKey);
        else
            credentials.Save(CredentialTargets.SearchApiKey, ApiKey);

        var search = new SearchOptions
        {
            Backend = Backend,
            ApiKey = null,
            SearchDepth = SearchDepth,
            MaxResults = (int)MaxResults,
            Topic = Topic,
        };

        var result = await configWriter.SaveSearchAsync(search, CancellationToken.None);
        StatusIsError = !result.Success;
        StatusMessage = result.Success
            ? "Saved. Restart Caliper for search changes to take effect."
            : result.Error ?? "Save failed.";
    }
}
