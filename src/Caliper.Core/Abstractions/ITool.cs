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

    /// <summary>
    /// Overrides the generic <c>CaliperOptions.ToolTimeoutSeconds</c> wrapping applied by
    /// dispatch (<c>AgentRunner.DispatchWithRetry</c>). Null (the default) keeps the generic
    /// timeout. A tool whose own work is expected to run far longer — e.g. a subagent's whole
    /// child run — returns its own budget here instead.
    /// </summary>
    TimeSpan? ToolTimeoutOverride => null;
}

public enum SideEffect
{
    ReadOnly,
    Write,
    Execute,
    Network,
}
