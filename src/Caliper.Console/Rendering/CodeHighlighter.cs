// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Caliper.Console.Rendering;

public static class CodeHighlighter
{
    private static readonly Regex s_tokenizer = new(
        """(?<comment>//.*$|#.*$)|(?<string>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HashSet<string> s_csharp = new(StringComparer.Ordinal)
    {
        "await", "break", "case", "catch", "class", "const", "else", "false", "for",
        "foreach", "if", "internal", "namespace", "new", "null", "private", "public",
        "return", "sealed", "static", "switch", "true", "try", "using", "var", "void",
    };

    private static readonly HashSet<string> s_javascript = new(StringComparer.Ordinal)
    {
        "await", "break", "case", "catch", "class", "const", "else", "false", "for",
        "function", "if", "import", "let", "new", "null", "return", "switch", "true",
        "try", "var",
    };

    private static readonly HashSet<string> s_powershell = new(StringComparer.OrdinalIgnoreCase)
    {
        "catch", "else", "false", "foreach", "function", "if", "param", "return",
        "switch", "throw", "true", "try", "while",
    };

    public static string Highlight(string code, string? language)
    {
        var keywords = Keywords(language);
        if (keywords.Count == 0)
            return Markup.Escape(code);

        var cursor = 0;
        var output = new System.Text.StringBuilder();
        foreach (Match match in s_tokenizer.Matches(code))
        {
            output.Append(Markup.Escape(code[cursor..match.Index]));
            var value = match.Value;
            if (match.Groups["comment"].Success)
                output.Append("[grey]").Append(Markup.Escape(value)).Append("[/]");
            else if (match.Groups["string"].Success)
                output.Append("[olive]").Append(Markup.Escape(value)).Append("[/]");
            else if (match.Groups["word"].Success && keywords.Contains(value))
                output.Append("[deepskyblue1]").Append(Markup.Escape(value)).Append("[/]");
            else
                output.Append(Markup.Escape(value));

            cursor = match.Index + match.Length;
        }

        output.Append(Markup.Escape(code[cursor..]));
        return output.ToString();
    }

    private static HashSet<string> Keywords(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "csharp" or "cs" or "dotnet" => s_csharp,
            "javascript" or "js" or "typescript" or "ts" => s_javascript,
            "powershell" or "ps1" or "pwsh" => s_powershell,
            _ => [],
        };
}
