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
    IConfigWriter configWriter,
    TimeProvider timeProvider) : ObservableObject
{
    // U11: advisory-only debounce for the raw editor's inline validation — Save always does its
    // own independent JsonDocument.Parse (via IConfigFileStore.WriteAsync), so a stale/skipped
    // debounce here can never let invalid JSON actually get written. Internal (not private) so
    // FakeTimeProvider-based tests can advance by exactly this amount instead of a duplicated
    // magic number.
    internal static readonly TimeSpan JsonValidationDebounce = TimeSpan.FromMilliseconds(500);

    private CancellationTokenSource? _jsonValidationCts;
    private string _loadedRawJson = string.Empty;
    private string _loadedPersistencePath = string.Empty;

    // U11 test seam: the most recently scheduled debounce+validate task. Production code never
    // reads this — it exists so FakeTimeProvider-based tests can deterministically await the
    // fire-and-forget validation after advancing time, rather than racing the thread pool that
    // runs its continuation.
    internal Task JsonValidationTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty] public partial string RawJson { get; set; } = string.Empty;
    [ObservableProperty] public partial string PersistencePath { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    // U11: empty when the raw JSON is valid (or hasn't been validated yet); otherwise the parse
    // error, formatted with line/position from JsonException.
    [ObservableProperty] public partial string JsonValidationMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasJsonError { get; set; }

    // U5: mirrors ModelsProvidersSettingsViewModel.RestartRequired. Shared by both save commands
    // below (the page has a single status InfoBar for both), cleared at the start of whichever ran
    // most recently.
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsRawDirty { get; set; }
    [ObservableProperty] public partial bool IsPersistenceDirty { get; set; }

    // U11: debounces ~500ms after the last keystroke before validating. Cancels any in-flight
    // validation for the previous text so only the latest edit is ever checked. The async work is
    // contained entirely in ScheduleJsonValidationAsync below (fire-and-forget from here, but every
    // exception it can throw — TaskCanceledException from the delay, JsonException from the parse
    // — is caught inside it), so this partial method itself never needs to be async.
    partial void OnRawJsonChanged(string value)
    {
        IsRawDirty = !string.Equals(value, _loadedRawJson, StringComparison.Ordinal);
        SaveRawCommand.NotifyCanExecuteChanged();
        _jsonValidationCts?.Cancel();
        _jsonValidationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _jsonValidationCts = cts;
        JsonValidationTask = ScheduleJsonValidationAsync(value, cts.Token);
    }

    partial void OnPersistencePathChanged(string value)
    {
        IsPersistenceDirty = !string.Equals(value, _loadedPersistencePath, StringComparison.Ordinal);
        SavePersistenceCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasJsonErrorChanged(bool value) => SaveRawCommand.NotifyCanExecuteChanged();

    private async Task ScheduleJsonValidationAsync(string capturedText, CancellationToken ct)
    {
        try
        {
            await Task.Delay(JsonValidationDebounce, timeProvider, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // PITFALL: stale-guard — cancellation above already covers the common case (a newer edit
        // cancels this token before the delay elapses), but comparing against the live property
        // keeps this correct even if a future caller invokes this method directly.
        if (!string.Equals(capturedText, RawJson, StringComparison.Ordinal))
            return;

        try
        {
            using var document = JsonDocument.Parse(capturedText);
            HasJsonError = false;
            JsonValidationMessage = string.Empty;
        }
        catch (JsonException ex)
        {
            HasJsonError = true;
            JsonValidationMessage = ex.LineNumber is { } line && ex.BytePositionInLine is { } position
                ? $"Line {line + 1}, position {position}: {ex.Message}"
                : ex.Message;
        }
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        RawJson = await configFileStore.ReadAsync(ct);
        var persistence = await configWriter.LoadPersistenceAsync(ct);
        PersistencePath = persistence.SqlitePath;
        _loadedRawJson = RawJson;
        _loadedPersistencePath = PersistencePath;
        IsRawDirty = false;
        IsPersistenceDirty = false;
        SaveRawCommand.NotifyCanExecuteChanged();
        SavePersistenceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSavePersistence))]
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
        if (result.Success)
        {
            _loadedPersistencePath = PersistencePath;
            IsPersistenceDirty = false;
            SavePersistenceCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveRaw))]
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
            _loadedRawJson = RawJson;
            IsRawDirty = false;
            SaveRawCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            StatusIsError = true;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void RevertPersistence()
    {
        PersistencePath = _loadedPersistencePath;
        StatusMessage = "Persistence path reverted.";
    }

    [RelayCommand]
    private void RevertRaw()
    {
        RawJson = _loadedRawJson;
        StatusMessage = "Raw JSON reverted.";
    }

    private bool CanSavePersistence() => IsPersistenceDirty;
    private bool CanSaveRaw() => IsRawDirty && !HasJsonError;
}
