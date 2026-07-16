// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class PermissionsSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_mode_and_lists()
    {
        var configWriter = new FakeConfigWriter
        {
            Permissions = new PermissionsOptions
            {
                Mode = PermissionMode.Auto,
                ShellAutoAllowlist = ["git status"],
            },
        };
        var viewModel = new PermissionsSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(PermissionMode.Auto, viewModel.SelectedPermissionMode);
        Assert.Equal(["git status"], viewModel.ShellAutoAllowlist);
    }

    [Fact]
    public async Task SaveAsync_persists_edited_lists()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new PermissionsSettingsViewModel(configWriter)
        {
            SelectedPermissionMode = PermissionMode.Plan,
        };
        viewModel.ShellDenylist.Add("rm -rf");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(PermissionMode.Plan, configWriter.SavedPermissions!.Mode);
        Assert.Equal(["rm -rf"], configWriter.SavedPermissions.ShellDenylist);
    }

    [Fact]
    public async Task SaveAsync_sets_restart_required_from_config_writer_result()
    {
        // SavePermissionsAsync's whole section is a live seam, so ConfigWriter always reports
        // RestartRequired: false in production — this exercises the VM's wiring in isolation via
        // the fake, matching the uniform pattern on every other settings page (U5).
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new PermissionsSettingsViewModel(configWriter);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SaveAsync_failed_save_does_not_set_restart_required()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new PermissionsSettingsViewModel(configWriter);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }
}
