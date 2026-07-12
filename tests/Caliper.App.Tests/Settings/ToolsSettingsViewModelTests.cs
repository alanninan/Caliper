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
