// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

public sealed partial class AgentBehaviorSettingsViewModel(IConfigWriter configWriter) : ObservableObject
{
    public IReadOnlyList<TurnStrategyKind> TurnStrategyOptions { get; } = Enum.GetValues<TurnStrategyKind>();
    public IReadOnlyList<string> ReasoningEffortOptions { get; } = ["none", "low", "medium", "high", "extra-high"];

    [ObservableProperty] public partial TurnStrategyKind SelectedTurnStrategy { get; set; }
    [ObservableProperty] public partial double MaxSteps { get; set; }
    [ObservableProperty] public partial double DuplicateCallLimit { get; set; }
    [ObservableProperty] public partial double ToolTimeoutSeconds { get; set; }
    [ObservableProperty] public partial double ToolMaxRetries { get; set; }
    [ObservableProperty] public partial double ToolOutputMaxChars { get; set; }
    [ObservableProperty] public partial double Temperature { get; set; }
    [ObservableProperty] public partial string SeedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReasoningEffort { get; set; } = "medium";
    [ObservableProperty] public partial bool ExcludeReasoning { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired. Every field this page saves
    // (TurnStrategy, step/retry/timeout limits, temperature, seed, reasoning) is read live from
    // runtimeSettings.Caliper by AgentRunner/TurnStrategySelector on every turn, so ConfigWriter
    // never reports RestartRequired: true here — kept for uniformity with every other settings
    // page rather than special-cased away.
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private Snapshot? _snapshot;

    partial void OnSelectedTurnStrategyChanged(TurnStrategyKind value) => UpdateDirty();
    partial void OnMaxStepsChanged(double value) => UpdateDirty();
    partial void OnDuplicateCallLimitChanged(double value) => UpdateDirty();
    partial void OnToolTimeoutSecondsChanged(double value) => UpdateDirty();
    partial void OnToolMaxRetriesChanged(double value) => UpdateDirty();
    partial void OnToolOutputMaxCharsChanged(double value) => UpdateDirty();
    partial void OnTemperatureChanged(double value) => UpdateDirty();
    partial void OnSeedTextChanged(string value) => UpdateDirty();
    partial void OnReasoningEffortChanged(string value) => UpdateDirty();
    partial void OnExcludeReasoningChanged(bool value) => UpdateDirty();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var caliper = await configWriter.LoadCaliperAsync(ct);
        SelectedTurnStrategy = caliper.TurnStrategy;
        MaxSteps = caliper.MaxSteps;
        DuplicateCallLimit = caliper.DuplicateCallLimit;
        ToolTimeoutSeconds = caliper.ToolTimeoutSeconds;
        ToolMaxRetries = caliper.ToolMaxRetries;
        ToolOutputMaxChars = caliper.ToolOutputMaxChars;
        Temperature = caliper.Temperature;
        SeedText = caliper.Seed?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ReasoningEffort = caliper.Reasoning.Effort;
        ExcludeReasoning = caliper.Reasoning.Exclude;
        _snapshot = Capture();
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        int? seed = null;
        if (!string.IsNullOrWhiteSpace(SeedText))
        {
            if (!int.TryParse(SeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                StatusIsError = true;
                StatusMessage = "Seed must be an integer or empty.";
                return;
            }
            seed = parsed;
        }

        var caliper = await configWriter.LoadCaliperAsync(CancellationToken.None);
        caliper.TurnStrategy = SelectedTurnStrategy;
        caliper.MaxSteps = (int)MaxSteps;
        caliper.DuplicateCallLimit = (int)DuplicateCallLimit;
        caliper.ToolTimeoutSeconds = (int)ToolTimeoutSeconds;
        caliper.ToolMaxRetries = (int)ToolMaxRetries;
        caliper.ToolOutputMaxChars = (int)ToolOutputMaxChars;
        caliper.Temperature = Temperature;
        caliper.Seed = seed;
        caliper.Reasoning.Effort = ReasoningEffort;
        caliper.Reasoning.Exclude = ExcludeReasoning;

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
        SelectedTurnStrategy = value.Strategy;
        MaxSteps = value.MaxSteps;
        DuplicateCallLimit = value.DuplicateLimit;
        ToolTimeoutSeconds = value.Timeout;
        ToolMaxRetries = value.Retries;
        ToolOutputMaxChars = value.OutputChars;
        Temperature = value.Temperature;
        SeedText = value.Seed;
        ReasoningEffort = value.Effort;
        ExcludeReasoning = value.Exclude;
        IsDirty = false;
    }

    private Snapshot Capture() => new(SelectedTurnStrategy, MaxSteps, DuplicateCallLimit,
        ToolTimeoutSeconds, ToolMaxRetries, ToolOutputMaxChars, Temperature, SeedText,
        ReasoningEffort, ExcludeReasoning);
    private void UpdateDirty()
    {
        if (_snapshot is not null)
            IsDirty = Capture() != _snapshot;
    }
    private sealed record Snapshot(TurnStrategyKind Strategy, double MaxSteps, double DuplicateLimit,
        double Timeout, double Retries, double OutputChars, double Temperature, string Seed,
        string Effort, bool Exclude);
}
