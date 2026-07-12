// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Memory;
using Caliper.Core.Models;

namespace Caliper.Core.Tools.BuiltIn;

public sealed class MemoryTool(IMemoryStore memoryStore) : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["action"],
          "properties": {
            "action": { "type": "string", "enum": ["remember", "recall", "forget"] },
            "scope": { "type": "string", "enum": ["project", "global"] },
            "key": { "type": "string" },
            "value": { "type": "string" },
            "query": { "type": "string" }
          }
        }
        """).RootElement.Clone();

    public string Name => "memory";
    public string Description => "Save, recall, or forget durable facts and preferences across sessions.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Write;

    // "recall" only reads; classify it read-only so it is not gated behind a write prompt.
    public SideEffect EffectiveSideEffect(JsonElement arguments) =>
        string.Equals(GetString(arguments, "action"), "recall", StringComparison.OrdinalIgnoreCase)
            ? SideEffect.ReadOnly
            : SideEffect.Write;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        try
        {
            var action = GetString(arguments, "action");
            if (string.IsNullOrWhiteSpace(action))
                return new ToolResult(false, "Missing required argument: action.");

            var scope = ResolveScope(GetString(arguments, "scope"), ctx.WorkingRoot);
            return action switch
            {
                "remember" => await RememberAsync(scope, arguments, ct).ConfigureAwait(false),
                "recall" => await RecallAsync(scope, arguments, ct).ConfigureAwait(false),
                "forget" => await ForgetAsync(scope, arguments, ct).ConfigureAwait(false),
                _ => new ToolResult(false, "Invalid action. Use remember, recall, or forget."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new ToolResult(false, ex.Message);
        }
    }

    private async Task<ToolResult> RememberAsync(string scope, JsonElement arguments, CancellationToken ct)
    {
        var key = GetString(arguments, "key");
        var value = GetString(arguments, "value");
        if (string.IsNullOrWhiteSpace(key))
            return new ToolResult(false, "remember requires key.");
        if (string.IsNullOrWhiteSpace(value))
            return new ToolResult(false, "remember requires value.");

        await memoryStore.RememberAsync(scope, key, value, ct).ConfigureAwait(false);
        return new ToolResult(true, $"Remembered '{key}'.");
    }

    private async Task<ToolResult> RecallAsync(string scope, JsonElement arguments, CancellationToken ct)
    {
        var entries = await memoryStore.RecallAsync(scope, GetString(arguments, "query"), ct).ConfigureAwait(false);
        if (entries.Count == 0)
            return new ToolResult(true, "No memory entries found.");

        var output = new StringBuilder();
        foreach (var entry in entries)
            output.AppendLine($"{ScopeLabel(entry.Scope)}:{entry.Key}: {entry.Value}");
        return new ToolResult(true, output.ToString().Trim());
    }

    private async Task<ToolResult> ForgetAsync(string scope, JsonElement arguments, CancellationToken ct)
    {
        var key = GetString(arguments, "key");
        if (string.IsNullOrWhiteSpace(key))
            return new ToolResult(false, "forget requires key.");

        await memoryStore.ForgetAsync(scope, key, ct).ConfigureAwait(false);
        return new ToolResult(true, $"Forgot '{key}'.");
    }

    private static string ResolveScope(string? requestedScope, string workingRoot) =>
        string.Equals(requestedScope, "global", StringComparison.OrdinalIgnoreCase)
            ? MemoryScope.Global
            : MemoryScope.Project(workingRoot);

    private static string ScopeLabel(string scope) =>
        scope == MemoryScope.Global ? "global" : "project";

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
