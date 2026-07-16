// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Configuration;

namespace Caliper.App.Tests.Settings;

public sealed class ContextMemorySettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_populates_context_and_memory_fields()
    {
        var configWriter = new FakeConfigWriter
        {
            Caliper = new CaliperOptions
            {
                Context = new ContextOptions { KeepRecentTurns = 12 },
                Memory = new MemoryOptions { GlobalDir = "/mem" },
            },
        };
        var viewModel = new ContextMemorySettingsViewModel(configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(12, viewModel.KeepRecentTurns);
        Assert.Equal("/mem", viewModel.MemoryGlobalDir);
    }

    [Fact]
    public async Task SaveAsync_persists_changes()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ContextMemorySettingsViewModel(configWriter)
        {
            CompactAtFraction = 0.75,
            KeepRecentTurns = 5,
            MemoryGlobalDir = "/new-mem",
        };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0.75, configWriter.SavedCaliper!.Context.CompactAtFraction);
        Assert.Equal("/new-mem", configWriter.SavedCaliper.Memory.GlobalDir);
    }

    [Fact]
    public async Task SaveAsync_sets_restart_required_from_config_writer_result()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new ContextMemorySettingsViewModel(configWriter) { CompactAtFraction = 0.5 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
    }

    [Fact]
    public async Task SaveAsync_failed_save_does_not_set_restart_required()
    {
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new ContextMemorySettingsViewModel(configWriter) { CompactAtFraction = 0.5 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    [Fact]
    public async Task SaveAsync_rejects_zero_fraction()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ContextMemorySettingsViewModel(configWriter) { CompactAtFraction = 0 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Null(configWriter.SavedCaliper);
    }

    [Fact]
    public async Task SaveAsync_rejects_fraction_of_one()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ContextMemorySettingsViewModel(configWriter) { CompactAtFraction = 1 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Null(configWriter.SavedCaliper);
    }

    [Fact]
    public async Task SaveAsync_rejects_negative_fraction()
    {
        var configWriter = new FakeConfigWriter();
        var viewModel = new ContextMemorySettingsViewModel(configWriter) { CompactAtFraction = -0.1 };

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.StatusIsError);
        Assert.Null(configWriter.SavedCaliper);
    }
}
