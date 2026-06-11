// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using ModelContextProtocol.Protocol;

namespace Caliper.Core.Tools.Mcp;

public static class McpClassifier
{
    public static SideEffect Classify(ToolAnnotations? annotations)
    {
        if (annotations?.ReadOnlyHint == true)
            return SideEffect.ReadOnly;

        if (annotations?.ReadOnlyHint == false && annotations.DestructiveHint == false)
            return SideEffect.Write;

        return SideEffect.Execute;
    }
}
