// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class ExecutionSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_fields_from_config_writer()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions
            {
                Backend = ExecutionBackendKind.Container,
                Image = "custom:image",
                Network = ExecutionNetworkKind.Bridge,
                Cpus = 4,
                MemoryMb = 8192,
                User = "2000",
            },
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Container", viewModel.SelectedBackend);
        Assert.Equal("custom:image", viewModel.Image);
        Assert.Equal("Bridge", viewModel.SelectedNetwork);
        Assert.Equal(4, viewModel.Cpus);
        Assert.Equal(8192, viewModel.MemoryMb);
        Assert.Equal("2000", viewModel.User);
        Assert.True(viewModel.IsContainerBackend);
        Assert.False(viewModel.IsHostBackend);
    }

    [Fact]
    public async Task SaveAsync_persists_edited_fields()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ExecutionSettingsViewModel(configWriter)
        {
            SelectedBackend = "Container",
            Image = "img",
            SelectedNetwork = "Bridge",
            Cpus = 1.5,
            MemoryMb = 2048,
            User = "1234",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ExecutionBackendKind.Container, configWriter.SavedExecution!.Backend);
        Assert.Equal("img", configWriter.SavedExecution.Image);
        Assert.Equal(ExecutionNetworkKind.Bridge, configWriter.SavedExecution.Network);
        Assert.Equal(1.5, configWriter.SavedExecution.Cpus);
        Assert.Equal(2048, configWriter.SavedExecution.MemoryMb);
        Assert.Equal("1234", configWriter.SavedExecution.User);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveAsync_surfaces_failed_config_write_result_without_throwing()
    {
        var configWriter = new FakeConfigWriter
        {
            NextSuccess = false,
            NextError = "Execution.Cpus must be > 0 (was 0).",
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Equal("Execution.Cpus must be > 0 (was 0).", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_sets_restart_required_from_config_writer_result()
    {
        // Execution is entirely live (see ExecutionOptions' doc comment), so ConfigWriter always
        // reports RestartRequired: false in production — this exercises the VM's wiring in
        // isolation via the fake, matching the uniform pattern on every other settings page (U5).
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SaveAsync_failed_save_does_not_set_restart_required()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    [Fact]
    public async Task LoadAsync_shows_wildcard_warning_when_global_allowlist_has_wildcard_and_backend_is_host()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Host },
            Permissions = new PermissionsOptions { ShellAutoAllowlist = ["*"] },
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(viewModel.WildcardWarningText));
        Assert.Contains("Container backend", viewModel.WildcardWarningText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_backend_to_host_surfaces_wildcard_warning_before_save()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Container },
            Permissions = new PermissionsOptions { ShellAutoAllowlist = ["*"] },
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, viewModel.WildcardWarningText);

        viewModel.SelectedBackend = "Host";

        Assert.False(string.IsNullOrEmpty(viewModel.WildcardWarningText));
    }

    [Fact]
    public async Task LoadAsync_shows_wildcard_warning_from_a_schedule_overlay_wildcard()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Host },
            Schedules =
            [
                new ScheduleOptions
                {
                    Name = "nightly",
                    Cron = "* * * * *",
                    Prompt = "p",
                    Permissions = new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
                },
            ],
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(viewModel.WildcardWarningText));
    }

    [Fact]
    public async Task LoadAsync_no_warning_when_backend_is_container_even_with_a_wildcard_present()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Container },
            Permissions = new PermissionsOptions { ShellAutoAllowlist = ["*"] },
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.WildcardWarningText);
    }

    [Fact]
    public async Task LoadAsync_no_warning_when_no_wildcard_is_present()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Host },
            Permissions = new PermissionsOptions { ShellAutoAllowlist = ["git status"] },
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.WildcardWarningText);
    }

    [Fact]
    public async Task LoadAsync_treats_a_failed_permissions_read_as_no_warning()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Host },
            ThrowOnLoadPermissions = true,
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.WildcardWarningText);
    }

    [Fact]
    public async Task LoadAsync_treats_a_failed_schedules_read_as_no_warning()
    {
        var configWriter = new FakeConfigWriter
        {
            Execution = new ExecutionOptions { Backend = ExecutionBackendKind.Host },
            ThrowOnLoadSchedules = true,
        };
        var viewModel = new ExecutionSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.WildcardWarningText);
    }
}
