// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Skills;

public sealed class SkillStoreTests
{
    [Fact]
    public void Valid_frontmatter_is_listed()
    {
        var root = CreateRoot();
        WriteSkill(root, "pdf-processing", "pdf-processing", "Extract PDF text.");

        var skills = Build(root).List();

        var skill = Assert.Single(skills);
        Assert.Equal("pdf-processing", skill.Name);
        Assert.Equal("Extract PDF text.", skill.Description);
    }

    [Fact]
    public void Name_mismatch_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(root, "pdf-processing", "other-skill", "Extract PDF text.");

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Missing_description_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(root, "pdf-processing", "pdf-processing", null);

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Uppercase_name_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(root, "PDF-processing", "PDF-processing", "Extract PDF text.");

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Leading_hyphen_name_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(root, "-pdf", "-pdf", "Extract PDF text.");

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Name_longer_than_64_chars_is_skipped()
    {
        var root = CreateRoot();
        var name = new string('a', 65);
        WriteSkill(root, name, name, "Extract PDF text.");

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Description_longer_than_1024_chars_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(root, "pdf-processing", "pdf-processing", new string('d', 1025));

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public void Compatibility_longer_than_500_chars_is_skipped()
    {
        var root = CreateRoot();
        WriteSkill(
            root,
            "pdf-processing",
            "pdf-processing",
            "Extract PDF text.",
            compatibility: new string('c', 501));

        Assert.Empty(Build(root).List());
    }

    [Fact]
    public async Task LoadBodyAsync_returns_markdown_body()
    {
        var root = CreateRoot();
        WriteSkill(root, "pdf-processing", "pdf-processing", "Extract PDF text.", "# PDF Processing\nUse OCR.");

        var body = await Build(root).LoadBodyAsync("pdf-processing", CancellationToken.None);

        Assert.Equal("# PDF Processing\nUse OCR.", body);
    }

    [Fact]
    public async Task LoadBodyAsync_throws_when_file_deleted_after_scan()
    {
        var root = CreateRoot();
        var skillFile = WriteSkill(root, "pdf-processing", "pdf-processing", "Extract PDF text.");
        var store = Build(root);
        Assert.Single(store.List());
        File.Delete(skillFile);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.LoadBodyAsync("pdf-processing", CancellationToken.None));
    }

    [Fact]
    public async Task LoadBodyAsync_unknown_skill_throws()
    {
        var root = CreateRoot();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(root).LoadBodyAsync("missing", CancellationToken.None));
    }

    [Fact]
    public void Missing_skills_directory_returns_empty_list()
    {
        var root = Path.Combine(Path.GetTempPath(), "caliper-skills-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(Build(root).List());
    }

    private static SkillStore Build(string root) =>
        new(
            Options.Create(new AgentOptions { SkillsDirectory = root }),
            NullLogger<SkillStore>.Instance);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "caliper-skills-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSkill(
        string root,
        string directoryName,
        string name,
        string? description,
        string body = "# Skill\nInstructions.",
        string? compatibility = null)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);

        var lines = new List<string>
        {
            "---",
            $"name: {name}",
        };

        if (description is not null)
            lines.Add($"description: {description}");

        if (compatibility is not null)
            lines.Add($"compatibility: {compatibility}");

        lines.Add("---");
        lines.Add("");
        lines.Add(body);

        var path = Path.Combine(directory, "SKILL.md");
        File.WriteAllText(path, string.Join('\n', lines));
        return path;
    }
}
