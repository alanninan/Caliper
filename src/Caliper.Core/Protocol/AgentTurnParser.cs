// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;

namespace Caliper.Core.Protocol;

internal static class AgentTurnParser
{
    internal static AgentTurn Parse(string text)
    {
        var json = ExtractJsonObject(text);
        return JsonSerializer.Deserialize(json, CaliperJsonContext.Default.AgentTurn)
            ?? throw new InvalidOperationException("Deserialized AgentTurn was null.");
    }

    internal static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
            return text;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }

        return text[start..];
    }
}
