// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Models;

public sealed record ToolResult(bool Success, string Output, FileChange? FileChange = null)
{
    public static ToolResult Denied { get; } = new(false, "Denied by user/policy.");
}

public sealed record FileChange(string Path, string Before, string After, bool Truncated = false)
{
    private const int MaxContentLength = 64 * 1024;

    public static FileChange Capture(string path, string before, string after)
    {
        var truncated = before.Length > MaxContentLength || after.Length > MaxContentLength;
        return new FileChange(
            path,
            Truncate(before),
            Truncate(after),
            truncated);
    }

    private static string Truncate(string content) =>
        content.Length <= MaxContentLength
            ? content
            : content[..MaxContentLength] + "\n[diff content truncated]";
}
