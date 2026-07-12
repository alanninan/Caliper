// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.App.Tests;

public sealed class SkillsViewModelTests
{
    [Fact]
    public void LoadSkills_populates_from_skill_store_sorted_by_name()
    {
        var store = new FakeSkillStore(
            new SkillMetadata("zeta", "Zeta skill"),
            new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);

        viewModel.LoadSkills();

        Assert.Equal(["alpha", "zeta"], viewModel.Skills.Select(s => s.Name));
        Assert.True(viewModel.HasSkills);
    }

    [Fact]
    public async Task SelectSkillAsync_loads_body_from_store()
    {
        var store = new FakeSkillStore(new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);
        viewModel.LoadSkills();

        await viewModel.SelectSkillAsync(viewModel.Skills[0]);

        Assert.Equal("alpha body", viewModel.SkillBody);
    }

    [Fact]
    public async Task SelectSkillAsync_null_resets_to_placeholder()
    {
        var viewModel = new SkillsViewModel(new FakeSkillStore());

        await viewModel.SelectSkillAsync(null);

        Assert.Equal("Select a skill to inspect its instructions.", viewModel.SkillBody);
    }

    private sealed class FakeSkillStore(params SkillMetadata[] skills) : ISkillStore
    {
        public IReadOnlyList<SkillMetadata> List() => skills;

        public Task<string> LoadBodyAsync(string name, CancellationToken ct) => Task.FromResult($"{name} body");
    }
}
