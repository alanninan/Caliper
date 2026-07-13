// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Protocol;
using Microsoft.Extensions.AI;

namespace Caliper.Core.Tools;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> to restrict the visible/dispatchable tool surface to a
/// fixed set of names for a single run (<c>RunSpec.ToolFilter</c>). A tool outside the filter is
/// treated exactly like an unknown tool: absent from <see cref="Enabled"/>/<see cref="AsAIFunctions"/>
/// and <see cref="Find"/> returns null, so <c>AgentRunner</c>'s dispatch path reports "Unknown
/// tool" without any special-casing.
/// </summary>
internal sealed class FilteredToolRegistry : IToolRegistry
{
    private readonly IToolRegistry _inner;
    private readonly HashSet<string> _allowed;

    public FilteredToolRegistry(IToolRegistry inner, IReadOnlyList<string> allowedNames)
    {
        _inner = inner;
        _allowed = new HashSet<string>(allowedNames, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ITool> Enabled => [.. _inner.Enabled.Where(tool => _allowed.Contains(tool.Name))];

    // Unfiltered: this mirrors ToolRegistry.All's purpose (the host-wide catalog for settings
    // UIs), which is orthogonal to a single run's ToolFilter scoping.
    public IReadOnlyList<ITool> All => _inner.All;

    public ITool? Find(string name) => _allowed.Contains(name) ? _inner.Find(name) : null;

    public IReadOnlyList<AIFunction> AsAIFunctions() =>
        [.. _inner.AsAIFunctions().Where(function => _allowed.Contains(function.Name))];

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu)
    {
        var entries = Enabled.Select(tool => (tool.Name, tool.ParameterSchema)).ToList();
        return ProtocolBuilder.BuildSchema(entries, skillMenu);
    }

    public string BuildToolMenu()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Available tools:");
        foreach (var tool in Enabled)
            builder.AppendLine($"  {tool.Name}: {tool.Description}");
        return builder.ToString();
    }
}
