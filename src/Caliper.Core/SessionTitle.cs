// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core;

/// <summary>
/// Derives a short, single-line session title from the first user message. Shared by the console
/// and the WinUI app so both truncate the same way — at a word boundary, not mid-word.
/// </summary>
public static class SessionTitle
{
    private const int MaxLength = 56;

    public static string FromPrompt(string firstMessage)
    {
        var singleLine = firstMessage.ReplaceLineEndings(" ").Trim();
        if (singleLine.Length <= MaxLength)
            return singleLine;

        // Cut at the last space at or before the limit so a word isn't split; fall back to a hard
        // cut if there's no space (a single very long token).
        var window = singleLine[..MaxLength];
        var lastSpace = window.LastIndexOf(' ');
        var trimmed = lastSpace > MaxLength / 2 ? window[..lastSpace] : window;
        return trimmed.TrimEnd() + "…";
    }
}
