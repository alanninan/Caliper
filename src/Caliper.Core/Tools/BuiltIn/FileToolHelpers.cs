// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Text.Json;
using Caliper.Core.Permissions;

namespace Caliper.Core.Tools.BuiltIn;

internal static class FileToolHelpers
{
    /// <summary>
    /// Reads a text file, honoring any UTF-8/UTF-16/UTF-32 byte-order mark, and returns the encoding
    /// that was detected so a subsequent write can round-trip it. When the file has no BOM we fall
    /// back to BOM-less UTF-8 so a plain file does not silently gain a BOM on the way back out.
    /// </summary>
    public static async Task<(string Content, Encoding Encoding)> ReadTextWithEncodingAsync(
        string path, CancellationToken ct)
    {
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        // CurrentEncoding is only meaningful once at least one read has happened.
        return (content, reader.CurrentEncoding);
    }

    public static string? GetString(JsonElement args, string name, string? fallback = null) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : fallback;

    public static bool GetBool(JsonElement args, string name, bool fallback = false) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public static int? GetInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    public static string ResolvePath(string requestedPath, ToolContext ctx)
    {
        var policy = new FileAccessPolicy(
            new Configuration.CaliperOptions { WorkingRoot = ctx.WorkingRoot },
            new Configuration.PermissionsOptions());
        var fullPath = policy.ResolvePath(requestedPath);
        if (!ctx.AllowOutsideWorkingRoot && !policy.IsInsideWorkingRoot(fullPath))
            throw new InvalidOperationException($"Path is outside the working root: {fullPath}");

        return fullPath;
    }
}
