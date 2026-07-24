// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class ToolsSettingsViewModel(
    IToolRegistry toolRegistry,
    IConfigWriter configWriter) : ObservableObject
{
    public ObservableCollection<ToolToggleViewModel> Tools { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired — set from the ConfigWriter
    // result rather than assumed, so a save that didn't actually change EnabledTools doesn't claim
    // a restart is needed.
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private HashSet<string> _loadedEnabled = new(StringComparer.OrdinalIgnoreCase);

    public string EnabledCountText => $"{Tools.Count(static t => t.IsEnabled):N0} of {Tools.Count:N0} enabled";

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var caliper = await configWriter.LoadCaliperAsync(ct);
        var enabled = new HashSet<string>(caliper.EnabledTools, StringComparer.OrdinalIgnoreCase);

        Tools.Clear();
        foreach (var tool in toolRegistry.All.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var toggle = new ToolToggleViewModel(tool.Name, tool.Description, tool.SideEffect.ToString())
            {
                IsEnabled = enabled.Contains(tool.Name),
            };
            toggle.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(EnabledCountText));
                UpdateDirty();
            };
            Tools.Add(toggle);
        }

        OnPropertyChanged(nameof(EnabledCountText));
        _loadedEnabled = [.. enabled];
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        var caliper = await configWriter.LoadCaliperAsync(CancellationToken.None);
        caliper.EnabledTools = [.. Tools.Where(static t => t.IsEnabled).Select(static t => t.Name)];

        var result = await configWriter.SaveCaliperAsync(caliper, CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success
            ? RestartRequired
                ? "Saved. Restart Caliper for the enabled tool set to take effect."
                : "Saved."
            : result.Error ?? "Save failed.";
        if (result.Success)
        {
            _loadedEnabled = [.. Tools.Where(static tool => tool.IsEnabled).Select(static tool => tool.Name)];
            IsDirty = false;
        }
    }

    [RelayCommand]
    private void Discard()
    {
        foreach (var tool in Tools)
            tool.IsEnabled = _loadedEnabled.Contains(tool.Name);
        IsDirty = false;
    }

    private void UpdateDirty()
    {
        var current = Tools.Where(static tool => tool.IsEnabled).Select(static tool => tool.Name);
        IsDirty = !_loadedEnabled.SetEquals(current);
    }
}

public sealed partial class ToolToggleViewModel(string name, string description, string sideEffect) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string SideEffect { get; } = sideEffect;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}
