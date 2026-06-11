// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Console.Commands;
using Caliper.Console.Rendering;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Agents;

namespace Caliper.Console.Tests;

public sealed class RenderingTests
{
    [Fact]
    public void Markdown_blocks_cover_common_constructs_and_escape_markup()
    {
        var blocks = MarkdownRenderer.RenderBlocks("""
            # Heading [red]

            - item `code`
            > quoted [blue]

            ```csharp
            public class Demo { }
            ```
            """);

        Assert.Contains(blocks, block => block.Kind == "heading" && block.Markup.Contains("[[red]]", StringComparison.Ordinal));
        Assert.Contains(blocks, block => block.Kind == "list" && block.Markup.Contains("[black on grey85]code[/]", StringComparison.Ordinal));
        Assert.Contains(blocks, block => block.Kind == "quote" && block.Markup.Contains("[[blue]]", StringComparison.Ordinal));
        Assert.Contains(blocks, block => block.Kind == "code" && block.Language == "csharp");
    }

    [Fact]
    public void Code_highlighter_marks_known_tokens_and_plain_unknown_language()
    {
        var highlighted = CodeHighlighter.Highlight("public class Demo { string s = \"x\"; // note }", "csharp");
        Assert.Contains("[deepskyblue1]public[/]", highlighted, StringComparison.Ordinal);
        Assert.Contains("[olive]\"x\"[/]", highlighted, StringComparison.Ordinal);
        Assert.Contains("[grey]// note }[/]", highlighted, StringComparison.Ordinal);

        Assert.Equal("public class Demo", CodeHighlighter.Highlight("public class Demo", "unknown"));
    }

    [Fact]
    public void Footer_formats_runtime_status()
    {
        var settings = new FakeRuntimeSettings();
        var footer = new StatusFooter(settings, new FakeMcpHub());

        var text = footer.Format(new UsageInfo(10, 5, 15), compacted: true);

        Assert.Contains("model/a", text, StringComparison.Ordinal);
        Assert.Contains("Auto", text, StringComparison.Ordinal);
        Assert.Contains("usage 10/5", text, StringComparison.Ordinal);
        Assert.Contains("MCP 1", text, StringComparison.Ordinal);
        Assert.Contains("compacted", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slash_parser_recognizes_phase_7_commands()
    {
        Assert.Equal(SlashCommandKind.Model, SlashCommandParser.Parse("/model openai/gpt").Kind);
        Assert.Equal("Auto", SlashCommandParser.Parse("/permissions Auto").Argument);
        Assert.Equal(SlashCommandKind.Models, SlashCommandParser.Parse("/models").Kind);
        Assert.Equal(SlashCommandKind.Help, SlashCommandParser.Parse("/help").Kind);
        Assert.Equal(SlashCommandKind.Unknown, SlashCommandParser.Parse("/missing").Kind);
    }
}

file sealed class FakeRuntimeSettings : IRuntimeSettings
{
    public CaliperOptions Caliper { get; } = new() { Model = "model/a" };
    public PermissionsOptions Permissions { get; } = new() { Mode = PermissionMode.Auto };

    public void SetModel(string model) => throw new NotSupportedException();
    public void SetPermissionMode(PermissionMode mode) => throw new NotSupportedException();
    public bool TrySet(string key, string value, out string message) => throw new NotSupportedException();
}

file sealed class FakeMcpHub : IMcpHub
{
    public Task ConnectAllAsync(CancellationToken ct) => Task.CompletedTask;
    public IReadOnlyList<ITool> Tools => [];
    public IReadOnlyList<McpServerStatus> Status => [new("local", true, 2, null), new("down", false, 0, "offline")];
    public Task DisposeAllAsync() => Task.CompletedTask;
}
