// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class SubagentsSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_globals_and_profiles()
    {
        var configWriter = new FakeConfigWriter
        {
            Subagents = new SubagentsOptions
            {
                MaxDepth = 3,
                MaxChildrenPerRun = 5,
                TimeoutSeconds = 120,
                DefaultProfile = "research",
                Profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["research"] = new SubagentProfileOptions { EnabledTools = ["read_file", "grep"], MaxSteps = 10 },
                },
            },
        };
        var viewModel = new SubagentsSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(3, viewModel.MaxDepth);
        Assert.Equal(5, viewModel.MaxChildrenPerRun);
        Assert.Equal(120, viewModel.TimeoutSeconds);
        Assert.Equal("research", viewModel.DefaultProfile);
        var profile = Assert.Single(viewModel.Profiles);
        Assert.Equal("research", profile.Name);
        Assert.Equal(["read_file", "grep"], profile.EnabledToolsList);
        Assert.Equal(10, profile.MaxSteps);
        Assert.True(viewModel.HasProfiles);
    }

    [Fact]
    public async Task LoadAsync_maps_null_mode_to_inherit_text()
    {
        var configWriter = new FakeConfigWriter
        {
            Subagents = new SubagentsOptions
            {
                DefaultProfile = "worker",
                Profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["worker"] = new SubagentProfileOptions { EnabledTools = ["bash"], Mode = null },
                },
            },
        };
        var viewModel = new SubagentsSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(SubagentProfileItemViewModel.InheritModeText, viewModel.Profiles.Single().ModeText);
    }

    [Fact]
    public async Task LoadAsync_maps_an_explicit_mode_to_its_enum_text()
    {
        var configWriter = new FakeConfigWriter
        {
            Subagents = new SubagentsOptions
            {
                DefaultProfile = "worker",
                Profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["worker"] = new SubagentProfileOptions { EnabledTools = ["bash"], Mode = PermissionMode.Plan },
                },
            },
        };
        var viewModel = new SubagentsSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Planning only", viewModel.Profiles.Single().ModeText);
    }

    [Fact]
    public async Task SaveAsync_maps_inherit_text_back_to_a_null_mode()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var profile = viewModel.Profiles.Single(p => p.Name == "research");
        profile.ModeText = SubagentProfileItemViewModel.InheritModeText;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(configWriter.SavedSubagents!.Profiles["research"].Mode);
    }

    [Fact]
    public async Task SaveAsync_maps_an_explicit_mode_text_to_a_permission_mode()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var profile = viewModel.Profiles.Single(p => p.Name == "research");
        profile.ModeText = "Plan";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(PermissionMode.Plan, configWriter.SavedSubagents!.Profiles["research"].Mode);
    }

    [Fact]
    public async Task SaveAsync_parses_enabled_tools_one_per_line()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var profile = viewModel.Profiles.Single(p => p.Name == "research");
        profile.EnabledToolsText = "read_file\ngrep\n\nglob";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["read_file", "grep", "glob"], configWriter.SavedSubagents!.Profiles["research"].EnabledTools);
    }

    [Fact]
    public async Task SaveAsync_persists_global_limits()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.MaxDepth = 4;
        viewModel.MaxChildrenPerRun = 10;
        viewModel.TimeoutSeconds = 900;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(4, configWriter.SavedSubagents!.MaxDepth);
        Assert.Equal(10, configWriter.SavedSubagents.MaxChildrenPerRun);
        Assert.Equal(900, configWriter.SavedSubagents.TimeoutSeconds);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public async Task Renaming_the_default_profile_keeps_default_profile_in_sync()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var profile = viewModel.Profiles.Single(p => p.Name == "research");
        Assert.Equal("research", viewModel.DefaultProfile);

        profile.Name = "explorer";

        Assert.Equal("explorer", viewModel.DefaultProfile);
        Assert.Contains("explorer", viewModel.ProfileNameOptions);
    }

    [Fact]
    public async Task Renaming_a_non_default_profile_does_not_change_default_profile()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var worker = viewModel.Profiles.Single(p => p.Name == "worker");

        worker.Name = "builder";

        Assert.Equal("research", viewModel.DefaultProfile);
    }

    [Fact]
    public async Task RemoveSelectedProfile_is_blocked_for_the_default_profile()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedProfile = viewModel.Profiles.Single(p => p.Name == "research");

        Assert.False(viewModel.CanRemoveSelected);
        Assert.False(string.IsNullOrEmpty(viewModel.RemoveBlockedReason));

        viewModel.RemoveSelectedProfileCommand.Execute(null);

        Assert.Contains(viewModel.Profiles, p => p.Name == "research");
    }

    [Fact]
    public async Task RemoveSelectedProfile_removes_a_non_default_profile()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedProfile = viewModel.Profiles.Single(p => p.Name == "worker");

        Assert.True(viewModel.CanRemoveSelected);
        viewModel.RemoveSelectedProfileCommand.Execute(null);

        Assert.DoesNotContain(viewModel.Profiles, p => p.Name == "worker");
        Assert.DoesNotContain("worker", viewModel.ProfileNameOptions);
    }

    [Fact]
    public async Task SaveCommand_is_disabled_for_duplicate_profile_names()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var worker = viewModel.Profiles.Single(p => p.Name == "worker");

        worker.Name = "research";

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_is_disabled_for_an_empty_profile_name()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var worker = viewModel.Profiles.Single(p => p.Name == "worker");

        worker.Name = "   ";

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_is_disabled_when_a_profile_has_no_enabled_tools()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        var worker = viewModel.Profiles.Single(p => p.Name == "worker");

        worker.EnabledToolsText = string.Empty;

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_is_disabled_when_default_profile_does_not_exist_among_profiles()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.DefaultProfile = "missing";

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_is_disabled_when_there_are_no_profiles()
    {
        var configWriter = new FakeConfigWriter
        {
            Subagents = new SubagentsOptions
            {
                DefaultProfile = string.Empty,
                Profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase),
            },
        };
        var viewModel = new SubagentsSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasProfiles);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddProfile_adds_its_name_to_the_default_profile_options()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.AddProfileCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedProfile);
        Assert.Contains(viewModel.SelectedProfile!.Name, viewModel.ProfileNameOptions);
    }

    [Fact]
    public async Task SaveAsync_surfaces_failed_config_write_result_without_throwing()
    {
        var configWriter = new FakeConfigWriter
        {
            NextSuccess = false,
            NextError = "Subagent profile 'research' must list at least one tool in EnabledTools.",
        };
        var viewModel = new SubagentsSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Equal("Subagent profile 'research' must list at least one tool in EnabledTools.", viewModel.StatusMessage);
    }
}
