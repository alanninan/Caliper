// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Protocol;

namespace Caliper.Core.Tests.Protocol;

public sealed class StreamingEnvelopeParserTests
{
    // ── Push + delta extraction ─────────────────────────────────────────────

    [Fact]
    public void Push_extracts_rationale_and_content_from_complete_respond_turn()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """{"rationale":"I will answer","action":"respond","content":"Hello!"}""";

        var deltas = parser.Push(json.AsSpan()).ToList();

        var rationale = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));
        var content   = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Content).Select(d => d.Text));

        Assert.Equal("I will answer", rationale);
        Assert.Equal("Hello!", content);
    }

    [Fact]
    public void Push_handles_rationale_only_turn()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """{"rationale":"searching","action":"call_tool","tool":"search","arguments":{"query":"test"}}""";

        var deltas = parser.Push(json.AsSpan()).ToList();

        var rationale = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));
        Assert.Equal("searching", rationale);
        Assert.DoesNotContain(deltas, d => d.Field == EnvelopeField.Content);
    }

    [Fact]
    public void Push_decodes_json_escape_sequences_in_tracked_fields()
    {
        var parser = new StreamingEnvelopeParser();
        // \n, \t, \", \\
        var json = """{"rationale":"line1\nline2\ttab\"quote\\slash","action":"respond","content":"ok"}""";

        var deltas    = parser.Push(json.AsSpan()).ToList();
        var rationale = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));

        Assert.Equal("line1\nline2\ttab\"quote\\slash", rationale);
    }

    [Fact]
    public void Push_decodes_unicode_escape_in_tracked_field()
    {
        var parser = new StreamingEnvelopeParser();
        var json = """{"rationale":"\u0041BC","action":"respond","content":"x"}""";

        var deltas    = parser.Push(json.AsSpan()).ToList();
        var rationale = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));

        Assert.Equal("ABC", rationale);
    }

    [Fact]
    public void Push_handles_incremental_chunks()
    {
        var parser = new StreamingEnvelopeParser();
        // Split a full JSON string into 3 arbitrary chunks to simulate token-by-token streaming.
        const string full = "{\"rationale\":\"hi\",\"action\":\"respond\",\"content\":\"answer\"}";
        var part1 = full[..17];   // {"rationale":"hi
        var part2 = full[17..44]; // ","action":"respond","content":"an
        var part3 = full[44..];   // swer"}

        var allDeltas = new List<EnvelopeDelta>();
        allDeltas.AddRange(parser.Push(part1.AsSpan()));
        allDeltas.AddRange(parser.Push(part2.AsSpan()));
        allDeltas.AddRange(parser.Push(part3.AsSpan()));

        var rationale = string.Concat(allDeltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));
        var content   = string.Concat(allDeltas.Where(d => d.Field == EnvelopeField.Content).Select(d => d.Text));

        Assert.Equal("hi", rationale);
        Assert.Equal("answer", content);
    }

    // ── Complete() ─────────────────────────────────────────────────────────

    [Fact]
    public void Complete_deserializes_respond_turn()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """{"rationale":"done","action":"respond","content":"The answer."}""";
        parser.Push(json.AsSpan());

        var turn = parser.Complete();

        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("done",        rt.Rationale);
        Assert.Equal("The answer.", rt.Content);
    }

    [Fact]
    public void Complete_deserializes_fenced_respond_turn()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """
            ```json
            {"rationale":"done","action":"respond","content":"The answer."}
            ```
            """;
        parser.Push(json.AsSpan());

        var turn = parser.Complete();

        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("done", rt.Rationale);
        Assert.Equal("The answer.", rt.Content);
    }

    [Fact]
    public void Complete_ignores_closing_braces_inside_string_values()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """
            ```json
            {"rationale":"done","action":"respond","content":"Use } inside text."}
            ```
            """;
        parser.Push(json.AsSpan());

        var turn = parser.Complete();

        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("Use } inside text.", rt.Content);
    }

    [Fact]
    public void Complete_deserializes_call_tool_turn()
    {
        var parser = new StreamingEnvelopeParser();
        const string json =
            """{"rationale":"search","action":"call_tool","tool":"search","arguments":{"query":"cats"}}""";
        parser.Push(json.AsSpan());

        var turn = parser.Complete();

        var ct = Assert.IsType<CallToolTurn>(turn);
        Assert.Equal("search", ct.Tool);
        Assert.Equal("cats", ct.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void Complete_throws_on_invalid_json()
    {
        var parser = new StreamingEnvelopeParser();
        parser.Push("{not valid".AsSpan());

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => parser.Complete());
    }

    // ── Reset ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_allows_reuse_for_next_turn()
    {
        var parser = new StreamingEnvelopeParser();
        parser.Push("""{"rationale":"a","action":"respond","content":"b"}""".AsSpan());
        parser.Reset();

        const string json2 = """{"rationale":"x","action":"respond","content":"y"}""";
        parser.Push(json2.AsSpan());
        var turn = (RespondTurn)parser.Complete();

        Assert.Equal("x", turn.Rationale);
        Assert.Equal("y", turn.Content);
    }

    // ── Edge cases (partial SPEC §6.3 fuzz suite) ──────────────────────────

    [Fact]
    public void Push_handles_escape_split_across_chunks()
    {
        // Backslash in one chunk, 'n' in the next — must decode to newline.
        var parser = new StreamingEnvelopeParser();
        const string before = """{"rationale":"line1\""";  // ends mid-escape: \
        const string after  = """n","action":"respond","content":"ok"}""";

        var d1 = parser.Push(before.AsSpan()).ToList();
        var d2 = parser.Push(after.AsSpan()).ToList();
        var rationale = string.Concat(d1.Concat(d2).Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));

        Assert.Equal("line1\n", rationale);
    }

    [Fact]
    public void Push_handles_unicode_escape_split_across_chunks()
    {
        // A = 'A'; split after \u00
        var parser = new StreamingEnvelopeParser();
        const string before = """{"rationale":"\u00""";
        const string after  = """41","action":"respond","content":"ok"}""";

        var d1 = parser.Push(before.AsSpan()).ToList();
        var d2 = parser.Push(after.AsSpan()).ToList();
        var rationale = string.Concat(d1.Concat(d2).Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));

        Assert.Equal("A", rationale);
    }

    [Fact]
    public void Push_handles_surrogate_pair_escape_in_tracked_field()
    {
        var parser = new StreamingEnvelopeParser();
        var json = """{"rationale":"ok","action":"respond","content":"Nice \uD83D\uDE00"}""";

        var deltas = parser.Push(json.AsSpan()).ToList();
        var content = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Content).Select(d => d.Text));

        Assert.Equal("Nice " + char.ConvertFromUtf32(0x1F600), content);
    }

    [Fact]
    public void Push_handles_surrogate_pair_escape_split_across_chunks()
    {
        var parser = new StreamingEnvelopeParser();
        const string before = """{"rationale":"\uD83D""";
        const string after = """\uDE00","action":"respond","content":"ok"}""";

        var d1 = parser.Push(before.AsSpan()).ToList();
        var d2 = parser.Push(after.AsSpan()).ToList();
        var rationale = string.Concat(d1.Concat(d2).Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));

        Assert.Equal(char.ConvertFromUtf32(0x1F600), rationale);
    }

    [Fact]
    public void Push_handles_content_containing_action_keyword()
    {
        // Content whose text literally contains the string "action" and braces.
        var parser = new StreamingEnvelopeParser();
        const string json = """{"rationale":"r","action":"respond","content":"use {\"action\":\"respond\"} to reply"}""";
        parser.Push(json.AsSpan());

        var turn = parser.Complete();
        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("use {\"action\":\"respond\"} to reply", rt.Content);
    }

    [Fact]
    public void Complete_throws_on_unknown_action()
    {
        var parser = new StreamingEnvelopeParser();
        parser.Push("""{"rationale":"r","action":"explode","content":"x"}""".AsSpan());

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => parser.Complete());
    }

    [Fact]
    public void Push_handles_empty_rationale()
    {
        var parser = new StreamingEnvelopeParser();
        const string json = """{"rationale":"","action":"respond","content":"hello"}""";
        var deltas = parser.Push(json.AsSpan()).ToList();
        var rationale = string.Concat(deltas.Where(d => d.Field == EnvelopeField.Rationale).Select(d => d.Text));
        Assert.Equal("", rationale);

        var turn = parser.Complete();
        var rt = Assert.IsType<RespondTurn>(turn);
        Assert.Equal("", rt.Rationale);
        Assert.Equal("hello", rt.Content);
    }
}
