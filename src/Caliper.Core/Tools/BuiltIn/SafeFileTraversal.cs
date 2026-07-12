// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Tools.BuiltIn;

internal static class SafeFileTraversal
{
    private const int MaxDirectories = 10000;

    // Nested build output and VCS metadata are noise for search tools and waste the match budget.
    // Only children are filtered, so an explicit search rooted inside one of these still works.
    private static readonly HashSet<string> s_ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", "bin", "obj", "node_modules", ".idea",
    };

    public static IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        if (File.Exists(root))
        {
            ct.ThrowIfCancellationRequested();
            yield return root;
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var visitedDirectories = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (++visitedDirectories > MaxDirectories)
                throw new InvalidOperationException($"Directory traversal exceeded {MaxDirectories} directories.");

            List<FileSystemInfo> entries;
            try
            {
                // Materialize under the try so an access error surfaces here, not mid-iteration.
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!s_ignoredDirectories.Contains(entry.Name))
                        pending.Push(entry.FullName);
                }
                else
                {
                    yield return entry.FullName;
                }
            }
        }
    }
}
