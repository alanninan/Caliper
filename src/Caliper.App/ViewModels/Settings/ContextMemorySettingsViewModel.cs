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
    [ObservableProperty] public partial double KeepRecentTurns { get; set; }
    [ObservableProperty] public partial double ReservedOutputTokens { get; set; }
    [ObservableProperty] public partial bool MemoryEnabled { get; set; } = true;
    [ObservableProperty] public partial string MemoryGlobalDir { get; set; } = string.Empty;
    [ObservableProperty] public partial string MemoryProjectFile { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var caliper = await configWriter.LoadCaliperAsync(ct);
        AutoCompact = caliper.Context.AutoCompact;
        CompactAtFraction = caliper.Context.CompactAtFraction;
        KeepRecentTurns = caliper.Context.KeepRecentTurns;
        ReservedOutputTokens = caliper.Context.ReservedOutputTokens;
        MemoryEnabled = caliper.Memory.Enabled;
        MemoryGlobalDir = caliper.Memory.GlobalDir;
        MemoryProjectFile = caliper.Memory.ProjectFile;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
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
        StatusMessage = result.Success ? "Saved. Changes apply to the next message you send." : result.Error ?? "Save failed.";
    }
}
