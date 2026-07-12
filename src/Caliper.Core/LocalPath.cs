// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core;

public static class LocalPath
{
    public static string ResolveHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (path == "~")
            return home;

        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, path[2..]);
        }

        return path;
    }
}
