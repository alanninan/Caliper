// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.RegularExpressions;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Skills;

internal sealed partial class SkillStore(
    IOptions<CaliperOptions> opts,
    ILogger<SkillStore> logger) : ISkillStore
{
    private const int MaxBodyWarningChars = 20_000;

    private readonly object _gate = new();
    private Dictionary<string, SkillEntry>? _skills;

    public IReadOnlyList<SkillMetadata> List()
    {
        EnsureScanned();
        return _skills!.Values.Select(entry => entry.Metadata).ToList();
    }

    public async Task<string> LoadBodyAsync(string name, CancellationToken ct)
    {
        EnsureScanned();

        if (!_skills!.TryGetValue(name, out var entry))
            throw new InvalidOperationException($"Unknown skill: {name}");

        if (!File.Exists(entry.FilePath))
            throw new InvalidOperationException($"Skill file no longer exists: {entry.FilePath}");

        var text = await File.ReadAllTextAsync(entry.FilePath, ct).ConfigureAwait(false);
        var body = SplitSkillFile(text, entry.FilePath) is { } parts
            ? parts.Body.TrimStart()
            : string.Empty;

        if (body.Length > MaxBodyWarningChars)
        {
            logger.LogWarning(
                "Skill '{Skill}' body is {Length} characters; oversized skill bodies are loaded but may crowd the context window.",
                name,
                body.Length);
        }

        return body;
    }

    private void EnsureScanned()
    {
        if (_skills is not null)
            return;

        lock (_gate)
        {
            if (_skills is not null)
                return;

            _skills = Scan();
        }
    }

    private Dictionary<string, SkillEntry> Scan()
    {
        var root = ResolveSkillsDirectory(opts.Value.SkillsDirectory);
        var skills = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);

        if (!Directory.Exists(root))
            return skills;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var skillFile = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillFile))
                continue;

            var text = File.ReadAllText(skillFile);
            var parts = SplitSkillFile(text, skillFile);
            if (parts is null)
                continue;

            SkillFrontmatter? frontmatter;
            try
            {
                frontmatter = ParseFrontmatter(parts.Value.Yaml);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Skipping skill at '{SkillFile}': invalid frontmatter: {Message}", skillFile, ex.Message);
                continue;
            }

            var directoryName = Path.GetFileName(directory);
            if (!TryValidate(skillFile, directoryName, frontmatter, out var metadata))
                continue;

            skills[metadata.Name] = new SkillEntry(metadata, skillFile);
        }

        return skills;
    }

    private bool TryValidate(
        string skillFile,
        string directoryName,
        SkillFrontmatter? frontmatter,
        out SkillMetadata metadata)
    {
        metadata = new SkillMetadata(string.Empty, string.Empty);

        if (frontmatter is null)
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': missing frontmatter.", skillFile);
            return false;
        }

        var name = frontmatter.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': name is required.", skillFile);
            return false;
        }

        if (name.Length > 64)
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': name '{SkillName}' is longer than 64 characters.", skillFile, name);
            return false;
        }

        if (!SkillNameRegex().IsMatch(name))
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': name '{SkillName}' is not a valid skill id.", skillFile, name);
            return false;
        }

        if (!string.Equals(name, directoryName, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Skipping skill at '{SkillFile}': name '{SkillName}' does not match directory '{DirectoryName}'.",
                skillFile,
                name,
                directoryName);
            return false;
        }

        var description = frontmatter.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': description is required.", skillFile);
            return false;
        }

        if (description.Length > 1024)
        {
            logger.LogWarning(
                "Skipping skill at '{SkillFile}': description for '{SkillName}' is longer than 1024 characters.",
                skillFile,
                name);
            return false;
        }

        if (frontmatter.Compatibility?.Length > 500)
        {
            logger.LogWarning(
                "Skipping skill at '{SkillFile}': compatibility for '{SkillName}' is longer than 500 characters.",
                skillFile,
                name);
            return false;
        }

        metadata = new SkillMetadata(name, description);
        return true;
    }

    private static string ResolveSkillsDirectory(string configuredPath) =>
        CaliperHome.ResolveStatePath(configuredPath);

    private static SkillFrontmatter ParseFrontmatter(string yaml)
    {
        string? name = null;
        string? description = null;
        string? compatibility = null;

        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = TrimScalar(line[(separator + 1)..].Trim());
            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "compatibility":
                    compatibility = value;
                    break;
            }
        }

        return new SkillFrontmatter
        {
            Name = name,
            Description = description,
            Compatibility = compatibility,
        };
    }

    private static string TrimScalar(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private SkillFileParts? SplitSkillFile(string text, string skillFile)
    {
        using var reader = new StringReader(text);
        var first = reader.ReadLine();
        if (!string.Equals(first, "---", StringComparison.Ordinal))
        {
            logger.LogWarning("Skipping skill at '{SkillFile}': SKILL.md must start with YAML frontmatter.", skillFile);
            return null;
        }

        var yaml = new StringWriter();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                var body = reader.ReadToEnd();
                return new SkillFileParts(yaml.ToString(), body);
            }

            yaml.WriteLine(line);
        }

        logger.LogWarning("Skipping skill at '{SkillFile}': SKILL.md frontmatter is not closed.", skillFile);
        return null;
    }

    [GeneratedRegex("^(?:[a-z0-9]|[a-z][a-z0-9-]*[a-z0-9])$")]
    private static partial Regex SkillNameRegex();

    private readonly record struct SkillEntry(SkillMetadata Metadata, string FilePath);
    private readonly record struct SkillFileParts(string Yaml, string Body);
}
