// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.ViewModels.Settings;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Tools;
using Microsoft.Extensions.AI;

namespace Caliper.App.Tests.Settings;

public sealed class ToolsSettingsViewModelTests
{
    [Fact]
    public async Task LoadAsync_marks_configured_tools_enabled()
    {
        var registry = new FakeToolRegistry(new StubTool("search"), new StubTool("bash"));
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { EnabledTools = ["search"] } };
        var viewModel = new ToolsSettingsViewModel(registry, configWriter);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.Tools.Single(t => t.Name == "search").IsEnabled);
        Assert.False(viewModel.Tools.Single(t => t.Name == "bash").IsEnabled);
        Assert.Equal("1 of 2 enabled", viewModel.EnabledCountText);
    }

    [Fact]
    public async Task SaveAsync_writes_only_checked_tools()
    {
        var registry = new FakeToolRegistry(new StubTool("search"), new StubTool("bash"));
        var configWriter = new FakeConfigWriter { Caliper = new CaliperOptions { EnabledTools = [] } };
        var viewModel = new ToolsSettingsViewModel(registry, configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.Tools.Single(t => t.Name == "bash").IsEnabled = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["bash"], configWriter.SavedCaliper!.EnabledTools);
    }

    [Fact]
    public async Task SaveAsync_sets_restart_required_from_config_writer_result()
    {
        var registry = new FakeToolRegistry(new StubTool("search"));
        var configWriter = new FakeConfigWriter { NextRestartRequired = true };
        var viewModel = new ToolsSettingsViewModel(registry, configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.RestartRequired);
        Assert.Equal("Saved. Restart Caliper for the enabled tool set to take effect.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_unchanged_tool_set_does_not_claim_restart_required()
    {
        // U5: the enabled-tool set didn't actually change, so ConfigWriter reports
        // RestartRequired: false — the status wording must follow that, not assume a restart is
        // always needed just because this page can, in general, require one.
        var registry = new FakeToolRegistry(new StubTool("search"));
        var configWriter = new FakeConfigWriter { NextRestartRequired = false };
        var viewModel = new ToolsSettingsViewModel(registry, configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.Equal("Saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_failed_save_does_not_set_restart_required()
    {
        var registry = new FakeToolRegistry(new StubTool("search"));
        var configWriter = new FakeConfigWriter { NextRestartRequired = true, NextSuccess = false, NextError = "boom" };
        var viewModel = new ToolsSettingsViewModel(registry, configWriter);
        await viewModel.LoadCommand.ExecuteAsync(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.RestartRequired);
        Assert.True(viewModel.StatusIsError);
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => $"Stub: {name}";
        public SideEffect SideEffect => SideEffect.ReadOnly;
        public JsonElement ParameterSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct) =>
            Task.FromResult(new ToolResult(true, "stub"));
    }

    private sealed class FakeToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Enabled => tools;
        public IReadOnlyList<ITool> All => tools;
        public ITool? Find(string name) => tools.FirstOrDefault(t => t.Name == name);
        public IReadOnlyList<AIFunction> AsAIFunctions() => [];
        public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu) => JsonSerializer.SerializeToElement(new { });
        public string BuildToolMenu() => string.Empty;
    }
}
