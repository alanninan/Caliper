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

    /// <summary>
    /// The side effect for a specific invocation. Defaults to <see cref="SideEffect"/>; tools
    /// whose risk depends on their arguments (e.g. a memory tool that both reads and writes) can
    /// narrow it so read-only invocations are not gated as writes.
    /// </summary>
    SideEffect EffectiveSideEffect(JsonElement arguments) => SideEffect;
}

public enum SideEffect
{
    ReadOnly,
    Write,
    Execute,
    Network,
}
