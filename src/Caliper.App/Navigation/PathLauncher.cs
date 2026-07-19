// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
using System.Diagnostics;

namespace Caliper.App.Navigation;

public interface IPathLauncher
{
    bool OpenExisting(string path);
}

public sealed class PathLauncher : IPathLauncher
{
    public bool OpenExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var resolved = Path.GetFullPath(
            path.StartsWith('~')
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('\\', '/'))
                : path);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            return false;

        Process.Start(new ProcessStartInfo(resolved) { UseShellExecute = true });
        return true;
    }
}
