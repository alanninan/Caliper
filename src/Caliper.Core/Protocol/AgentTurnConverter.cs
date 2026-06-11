// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caliper.Core.Protocol;

/// <summary>
/// Manual dispatch converter for <see cref="AgentTurn"/>. Buffers the full JSON object
/// (<c>JsonDocument.ParseValue</c>), reads <c>rationale</c>, and dispatches on <c>action</c>.
/// This avoids STJ's polymorphic-discriminator ordering requirement — the model always
/// emits <c>rationale</c> before <c>action</c>, which would break the default path.
/// Serialization is intentionally unsupported: <see cref="AgentTurn"/> is model-produced only.
/// </summary>
public sealed class AgentTurnConverter : JsonConverter<AgentTurn>
{
    public override AgentTurn Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc  = JsonDocument.ParseValue(ref reader);
        var root       = doc.RootElement;
        var rationale  = root.GetProperty("rationale").GetString() ?? "";

        return root.GetProperty("action").GetString() switch
        {
            "respond"    => new RespondTurn
                            {
                                Rationale = rationale,
                                Content   = root.GetProperty("content").GetString() ?? "",
                            },
            "call_tool"  => new CallToolTurn
                            {
                                Rationale = rationale,
                                Tool      = root.GetProperty("tool").GetString()!,
                                Arguments = root.GetProperty("arguments").Clone(),
                            },
            "load_skill" => new LoadSkillTurn
                            {
                                Rationale = rationale,
                                Skill     = root.GetProperty("skill").GetString()!,
                            },
            var a => throw new JsonException($"Unknown action discriminator: '{a}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, AgentTurn value, JsonSerializerOptions options) =>
        throw new NotSupportedException("AgentTurn is model-produced and cannot be serialized.");
}
