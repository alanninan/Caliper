// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Tools.BuiltIn;

internal static class SafeFileTraversal
{
    private const int MaxDirectories = 10000;

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

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry.FullName);
                else
                    yield return entry.FullName;
            }
        }
    }
}
