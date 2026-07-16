// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class GeneralSettingsViewModel(
    IConfigWriter configWriter,
    IAppPreferencesStore preferencesStore,
    IRuntimeSettings runtimeSettings) : ObservableObject
{
    public IReadOnlyList<AppThemePreference> ThemeOptions { get; } = Enum.GetValues<AppThemePreference>();

    [ObservableProperty]
    public partial AppThemePreference SelectedTheme { get; set; } = preferencesStore.Load().Theme;

    [ObservableProperty]
    public partial string WorkingRoot { get; set; } = runtimeSettings.Caliper.WorkingRoot;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired. WorkingRoot is a live seam
    // (AgentRunner reads runtimeSettings.Caliper.WorkingRoot fresh per run), so ConfigWriter never
    // reports RestartRequired: true for this save — kept for uniformity with every other settings
    // page rather than special-cased away.
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    partial void OnSelectedThemeChanged(AppThemePreference value) =>
        preferencesStore.Save(preferencesStore.Load() with { Theme = value });

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var current = await configWriter.LoadCaliperAsync(ct);
        WorkingRoot = current.WorkingRoot;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        var current = await configWriter.LoadCaliperAsync(CancellationToken.None);
        current.WorkingRoot = WorkingRoot;

        var result = await configWriter.SaveCaliperAsync(current, CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success
            ? "Saved."
            : result.Error ?? "Save failed.";
    }
}
