// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;

namespace Caliper.Core.Protocol;

/// <summary>
/// Builds the per-request JSON Schema envelope that Ollama compiles into a GBNF grammar.
/// The schema is a discriminated union keyed on "action". Every string field carries a
/// maxLength cap to bound worst-case token generation and protect the context budget.
/// </summary>
public static class ProtocolBuilder
{
    /// <summary>Reasoning field cap (brief justification, not open-ended CoT).</summary>
    public const int RationaleMaxLength = 400;

    /// <summary>Response content cap.</summary>
    public const int ContentMaxLength = 8000;

    /// <param name="tools">Each entry: (tool name, flat argument schema).</param>
    /// <param name="skillNames">Names of skills currently on the menu; if empty, the load_skill branch is omitted.</param>
    public static JsonElement BuildSchema(
        IReadOnlyList<(string Name, JsonElement ArgumentSchema)> tools,
        IReadOnlyList<string> skillNames)
    {
        using var ms = new System.IO.MemoryStream();
        using var w  = new Utf8JsonWriter(ms);

        w.WriteStartObject();
        w.WriteStartArray("oneOf");

        WriteRespondBranch(w);

        foreach (var (name, argSchema) in tools)
            WriteToolBranch(w, name, argSchema);

        if (skillNames.Count > 0)
            WriteSkillBranch(w, skillNames);

        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();

        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    public static JsonElement BuildRespondSchema() =>
        BuildSingleBranchSchema(WriteRespondBranch);

    public static JsonElement BuildToolSchema(string toolName, JsonElement argumentSchema) =>
        BuildSingleBranchSchema(w => WriteToolBranch(w, toolName, argumentSchema));

    public static JsonElement BuildSkillSchema(string skillName) =>
        BuildSingleBranchSchema(w => WriteSkillBranch(w, [skillName]));

    public static JsonElement BuildDecisionSchema(
        IReadOnlyList<(string Name, JsonElement ArgumentSchema)> tools,
        IReadOnlyList<string> skillNames)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);

        w.WriteStartObject();
        w.WriteStartArray("oneOf");

        WriteRespondDecisionBranch(w);

        foreach (var (name, _) in tools)
            WriteToolDecisionBranch(w, name);

        if (skillNames.Count > 0)
            WriteSkillDecisionBranch(w, skillNames);

        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();

        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    private static JsonElement BuildSingleBranchSchema(Action<Utf8JsonWriter> writeBranch)
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new Utf8JsonWriter(ms);

        w.WriteStartObject();
        w.WriteStartArray("oneOf");
        writeBranch(w);
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();

        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    // (a) respond
    private static void WriteRespondBranch(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteStringValue("content");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "respond");
        WriteStringProp(w, "content", ContentMaxLength);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // (b) call_tool — one branch per tool
    private static void WriteToolBranch(Utf8JsonWriter w, string toolName, JsonElement argSchema)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteStringValue("tool");
        w.WriteStringValue("arguments");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "call_tool");
        WriteConstProp(w, "tool", toolName);
        w.WritePropertyName("arguments");
        argSchema.WriteTo(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // (c) load_skill
    private static void WriteSkillBranch(Utf8JsonWriter w, IReadOnlyList<string> skillNames)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteStringValue("skill");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "load_skill");
        w.WriteStartObject("skill");
        w.WriteStartArray("enum");
        foreach (var name in skillNames)
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteRespondDecisionBranch(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "respond");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteToolDecisionBranch(Utf8JsonWriter w, string toolName)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteStringValue("tool");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "call_tool");
        WriteConstProp(w, "tool", toolName);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteSkillDecisionBranch(Utf8JsonWriter w, IReadOnlyList<string> skillNames)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        w.WriteStringValue("rationale");
        w.WriteStringValue("action");
        w.WriteStringValue("skill");
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteStringProp(w, "rationale", RationaleMaxLength);
        WriteConstProp(w, "action", "load_skill");
        w.WriteStartObject("skill");
        w.WriteStartArray("enum");
        foreach (var name in skillNames)
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteStringProp(Utf8JsonWriter w, string propName, int? maxLength = null)
    {
        w.WriteStartObject(propName);
        w.WriteString("type", "string");
        if (maxLength.HasValue)
            w.WriteNumber("maxLength", maxLength.Value);
        w.WriteEndObject();
    }

    private static void WriteConstProp(Utf8JsonWriter w, string propName, string constValue)
    {
        w.WriteStartObject(propName);
        w.WriteString("const", constValue);
        w.WriteEndObject();
    }
}
