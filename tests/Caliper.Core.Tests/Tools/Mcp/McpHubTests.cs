// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Tools.Mcp;

public sealed class McpHubTests
{
    [Fact]
    public async Task ConnectAll_records_failed_server_without_throwing()
    {
        var options = new McpOptions
        {
            Servers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["bad"] = new() { Type = "unsupported" },
            },
        };
        var hub = new McpHub(
            Options.Create(options),
            Options.Create(new CaliperOptions()),
            NullLoggerFactory.Instance,
            NullLogger<McpHub>.Instance);

        await hub.ConnectAllAsync(CancellationToken.None);

        var status = Assert.Single(hub.Status);
        Assert.False(status.Connected);
        Assert.Equal("bad", status.Name);
        Assert.Contains("Unsupported MCP transport type", status.Error);
        Assert.Empty(hub.Tools);
    }

    [Fact]
    public async Task ConnectAll_raises_StatusChanged()
    {
        var options = new McpOptions
        {
            Servers = new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["bad"] = new() { Type = "unsupported" },
            },
        };
        var hub = new McpHub(
            Options.Create(options),
            Options.Create(new CaliperOptions()),
            NullLoggerFactory.Instance,
            NullLogger<McpHub>.Instance);

        var raised = false;
        hub.StatusChanged += (_, _) => raised = true;

        await hub.ConnectAllAsync(CancellationToken.None);

        Assert.True(raised);
    }

    [Fact]
    public async Task DisposeAll_raises_StatusChanged()
    {
        var hub = new McpHub(
            Options.Create(new McpOptions()),
            Options.Create(new CaliperOptions()),
            NullLoggerFactory.Instance,
            NullLogger<McpHub>.Instance);

        var raised = false;
        hub.StatusChanged += (_, _) => raised = true;

        await hub.DisposeAllAsync();

        Assert.True(raised);
    }
}
