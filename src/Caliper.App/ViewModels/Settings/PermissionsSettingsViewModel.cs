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

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired for uniformity across settings
    // pages. SavePermissionsAsync's whole section is a live seam (PermissionGate reads
    // runtimeSettings.Permissions fresh on every call), so ConfigWriter always reports
    // RestartRequired: false here and this never actually shows the restart action — kept anyway
    // so the pattern is identical on every settings page rather than special-cased away on this
    // one.
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private Snapshot? _snapshot;

    partial void OnSelectedPermissionModeChanged(PermissionMode value) => UpdateDirty();
    partial void OnRememberApprovalsChanged(bool value) => UpdateDirty();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var permissions = await configWriter.LoadPermissionsAsync(ct);
        SelectedPermissionMode = permissions.Mode;
        RememberApprovals = permissions.RememberApprovals;
        ReplaceAll(ShellAutoAllowlist, permissions.ShellAutoAllowlist);
        ReplaceAll(ShellDenylist, permissions.ShellDenylist);
        ReplaceAll(AutoAllowFileRoots, permissions.AutoAllowFileRoots);
        _snapshot = Capture();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
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
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success ? "Saved." : result.Error ?? "Save failed.";
        if (result.Success)
        {
            _snapshot = Capture();
            IsDirty = false;
        }
    }

    public void MarkDirty() => UpdateDirty();

    [RelayCommand]
    private void Discard()
    {
        if (_snapshot is not { } value)
            return;
        SelectedPermissionMode = value.Mode;
        RememberApprovals = value.Remember;
        ReplaceAll(ShellAutoAllowlist, value.Allow);
        ReplaceAll(ShellDenylist, value.Deny);
        ReplaceAll(AutoAllowFileRoots, value.Roots);
        IsDirty = false;
    }

    private Snapshot Capture() => new(SelectedPermissionMode, RememberApprovals,
        [.. ShellAutoAllowlist], [.. ShellDenylist], [.. AutoAllowFileRoots]);
    private void UpdateDirty()
    {
        if (_snapshot is not { } value)
            return;
        IsDirty = SelectedPermissionMode != value.Mode || RememberApprovals != value.Remember ||
            !ShellAutoAllowlist.SequenceEqual(value.Allow, StringComparer.Ordinal) ||
            !ShellDenylist.SequenceEqual(value.Deny, StringComparer.Ordinal) ||
            !AutoAllowFileRoots.SequenceEqual(value.Roots, StringComparer.Ordinal);
    }
    private sealed record Snapshot(PermissionMode Mode, bool Remember, string[] Allow, string[] Deny, string[] Roots);

    private static void ReplaceAll(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
