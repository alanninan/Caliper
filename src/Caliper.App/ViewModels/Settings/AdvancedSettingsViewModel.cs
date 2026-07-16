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

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired. Shared by both save commands
    // below (the page has a single status InfoBar for both), cleared at the start of whichever ran
    // most recently.
    [ObservableProperty] public partial bool RestartRequired { get; set; }

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
        RestartRequired = false;
        var result = await configWriter.SavePersistenceAsync(
            new PersistenceOptions { SqlitePath = PersistencePath },
            CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success
            ? RestartRequired
                ? "Saved. Restart Caliper for the database path change to take effect."
                : "Saved."
            : result.Error ?? "Save failed.";
    }

    [RelayCommand]
    private async Task SaveRawAsync()
    {
        RestartRequired = false;
        try
        {
            await configFileStore.WriteAsync(RawJson, CancellationToken.None);
            StatusIsError = false;
            // A raw write bypasses ConfigWriter's typed validation and per-section restart
            // computation entirely — any section could have changed — so this is unconditionally
            // restart-required rather than trying to diff an untyped JSON blob against the
            // previous file.
            RestartRequired = true;
            StatusMessage = "Raw JSON saved. Restart Caliper to apply the changes.";
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
    }
}
