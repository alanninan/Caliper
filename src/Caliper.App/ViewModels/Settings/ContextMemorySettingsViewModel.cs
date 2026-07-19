// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class ContextMemorySettingsViewModel(IConfigWriter configWriter) : ObservableObject
{
    [ObservableProperty] public partial bool AutoCompact { get; set; } = true;
    [ObservableProperty] public partial double CompactAtFraction { get; set; }
    public double CompactAtPercent
    {
        get => CompactAtFraction * 100;
        set
        {
            if (SetProperty(CompactAtFraction, value / 100, this, static (owner, fraction) => owner.CompactAtFraction = fraction))
                OnPropertyChanged();
        }
    }
    [ObservableProperty] public partial double KeepRecentTurns { get; set; }
    [ObservableProperty] public partial double ReservedOutputTokens { get; set; }
    [ObservableProperty] public partial bool MemoryEnabled { get; set; } = true;
    [ObservableProperty] public partial string MemoryGlobalDir { get; set; } = string.Empty;
    [ObservableProperty] public partial string MemoryProjectFile { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired. Context/Memory fields are read
    // live from runtimeSettings.Caliper (context compaction and memory injection both consult it
    // per turn), so ConfigWriter never reports RestartRequired: true here — kept for uniformity
    // with every other settings page rather than special-cased away.
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private Snapshot? _snapshot;

    partial void OnAutoCompactChanged(bool value) => UpdateDirty();
    partial void OnCompactAtFractionChanged(double value)
    {
        OnPropertyChanged(nameof(CompactAtPercent));
        UpdateDirty();
    }
    partial void OnKeepRecentTurnsChanged(double value) => UpdateDirty();
    partial void OnReservedOutputTokensChanged(double value) => UpdateDirty();
    partial void OnMemoryEnabledChanged(bool value) => UpdateDirty();
    partial void OnMemoryGlobalDirChanged(string value) => UpdateDirty();
    partial void OnMemoryProjectFileChanged(string value) => UpdateDirty();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var caliper = await configWriter.LoadCaliperAsync(ct);
        AutoCompact = caliper.Context.AutoCompact;
        CompactAtFraction = caliper.Context.CompactAtFraction;
        OnPropertyChanged(nameof(CompactAtPercent));
        KeepRecentTurns = caliper.Context.KeepRecentTurns;
        ReservedOutputTokens = caliper.Context.ReservedOutputTokens;
        MemoryEnabled = caliper.Memory.Enabled;
        MemoryGlobalDir = caliper.Memory.GlobalDir;
        MemoryProjectFile = caliper.Memory.ProjectFile;
        _snapshot = Capture();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        if (CompactAtFraction <= 0 || CompactAtFraction >= 1)
        {
            StatusIsError = true;
            StatusMessage = "Compact at fraction must be between 0 and 1.";
            return;
        }

        var caliper = await configWriter.LoadCaliperAsync(CancellationToken.None);
        caliper.Context.AutoCompact = AutoCompact;
        caliper.Context.CompactAtFraction = CompactAtFraction;
        caliper.Context.KeepRecentTurns = (int)KeepRecentTurns;
        caliper.Context.ReservedOutputTokens = (int)ReservedOutputTokens;
        caliper.Memory.Enabled = MemoryEnabled;
        caliper.Memory.GlobalDir = MemoryGlobalDir;
        caliper.Memory.ProjectFile = MemoryProjectFile;

        var result = await configWriter.SaveCaliperAsync(caliper, CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success ? "Saved. Changes apply to the next message you send." : result.Error ?? "Save failed.";
        if (result.Success)
        {
            _snapshot = Capture();
            IsDirty = false;
        }
    }

    [RelayCommand]
    private void Discard()
    {
        if (_snapshot is not { } value)
            return;
        AutoCompact = value.AutoCompact;
        CompactAtFraction = value.CompactAtFraction;
        KeepRecentTurns = value.KeepTurns;
        ReservedOutputTokens = value.ReservedTokens;
        MemoryEnabled = value.MemoryEnabled;
        MemoryGlobalDir = value.GlobalDir;
        MemoryProjectFile = value.ProjectFile;
        IsDirty = false;
    }

    private Snapshot Capture() => new(AutoCompact, CompactAtFraction, KeepRecentTurns,
        ReservedOutputTokens, MemoryEnabled, MemoryGlobalDir, MemoryProjectFile);
    private void UpdateDirty()
    {
        if (_snapshot is not null)
            IsDirty = Capture() != _snapshot;
    }
    private sealed record Snapshot(bool AutoCompact, double CompactAtFraction, double KeepTurns,
        double ReservedTokens, bool MemoryEnabled, string GlobalDir, string ProjectFile);
}
