// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Abstractions;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Enabled { get; }

    /// <summary>
    /// Every built-in tool registered with the host, regardless of whether it is currently
    /// enabled via <c>CaliperOptions.EnabledTools</c>. Used by settings UIs that need to offer a
    /// toggle for a disabled tool, not just list the active set. MCP tools are intentionally
    /// excluded — they are connection-dependent and already surfaced via <see cref="IMcpHub"/>.
    /// </summary>
    IReadOnlyList<ITool> All { get; }

    ITool? Find(string name);
    IReadOnlyList<AIFunction> AsAIFunctions();
    JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu);
    string BuildToolMenu();
}
