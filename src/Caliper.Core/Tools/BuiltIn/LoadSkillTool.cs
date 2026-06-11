// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class LoadSkillTool : ITool
{
    private static readonly JsonElement ParameterSchemaValue =
        JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["name"],
              "properties": { "name": { "type": "string", "maxLength": 64 } }
            }
            """).RootElement.Clone();

    public string Name => "load_skill";
    public string Description => "Load a local Caliper skill by name when its instructions are needed for the current task.";
    public JsonElement ParameterSchema => ParameterSchemaValue;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct) =>
        Task.FromResult(new ToolResult(false, "load_skill is handled by the agent runner."));
}
