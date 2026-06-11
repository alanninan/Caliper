// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Tools;

internal static class ToolOutput
{
    private const string TruncatedMarker = ". [truncated]";

    internal static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;

        if (text.Length <= maxChars)
            return text;

        if (maxChars <= TruncatedMarker.Length)
            return text[..maxChars];

        var keep = Math.Max(0, maxChars - TruncatedMarker.Length);
        return text[..keep].TrimEnd() + TruncatedMarker;
    }
}
