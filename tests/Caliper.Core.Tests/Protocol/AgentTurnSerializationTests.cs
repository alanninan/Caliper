// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Protocol;

namespace Caliper.Core.Tests.Protocol;

/// <summary>
/// Verifies AgentTurn deserialization via the custom AgentTurnConverter.
/// Serialization is deliberately unsupported (AgentTurn is model-produced/read-only).
/// </summary>
public sealed class AgentTurnSerializationTests
{
    // ── Deserialization via converter ──────────────────────────────────────

    [Fact]
    public void Deserialize_respond_turn_from_json()
    {
        const string json = """{"rationale":"thinking","action":"respond","content":"answer"}""";
        var turn = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.AgentTurn);

        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("thinking", rt.Rationale);
        Assert.Equal("answer",   rt.Content);
    }

    [Fact]
    public void Deserialize_call_tool_turn_from_json()
    {
        const string json = """{"rationale":"will search","action":"call_tool","tool":"search","arguments":{"query":"test"}}""";
        var turn = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.AgentTurn);

        var ct = Assert.IsType<CallToolTurn>(turn);
        Assert.Equal("will search", ct.Rationale);
        Assert.Equal("search",      ct.Tool);
        Assert.Equal("test",        ct.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void Deserialize_load_skill_turn_from_json()
    {
        const string json = """{"rationale":"need skill","action":"load_skill","skill":"pdf-processing"}""";
        var turn = JsonSerializer.Deserialize(json, CaliperJsonContext.Default.AgentTurn);

        var ls = Assert.IsType<LoadSkillTurn>(turn);
        Assert.Equal("need skill",     ls.Rationale);
        Assert.Equal("pdf-processing", ls.Skill);
    }

    [Fact]
    public void Deserialize_respond_from_constrained_json()
    {
        // Simulates model output: "rationale" precedes the "action" discriminator.
        // The AgentTurnConverter buffers the full object, so field order doesn't matter.
        const string json = """{"rationale":"I know this.","action":"respond","content":"42"}""";
        var parser = new StreamingEnvelopeParser();
        parser.Push(json.AsSpan());
        var turn = parser.Complete();

        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("42", rt.Content);
    }

    [Fact]
    public void Deserialize_unknown_action_throws_JsonException()
    {
        const string json = """{"rationale":"r","action":"explode","content":"x"}""";
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(json, CaliperJsonContext.Default.AgentTurn));
    }

    [Fact]
    public void Serialize_AgentTurn_throws_NotSupportedException()
    {
        // AgentTurn is model-produced only. Serialization is explicitly unsupported.
        AgentTurn turn = new RespondTurn { Rationale = "r", Content = "c" };
        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(turn, CaliperJsonContext.Default.AgentTurn));
    }
}
