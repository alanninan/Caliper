// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Tools.Mcp;

namespace Caliper.Core.Tests.Tools.Mcp;

public sealed class McpToolNamingTests
{
    [Fact]
    public void Namespaced_combines_server_and_tool_with_sanitizing()
    {
        var name = McpToolNaming.Namespaced("file system", "read/path");

        Assert.Equal("file_system__read_path", name);
    }

    [Fact]
    public void Namespaced_truncates_to_tool_name_limit()
    {
        var name = McpToolNaming.Namespaced(new string('s', 48), new string('t', 48));

        Assert.Equal(64, name.Length);
    }
}
