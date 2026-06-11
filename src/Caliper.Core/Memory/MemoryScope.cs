// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Memory;

public static class MemoryScope
{
    public const string Global = "global";

    public static string Project(string workingRoot)
    {
        return $"project:{NormalizeWorkingRoot(workingRoot)}";
    }

    public static string NormalizeWorkingRoot(string workingRoot)
    {
        var fullPath = Path.GetFullPath(LocalPath.ResolveHome(workingRoot));
        if (OperatingSystem.IsWindows())
            fullPath = fullPath.ToUpperInvariant();

        return fullPath;
    }
}
