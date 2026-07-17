// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;

namespace Caliper.App.Tests;

public sealed class AppPreferencesStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "caliper-tests", Path.GetRandomFileName());

    private AppPreferencesStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new AppPreferencesStore(Path.Combine(_directory, "app-ui.json"));
    }

    [Fact]
    public void Save_then_load_round_trips_is_maximized_with_restore_bounds()
    {
        var store = CreateStore();

        store.Save(new AppPreferences
        {
            WindowX = 10,
            WindowY = 20,
            WindowWidth = 1280,
            WindowHeight = 720,
            IsMaximized = true,
        });
        var loaded = store.Load();

        // The floating restore rect must survive alongside the maximized flag (B6): a maximized
        // close persists IsMaximized=true while keeping the previously saved floating bounds.
        Assert.True(loaded.IsMaximized);
        Assert.Equal(10, loaded.WindowX);
        Assert.Equal(20, loaded.WindowY);
        Assert.Equal(1280, loaded.WindowWidth);
        Assert.Equal(720, loaded.WindowHeight);
    }

    [Fact]
    public void Save_then_load_round_trips_is_maximized_false()
    {
        var store = CreateStore();

        store.Save(new AppPreferences { IsMaximized = false });

        Assert.False(store.Load().IsMaximized);
    }

    [Fact]
    public void Load_prefs_file_without_is_maximized_key_defaults_to_false()
    {
        // A prefs file written before the IsMaximized flag existed must keep loading (backward
        // compatible) and default the flag to false.
        var store = CreateStore();
        File.WriteAllText(
            Path.Combine(_directory, "app-ui.json"),
            """{"Theme":1,"SessionsPaneCollapsed":true,"WindowX":5,"WindowY":6,"WindowWidth":800,"WindowHeight":600}""");

        var loaded = store.Load();

        Assert.False(loaded.IsMaximized);
        Assert.Equal(AppThemePreference.Light, loaded.Theme);
        Assert.True(loaded.SessionsPaneCollapsed);
        Assert.Equal(800, loaded.WindowWidth);
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var store = CreateStore();

        var loaded = store.Load();

        Assert.False(loaded.IsMaximized);
        Assert.Null(loaded.WindowWidth);
        Assert.Equal(AppThemePreference.System, loaded.Theme);
    }

    [Fact]
    public void Save_then_load_round_trips_inspector_pane_collapsed()
    {
        var store = CreateStore();

        store.Save(new AppPreferences { InspectorPaneCollapsed = true });

        Assert.True(store.Load().InspectorPaneCollapsed);
    }

    [Fact]
    public void Save_then_load_round_trips_pane_widths()
    {
        var store = CreateStore();

        store.Save(new AppPreferences { SessionsPaneWidth = 210.5, InspectorPaneWidth = 340 });
        var loaded = store.Load();

        Assert.Equal(210.5, loaded.SessionsPaneWidth);
        Assert.Equal(340, loaded.InspectorPaneWidth);
    }

    [Fact]
    public void Save_then_load_round_trips_run_scheduler_in_app()
    {
        var store = CreateStore();

        store.Save(new AppPreferences { RunSchedulerInApp = true });

        Assert.True(store.Load().RunSchedulerInApp);
    }

    [Fact]
    public void Load_prefs_file_without_run_scheduler_key_defaults_to_false()
    {
        // P2: a prefs file written before the in-app scheduler toggle existed must keep loading
        // (backward compatible) and default the opt-in flag to off.
        var store = CreateStore();
        File.WriteAllText(
            Path.Combine(_directory, "app-ui.json"),
            """{"Theme":1,"SessionsPaneCollapsed":true,"WindowX":5,"WindowY":6,"WindowWidth":800,"WindowHeight":600}""");

        Assert.False(store.Load().RunSchedulerInApp);
    }

    [Fact]
    public void Load_prefs_file_without_inspector_or_width_keys_defaults_to_false_and_null_widths()
    {
        // U1/U2: a prefs file written before the inspector-collapse flag and the pane-width keys
        // existed must keep loading (backward compatible), defaulting the flag to false and both
        // widths to null so ChatPage falls back to its historical default widths.
        var store = CreateStore();
        File.WriteAllText(
            Path.Combine(_directory, "app-ui.json"),
            """{"Theme":1,"SessionsPaneCollapsed":true,"WindowX":5,"WindowY":6,"WindowWidth":800,"WindowHeight":600}""");

        var loaded = store.Load();

        Assert.False(loaded.InspectorPaneCollapsed);
        Assert.Null(loaded.SessionsPaneWidth);
        Assert.Null(loaded.InspectorPaneWidth);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test over it.
        }
    }
}
