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
    // Absent (defaults to false) in prefs files written before the inspector pane gained a
    // collapse toggle, so old files keep loading with the inspector shown.
    public bool InspectorPaneCollapsed { get; init; }
    // Last user-resized (GridSplitter) or restored width for each pane, in DIPs. Null in prefs
    // files predating this feature (and before the pane has ever been resized), in which case
    // the page falls back to its historical default width.
    public double? SessionsPaneWidth { get; init; }
    public double? InspectorPaneWidth { get; init; }
    public bool ShowSubagentRuns { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
    // Whether the window was maximized at last close. When true, WindowX/Y/Width/Height still hold
    // the last known *restore* (floating) rect, not the maximized rect — see MainWindow_Closed.
    // Absent in older prefs files, which default to false (not maximized) via plain JSON deserialize.
    public bool IsMaximized { get; init; }
    // P2: whether SchedulerHostedService should run inside this app process (AppSchedulerController)
    // for as long as the window stays open. Host-local behavior — belongs here, not in config.json —
    // since headless scheduling (`--serve`) is a separate, unrelated on/off switch. Absent in prefs
    // files predating this feature, which default to false (opt-in, scheduler stays off).
    public bool RunSchedulerInApp { get; init; }
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
    private readonly string _preferencesPath;

    public AppPreferencesStore()
        : this(Path.Combine(CaliperHome.Resolve(), "app-ui.json"))
    {
    }

    // Test seam: the tests compile this file as linked source, so they can round-trip against a
    // temp file instead of the user's real ~/.caliper/app-ui.json.
    internal AppPreferencesStore(string preferencesPath) => _preferencesPath = preferencesPath;

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
                return new AppPreferences();

            var json = File.ReadAllText(_preferencesPath);
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
            File.WriteAllText(_preferencesPath, JsonSerializer.Serialize(preferences));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed preferences write is non-fatal (they're presentation-only) but shouldn't be
            // completely silent, or a persistently unwritable file goes unnoticed in diagnosis.
            System.Diagnostics.Debug.WriteLine($"Failed to save app preferences: {ex.Message}");
        }
    }
}
