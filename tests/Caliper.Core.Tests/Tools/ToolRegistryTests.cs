// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools;

public sealed class ToolRegistryTests
{
    private static ToolRegistry Build(IEnumerable<ITool> tools, CaliperOptions? opts = null, IMcpHub? mcpHub = null)
    {
        opts ??= new CaliperOptions { EnabledTools = ["flat-tool", "nested-tool"] };
        return new ToolRegistry(tools, Options.Create(opts), NullLogger<ToolRegistry>.Instance, mcpHub);
    }

    [Fact]
    public void Enabled_contains_only_tools_listed_in_config()
    {
        var opts = new CaliperOptions { EnabledTools = ["tool-a"] };
        var registry = Build([new StubTool("tool-a"), new StubTool("tool-b")], opts);

        var names = registry.Enabled.Select(t => t.Name).ToList();
        Assert.Contains("tool-a", names);
        Assert.DoesNotContain("tool-b", names);
    }

    [Fact]
    public void All_includes_tools_disabled_by_config()
    {
        var opts = new CaliperOptions { EnabledTools = ["tool-a"] };
        var registry = Build([new StubTool("tool-a"), new StubTool("tool-b")], opts);

        var allNames = registry.All.Select(t => t.Name).ToList();
        Assert.Contains("tool-a", allNames);
        Assert.Contains("tool-b", allNames);
        Assert.DoesNotContain("tool-b", registry.Enabled.Select(t => t.Name));
    }

    [Fact]
    public void Find_returns_registered_tool_by_name()
    {
        var opts     = new CaliperOptions { EnabledTools = ["search"] };
        var registry = Build([new StubTool("search")], opts);

        var found = registry.Find("search");
        Assert.NotNull(found);
        Assert.Equal("search", found.Name);
    }

    [Fact]
    public void Find_returns_null_for_unknown_tool()
    {
        var opts     = new CaliperOptions { EnabledTools = ["search"] };
        var registry = Build([new StubTool("search")], opts);

        Assert.Null(registry.Find("nonexistent"));
    }

    [Fact]
    public void BuildResponseSchema_includes_tool_branch()
    {
        var opts     = new CaliperOptions { EnabledTools = ["search"] };
        var registry = Build([new StubTool("search")], opts);

        var schema = registry.BuildResponseSchema([]);
        var oneOf  = schema.GetProperty("oneOf");

        var hasSearchBranch = oneOf.EnumerateArray()
            .Any(b => b.TryGetProperty("properties", out var p) &&
                      p.TryGetProperty("tool", out var t) &&
                      t.TryGetProperty("const", out var c) &&
                      c.GetString() == "search");
        Assert.True(hasSearchBranch);
    }

    [Fact]
    public void Registry_composes_mcp_tools_and_keeps_builtin_on_collision()
    {
        var builtin = new StubTool("search");
        var mcpTool = new StubMcpTool("server__search");
        var collidingMcpTool = new StubMcpTool("search");
        var opts = new CaliperOptions { EnabledTools = ["search"] };
        var registry = Build([builtin], opts, new StubMcpHub([mcpTool, collidingMcpTool]));

        var names = registry.Enabled.Select(tool => tool.Name).ToList();

        Assert.Equal(builtin, registry.Find("search"));
        Assert.Equal(mcpTool, registry.Find("server__search"));
        Assert.Equal(["search", "server__search"], names);
        Assert.Contains(registry.AsAIFunctions(), function => function.Name == "server__search");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────

file sealed class StubTool(string name) : ITool
{
    public string Name        => name;
    public string Description => $"Stub: {name}";
    public SideEffect SideEffect => SideEffect.ReadOnly;
    public JsonElement ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["q"],"properties":{"q":{"type":"string"}}}""")
            .RootElement.Clone();

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult(true, "stub output"));
}

file sealed class StubMcpTool(string name) : ITool, IMcpTool
{
    public string Name => name;
    public string Description => $"MCP Stub: {name}";
    public SideEffect SideEffect => SideEffect.ReadOnly;
    public JsonElement ParameterSchema =>
        JsonDocument.Parse("""{"type":"object","additionalProperties":true}""")
            .RootElement.Clone();

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult(true, "mcp output"));
}

file sealed class StubMcpHub(IReadOnlyList<ITool> tools) : IMcpHub
{
    public IReadOnlyList<ITool> Tools => tools;
    public IReadOnlyList<McpServerStatus> Status => [];
    public Task ConnectAllAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisposeAllAsync() => Task.CompletedTask;
    public event EventHandler? StatusChanged { add { } remove { } }
}
