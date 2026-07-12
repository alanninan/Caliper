// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core;

public static class CaliperHome
{
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("CALIPER_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".caliper")
            : LocalPath.ResolveHome(configured);

        return Path.GetFullPath(home);
    }

    public static string ConfigPath => Path.Combine(Resolve(), "config.json");
    public static string SkillsPath => Path.Combine(Resolve(), "skills");
    public static string MemoryPath => Path.Combine(Resolve(), "memory");
    public static string LogsPath => Path.Combine(Resolve(), "logs");
    public static string DatabasePath => Path.Combine(Resolve(), "caliper.db");

    public static void EnsureInitialized()
    {
        var home = Resolve();
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(MemoryPath);
        Directory.CreateDirectory(SkillsPath);
        Directory.CreateDirectory(LogsPath);

        if (!File.Exists(ConfigPath))
            File.WriteAllText(ConfigPath, DefaultConfigTemplate);

        SeedSkillsIfEmpty(Path.Combine(AppContext.BaseDirectory, "skills"), SkillsPath);
    }

    public static string ResolveStatePath(string configuredPath)
    {
        if (configuredPath.StartsWith("~/.caliper", StringComparison.Ordinal) ||
            configuredPath.StartsWith("~\\.caliper", StringComparison.Ordinal))
        {
            var suffix = configuredPath["~/.caliper".Length..].TrimStart('/', '\\');
            return Path.GetFullPath(Path.Combine(Resolve(), suffix));
        }

        configuredPath = LocalPath.ResolveHome(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(Resolve(), configuredPath));
    }

    private static void SeedSkillsIfEmpty(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        if (Directory.EnumerateFileSystemEntries(destination).Any())
            return;

        CopyDirectory(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private const string DefaultConfigTemplate = """
        // Caliper config. You can also set CALIPER_OPENROUTER_KEY or CALIPER_GEMINI_KEY instead
        // of storing a key here.
        {
          "Caliper": {
            "Provider": "OpenRouter",
            "Model": "openrouter/model-slug",
            "SkillsDirectory": "~/.caliper/skills",
            "Memory": {
              "GlobalDir": "~/.caliper/memory"
            }
          },
          "Providers": {
            "OpenRouter": {
              "Endpoint": "https://openrouter.ai/api/v1",
              "AppTitle": "Caliper"
            },
            "Gemini": {
              "Endpoint": "https://generativelanguage.googleapis.com/v1beta/openai/"
            }
          },
          "Persistence": {
            "SqlitePath": "~/.caliper/caliper.db"
          },
          "Mcp": {
            "Servers": {}
          }
        }
        """;
}
