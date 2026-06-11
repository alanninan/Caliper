// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Tools.Mcp;
using ModelContextProtocol.Protocol;

namespace Caliper.Core.Tests.Tools.Mcp;

public sealed class McpClassifierTests
{
    [Fact]
    public void Readonly_hint_maps_to_readonly()
    {
        var effect = McpClassifier.Classify(new ToolAnnotations { ReadOnlyHint = true });

        Assert.Equal(SideEffect.ReadOnly, effect);
    }

    [Fact]
    public void Non_readonly_non_destructive_maps_to_write()
    {
        var effect = McpClassifier.Classify(new ToolAnnotations
        {
            ReadOnlyHint = false,
            DestructiveHint = false,
        });

        Assert.Equal(SideEffect.Write, effect);
    }

    [Fact]
    public void Destructive_or_unknown_maps_to_execute()
    {
        Assert.Equal(SideEffect.Execute, McpClassifier.Classify(null));
        Assert.Equal(SideEffect.Execute, McpClassifier.Classify(new ToolAnnotations()));
        Assert.Equal(SideEffect.Execute, McpClassifier.Classify(new ToolAnnotations
        {
            ReadOnlyHint = false,
            DestructiveHint = true,
        }));
    }
}
