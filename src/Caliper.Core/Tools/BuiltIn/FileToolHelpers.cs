// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Permissions;

namespace Caliper.Core.Tools.BuiltIn;

internal static class FileToolHelpers
{
    public static string? GetString(JsonElement args, string name, string? fallback = null) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
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
