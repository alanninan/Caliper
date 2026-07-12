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
}
