// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class PermissionsSettingsViewModel(IConfigWriter configWriter) : ObservableObject
{
    public IReadOnlyList<PermissionMode> PermissionModes { get; } = Enum.GetValues<PermissionMode>();

    public ObservableCollection<string> ShellAutoAllowlist { get; } = [];
    public ObservableCollection<string> ShellDenylist { get; } = [];
    public ObservableCollection<string> AutoAllowFileRoots { get; } = [];

    [ObservableProperty] public partial PermissionMode SelectedPermissionMode { get; set; }
    [ObservableProperty] public partial bool RememberApprovals { get; set; } = true;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var permissions = await configWriter.LoadPermissionsAsync(ct);
        SelectedPermissionMode = permissions.Mode;
        RememberApprovals = permissions.RememberApprovals;
        ReplaceAll(ShellAutoAllowlist, permissions.ShellAutoAllowlist);
        ReplaceAll(ShellDenylist, permissions.ShellDenylist);
        ReplaceAll(AutoAllowFileRoots, permissions.AutoAllowFileRoots);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var permissions = new PermissionsOptions
        {
            Mode = SelectedPermissionMode,
            RememberApprovals = RememberApprovals,
            ShellAutoAllowlist = [.. ShellAutoAllowlist],
            ShellDenylist = [.. ShellDenylist],
            AutoAllowFileRoots = [.. AutoAllowFileRoots],
        };

        var result = await configWriter.SavePermissionsAsync(permissions, CancellationToken.None);
        StatusIsError = !result.Success;
        StatusMessage = result.Success ? "Saved." : result.Error ?? "Save failed.";
    }

    private static void ReplaceAll(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
