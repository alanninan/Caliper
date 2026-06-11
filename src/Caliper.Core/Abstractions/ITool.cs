// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Models;
using Caliper.Core.Tools;

namespace Caliper.Core.Abstractions;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    SideEffect SideEffect { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct);
}

public enum SideEffect
{
    ReadOnly,
    Write,
    Execute,
    Network,
}
