// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class AgentBehaviorSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_fields_from_config_writer()
    {
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { MaxSteps = 42, Temperature = 0.5 } };
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(42, viewModel.MaxSteps);
        Assert.Equal(0.5, viewModel.Temperature);
    }

    [Fact]
    public async Task SaveAsync_parses_seed_and_persists()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter)
        {
            MaxSteps = 10,
            SeedText = "123",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(10, configWriter.SavedCaliper!.MaxSteps);
        Assert.Equal(123, configWriter.SavedCaliper.Seed);
        Assert.False(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveAsync_invalid_seed_fails_without_saving()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter) { SeedText = "not-a-number" };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Null(configWriter.SavedCaliper);
    }

    [Fact]
    public async Task SaveAsync_sets_restart_required_from_config_writer_result()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter) { MaxSteps = 10 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SaveAsync_failed_save_does_not_set_restart_required()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter) { MaxSteps = 10 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveAsync_blank_seed_clears_it()
    {
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { Seed = 7 } };
        var viewModel = new AgentBehaviorSettingsViewModel(configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SeedText = string.Empty;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(configWriter.SavedCaliper!.Seed);
    }
}
