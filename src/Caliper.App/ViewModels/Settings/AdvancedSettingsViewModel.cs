// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class AdvancedSettingsViewModel(
    IConfigFileStore configFileStore,
    IConfigWriter configWriter) : ObservableObject
{
    [ObservableProperty] public partial string RawJson { get; set; } = string.Empty;
    [ObservableProperty] public partial string PersistencePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        RawJson = await configFileStore.ReadAsync(ct);
        var persistence = await configWriter.LoadPersistenceAsync(ct);
        PersistencePath = persistence.SqlitePath;
    }

    [RelayCommand]
    private async Task SavePersistenceAsync()
    {
        var result = await configWriter.SavePersistenceAsync(
            new PersistenceOptions { SqlitePath = PersistencePath },
            CancellationToken.None);
        StatusIsError = !result.Success;
        StatusMessage = result.Success
            ? "Saved. Restart Caliper for the database path change to take effect."
            : result.Error ?? "Save failed.";
    }

    [RelayCommand]
    private async Task SaveRawAsync()
    {
        try
        {
            await configFileStore.WriteAsync(RawJson, CancellationToken.None);
            StatusIsError = false;
            StatusMessage = "Raw JSON saved. Restart Caliper to apply the changes.";
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
    }
}
