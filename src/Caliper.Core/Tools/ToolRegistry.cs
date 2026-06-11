// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly IMcpHub? _mcpHub;

    public ToolRegistry(
        IEnumerable<ITool> tools,
        IOptions<CaliperOptions> options,
        ILogger<ToolRegistry> logger,
        IMcpHub? mcpHub = null)
    {
        _mcpHub = mcpHub;
        var enabled = new HashSet<string>(options.Value.EnabledTools, StringComparer.OrdinalIgnoreCase);
        _tools = [];

        foreach (var tool in tools)
        {
            if (!enabled.Contains(tool.Name))
                continue;

            ValidateFlatness(tool, logger);
            _tools[tool.Name] = tool;
        }

        // Warn about tools listed in config but not registered.
        foreach (var name in enabled)
        {
            if (!_tools.ContainsKey(name))
                logger.LogWarning("Tool '{Name}' is listed in EnabledTools but has no registered implementation.", name);
        }
    }

    public IReadOnlyList<ITool> Enabled => [.. AllTools()];

    public ITool? Find(string name) =>
        _tools.GetValueOrDefault(name) ??
        _mcpHub?.Tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<AIFunction> AsAIFunctions() =>
        AllTools().Select(static tool => new ToolDeclarationFunction(tool)).ToList();

    public JsonElement BuildResponseSchema(IReadOnlyList<string> skillMenu)
    {
        var entries = AllTools()
            .Select(t => (t.Name, t.ParameterSchema))
            .ToList();
        return ProtocolBuilder.BuildSchema(entries, skillMenu);
    }

    public string BuildToolMenu()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available tools:");
        foreach (var tool in AllTools())
            sb.AppendLine($"  {tool.Name}: {tool.Description}");
        return sb.ToString();
    }

    private IEnumerable<ITool> AllTools()
    {
        foreach (var tool in _tools.Values)
            yield return tool;

        if (_mcpHub is null)
            yield break;

        var names = new HashSet<string>(_tools.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _mcpHub.Tools)
        {
            if (names.Add(tool.Name))
                yield return tool;
        }
    }

    private static void ValidateFlatness(ITool tool, ILogger logger)
    {
        if (tool.ParameterSchema.ValueKind != JsonValueKind.Object) return;
        if (!tool.ParameterSchema.TryGetProperty("properties", out var props)) return;

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object &&
                prop.Value.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "object")
            {
                logger.LogWarning(
                    "Tool '{Name}' has nested object schema for property '{Prop}'. " +
                    "Nested schemas may degrade GBNF conversion on small models.",
                    tool.Name, prop.Name);
            }
        }
    }

    private sealed class ToolDeclarationFunction(ITool tool) : AIFunction
    {
        public override string Name => tool.Name;
        public override string Description => tool.Description;
        public override JsonElement JsonSchema => tool.ParameterSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            new("Caliper dispatches tools itself.");
    }
}
