// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Protocol;

namespace Caliper.Core.Tests.Protocol;

public sealed class ProtocolBuilderTests
{
    [Fact]
    public void BuildSchema_with_no_tools_or_skills_has_only_respond_branch()
    {
        var schema = ProtocolBuilder.BuildSchema([], []);

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(1, oneOf.GetArrayLength());

        var branch = oneOf[0];
        Assert.Equal("respond", branch.GetProperty("properties").GetProperty("action").GetProperty("const").GetString());
    }

    [Fact]
    public void BuildSchema_with_one_tool_has_respond_and_tool_branches()
    {
        var argSchema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}""")
            .RootElement.Clone();

        var schema = ProtocolBuilder.BuildSchema([("search", argSchema)], []);

        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());

        var toolBranch = oneOf[1];
        Assert.Equal("call_tool", toolBranch.GetProperty("properties").GetProperty("action").GetProperty("const").GetString());
        Assert.Equal("search",    toolBranch.GetProperty("properties").GetProperty("tool").GetProperty("const").GetString());
    }

    [Fact]
    public void BuildSchema_with_skills_adds_load_skill_branch()
    {
        var schema = ProtocolBuilder.BuildSchema([], ["pdf-processing", "code-review"]);

        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());  // respond + load_skill

        var skillBranch = oneOf[1];
        Assert.Equal("load_skill", skillBranch.GetProperty("properties").GetProperty("action").GetProperty("const").GetString());

        var enumArr = skillBranch.GetProperty("properties").GetProperty("skill").GetProperty("enum");
        var names   = enumArr.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("pdf-processing", names);
        Assert.Contains("code-review",    names);
    }

    [Fact]
    public void BuildSchema_empty_skill_list_omits_load_skill_branch()
    {
        var schema = ProtocolBuilder.BuildSchema([], []);
        var oneOf  = schema.GetProperty("oneOf");

        // No load_skill branch when there are no skills.
        Assert.DoesNotContain(oneOf.EnumerateArray(),
            b => b.TryGetProperty("properties", out var p) &&
                 p.TryGetProperty("action", out var a) &&
                 a.TryGetProperty("const", out var c) &&
                 c.GetString() == "load_skill");
    }

    [Fact]
    public void BuildSchema_all_branches_have_required_rationale_field()
    {
        var argSchema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["q"],"properties":{"q":{"type":"string"}}}""").RootElement.Clone();
        var schema    = ProtocolBuilder.BuildSchema([("search", argSchema)], ["skill-a"]);

        foreach (var branch in schema.GetProperty("oneOf").EnumerateArray())
        {
            var required = branch.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("rationale", required);
            Assert.Contains("action",    required);
        }
    }

    [Fact]
    public void BuildSchema_rationale_has_maxLength_400()
    {
        var schema = ProtocolBuilder.BuildSchema([], []);
        var branch = schema.GetProperty("oneOf")[0];
        var rationale = branch.GetProperty("properties").GetProperty("rationale");
        Assert.Equal(ProtocolBuilder.RationaleMaxLength, rationale.GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void BuildSchema_content_has_maxLength_8000()
    {
        var schema = ProtocolBuilder.BuildSchema([], []);
        var branch = schema.GetProperty("oneOf")[0];
        var content = branch.GetProperty("properties").GetProperty("content");
        Assert.Equal(ProtocolBuilder.ContentMaxLength, content.GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void BuildSchema_all_branches_have_additionalProperties_false()
    {
        var schema = ProtocolBuilder.BuildSchema([], ["s"]);
        foreach (var branch in schema.GetProperty("oneOf").EnumerateArray())
            Assert.False(branch.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void BuildToolSchema_has_only_selected_tool_branch()
    {
        var argSchema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}""")
            .RootElement.Clone();

        var schema = ProtocolBuilder.BuildToolSchema("search", argSchema);

        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(1, oneOf.GetArrayLength());
        var branch = oneOf[0];
        Assert.Equal("call_tool", branch.GetProperty("properties").GetProperty("action").GetProperty("const").GetString());
        Assert.Equal("search", branch.GetProperty("properties").GetProperty("tool").GetProperty("const").GetString());
    }

    [Fact]
    public void BuildRespondSchema_has_only_respond_branch()
    {
        var schema = ProtocolBuilder.BuildRespondSchema();

        var oneOf = schema.GetProperty("oneOf");
        Assert.Equal(1, oneOf.GetArrayLength());
        Assert.Equal("respond", oneOf[0].GetProperty("properties").GetProperty("action").GetProperty("const").GetString());
    }

    [Fact]
    public void BuildDecisionSchema_omits_branch_specific_payload_fields()
    {
        var argSchema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"}}}""")
            .RootElement.Clone();

        var schema = ProtocolBuilder.BuildDecisionSchema([("search", argSchema)], ["pdf-processing"]);
        var branches = schema.GetProperty("oneOf").EnumerateArray().ToList();

        Assert.Equal(3, branches.Count);
        Assert.DoesNotContain(branches, branch =>
            branch.GetProperty("properties").TryGetProperty("content", out _) ||
            branch.GetProperty("properties").TryGetProperty("arguments", out _));
    }
}
