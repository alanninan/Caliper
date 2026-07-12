// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Configuration;

namespace Caliper.Core.Permissions;

public sealed class FileAccessPolicy
{
    // Windows/macOS default file systems are case-insensitive; Linux is case-sensitive. Using the
    // wrong comparison lets a differently-cased path outside the root read as inside (or vice versa).
    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public FileAccessPolicy(CaliperOptions caliperOptions, PermissionsOptions permissionsOptions)
    {
        WorkingRoot = ResolveRoot(caliperOptions.WorkingRoot);
        AutoAllowRoots = permissionsOptions.AutoAllowFileRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(ResolveRoot)
            .Distinct(s_pathComparer)
            .ToList();
    }

    public string WorkingRoot { get; }
    public IReadOnlyList<string> AutoAllowRoots { get; }

    public string ResolvePath(string requestedPath)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath) ? "." : requestedPath;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingRoot, path));
    }

    public bool IsInsideWorkingRoot(string fullPath) =>
        IsInsideRoot(fullPath, WorkingRoot);

    public bool IsInsideAutoAllowRoot(string fullPath) =>
        AutoAllowRoots.Any(root => IsInsideRoot(fullPath, root));

    public bool RequiresPermission(string tool, JsonElement arguments)
    {
        if (!TryGetRequestedPath(tool, arguments, out var requestedPath))
            return false;

        var fullPath = ResolvePhysicalPath(ResolvePath(requestedPath));
        return !IsInsideWorkingRoot(fullPath) && !IsInsideAutoAllowRoot(fullPath);
    }

    public static bool IsFileTool(string tool) =>
        tool is "read_file" or "list_dir" or "glob" or "grep" or "write_file" or "edit_file";

    public static bool TryGetRequestedPath(string tool, JsonElement arguments, out string requestedPath)
    {
        requestedPath = ".";
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("path", out var pathElement) &&
            pathElement.ValueKind == JsonValueKind.String)
        {
            requestedPath = pathElement.GetString() ?? ".";
        }

        return IsFileTool(tool);
    }

    private static string ResolveRoot(string root)
    {
        if (root.StartsWith("~/", StringComparison.Ordinal) || root.StartsWith("~\\", StringComparison.Ordinal))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root[2..]);

        return EnsureTrailingSeparator(ResolvePhysicalPath(Path.GetFullPath(root)));
    }

    private static bool IsInsideRoot(string fullPath, string root)
    {
        var normalized = EnsureTrailingSeparator(ResolvePhysicalPath(Path.GetFullPath(fullPath)));
        return normalized.StartsWith(root, s_pathComparison);
    }

    private static string ResolvePhysicalPath(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            return ResolveExistingPath(fullPath);

        var missingSegments = new Stack<string>();
        var current = fullPath;
        while (!string.IsNullOrEmpty(current) && !File.Exists(current) && !Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(name))
                missingSegments.Push(name);

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        var resolved = !string.IsNullOrEmpty(current) && (File.Exists(current) || Directory.Exists(current))
            ? ResolveExistingPath(current)
            : fullPath;
        while (missingSegments.Count > 0)
            resolved = Path.Combine(resolved, missingSegments.Pop());

        return Path.GetFullPath(resolved);
    }

    private static string ResolveExistingPath(string path)
    {
        var attributes = File.GetAttributes(path);
        var info = (attributes & FileAttributes.Directory) != 0
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? path);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
