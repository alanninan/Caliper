// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.RegularExpressions;

namespace Caliper.Core.Tools.Mcp;

internal static partial class McpToolNaming
{
    public static string Namespaced(string serverName, string toolName)
    {
        var name = $"{Sanitize(serverName)}__{Sanitize(toolName)}";
        return name.Length <= 64 ? name : name[..64].TrimEnd('_', '-');
    }

    private static string Sanitize(string value)
    {
        var normalized = InvalidToolNameChars().Replace(value, "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "mcp" : normalized;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidToolNameChars();
}
