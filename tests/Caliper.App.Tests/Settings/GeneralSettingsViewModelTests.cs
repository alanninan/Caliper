// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class GeneralSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_working_root_from_config_writer()
    {
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { WorkingRoot = "/repo" } };
        var viewModel = new GeneralSettingsViewModel(configWriter, new FakePreferencesStore(), new TestRuntimeSettings());

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("/repo", viewModel.WorkingRoot);
    }

    [Fact]
    public async Task SaveAsync_persists_working_root_without_touching_other_fields()
    {
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { Model = "keep-me", WorkingRoot = "/old" } };
        var viewModel = new GeneralSettingsViewModel(configWriter, new FakePreferencesStore(), new TestRuntimeSettings())
        {
            WorkingRoot = "/new",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("/new", configWriter.SavedCaliper!.WorkingRoot);
        Assert.Equal("keep-me", configWriter.SavedCaliper.Model);
        Assert.Equal("Saved.", viewModel.StatusMessage);
    }

    [Fact]
    public void SelectedTheme_change_persists_via_preferences_store()
    {
        var preferences = new FakePreferencesStore();
        var viewModel = new GeneralSettingsViewModel(new FakeConfigWriter(), preferences, new TestRuntimeSettings())
        {
            SelectedTheme = AppThemePreference.Dark,
        };

        Assert.Equal(AppThemePreference.Dark, preferences.Saved?.Theme);
    }

    private sealed class FakePreferencesStore : IAppPreferencesStore
    {
        public AppPreferences? Saved { get; private set; }

        public AppPreferences Load() => new();

        public void Save(AppPreferences preferences) => Saved = preferences;
    }
}
