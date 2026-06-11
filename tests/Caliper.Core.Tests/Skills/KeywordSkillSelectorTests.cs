// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using Caliper.Core.Skills;

namespace Caliper.Core.Tests.Skills;

public sealed class KeywordSkillSelectorTests
{
    [Fact]
    public async Task Returns_top_matching_skills_sorted_by_score()
    {
        var selector = new KeywordSkillSelector();
        var skills = new[]
        {
            new SkillMetadata("spreadsheet-cleanup", "Clean spreadsheet data and normalize CSV columns."),
            new SkillMetadata("pdf-processing", "Extract PDF text and fill PDF forms."),
            new SkillMetadata("image-editing", "Create bitmap mockups and visual assets."),
        };

        var selected = await selector.SelectAsync(
            "Please clean spreadsheet columns in this CSV data.",
            skills,
            max: 2,
            CancellationToken.None);

        Assert.Equal(["spreadsheet-cleanup"], selected);
    }

    [Fact]
    public async Task Handles_empty_user_message()
    {
        var selector = new KeywordSkillSelector();
        var selected = await selector.SelectAsync(
            "",
            [new SkillMetadata("pdf-processing", "Extract PDF text.")],
            max: 1,
            CancellationToken.None);

        Assert.Empty(selected);
    }

    [Fact]
    public async Task Handles_empty_skill_list()
    {
        var selector = new KeywordSkillSelector();
        var selected = await selector.SelectAsync("extract PDF text", [], max: 1, CancellationToken.None);

        Assert.Empty(selected);
    }

    [Fact]
    public async Task Excludes_zero_overlap_skills()
    {
        var selector = new KeywordSkillSelector();
        var selected = await selector.SelectAsync(
            "extract PDF text",
            [new SkillMetadata("spreadsheet-cleanup", "Clean spreadsheet data.")],
            max: 1,
            CancellationToken.None);

        Assert.Empty(selected);
    }
}
