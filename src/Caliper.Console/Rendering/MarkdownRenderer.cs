// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Caliper.Console.Rendering;

public sealed record MarkdownRenderBlock(string Kind, string Markup, string? Language = null);

public sealed class MarkdownRenderer
{
    public static IReadOnlyList<MarkdownRenderBlock> RenderBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<MarkdownRenderBlock>();
        var paragraph = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(blocks, paragraph);
                var language = line[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }

                blocks.Add(new MarkdownRenderBlock("code", string.Join(Environment.NewLine, code), language));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(blocks, paragraph);
                continue;
            }

            if (TryHeading(line, out var heading))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(heading);
                continue;
            }

            if (TryListItem(line, out var listItem))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(listItem);
                continue;
            }

            if (line.TrimStart().StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownRenderBlock("quote", $"[grey]| {Inline(line.TrimStart()[2..])}[/]"));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph(blocks, paragraph);
        return blocks;
    }

    public static IRenderable Render(string markdown)
    {
        var renderables = new List<IRenderable>();
        foreach (var block in RenderBlocks(markdown))
        {
            if (block.Kind == "code")
            {
                var highlighted = CodeHighlighter.Highlight(block.Markup, block.Language);
                var title = string.IsNullOrWhiteSpace(block.Language) ? "code" : block.Language;
                renderables.Add(new Panel(new Markup(highlighted))
                    .Header(Markup.Escape(title))
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Grey));
            }
            else
            {
                renderables.Add(new Markup(block.Markup));
            }
        }

        return new Rows(renderables);
    }

    private static bool TryHeading(string line, out MarkdownRenderBlock block)
    {
        var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
        if (match.Success)
        {
            block = new MarkdownRenderBlock("heading", $"[bold underline]{Inline(match.Groups[2].Value)}[/]");
            return true;
        }

        block = default!;
        return false;
    }

    private static bool TryListItem(string line, out MarkdownRenderBlock block)
    {
        var trimmed = line.TrimStart();
        var match = Regex.Match(trimmed, @"^([-*]|\d+[.])\s+(.+)$");
        if (match.Success)
        {
            block = new MarkdownRenderBlock("list", $"  [cyan]*[/] {Inline(match.Groups[2].Value)}");
            return true;
        }

        block = default!;
        return false;
    }

    private static void FlushParagraph(List<MarkdownRenderBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0)
            return;

        blocks.Add(new MarkdownRenderBlock("paragraph", Inline(string.Join(" ", paragraph))));
        paragraph.Clear();
    }

    private static string Inline(string text)
    {
        var escaped = Markup.Escape(text);
        escaped = Regex.Replace(escaped, @"`([^`]+)`", "[black on grey85]$1[/]");
        escaped = Regex.Replace(escaped, @"\*\*([^*]+)\*\*", "[bold]$1[/]");
        escaped = Regex.Replace(escaped, @"\*([^*]+)\*", "[italic]$1[/]");
        return escaped;
    }
}
