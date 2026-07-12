// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core;

namespace Caliper.App.Preferences;

public enum AppThemePreference { System, Light, Dark }

public sealed record AppPreferences
{
    public AppThemePreference Theme { get; init; } = AppThemePreference.System;
    public bool SessionsPaneCollapsed { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
}

public interface IAppPreferencesStore
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}

/// <summary>
/// App-local UI preferences (theme, window placement) that live outside Caliper.Core's
/// config.json — these are presentation concerns for this specific WinUI host, not runtime
/// settings the engine or other hosts need to know about.
/// </summary>
public sealed class AppPreferencesStore : IAppPreferencesStore
{
    private static readonly string PreferencesPath = Path.Combine(CaliperHome.Resolve(), "app-ui.json");

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
                return new AppPreferences();

            var json = File.ReadAllText(PreferencesPath);
            return JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(preferences));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed preferences write is non-fatal (they're presentation-only) but shouldn't be
            // completely silent, or a persistently unwritable file goes unnoticed in diagnosis.
            System.Diagnostics.Debug.WriteLine($"Failed to save app preferences: {ex.Message}");
        }
    }
}
