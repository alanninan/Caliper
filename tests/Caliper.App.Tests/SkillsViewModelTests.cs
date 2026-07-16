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
    public void Refresh_populates_from_skill_store_sorted_by_name()
    {
        var store = new FakeSkillStore(
            new SkillMetadata("zeta", "Zeta skill"),
            new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);

        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(["alpha", "zeta"], viewModel.Skills.Select(s => s.Name));
        Assert.True(viewModel.HasSkills);
        Assert.Equal("2 skills discovered.", viewModel.StatusMessage);
    }

    [Fact]
    public void Refresh_repopulates_after_store_list_changes()
    {
        var store = new FakeSkillStore(new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);
        viewModel.RefreshCommand.Execute(null);
        Assert.Equal(["alpha"], viewModel.Skills.Select(s => s.Name));

        store.SetSkills(new SkillMetadata("alpha", "Alpha skill"), new SkillMetadata("beta", "Beta skill"));
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal(["alpha", "beta"], viewModel.Skills.Select(s => s.Name));
        Assert.Equal("2 skills discovered.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Refresh_reselects_previously_selected_skill_by_name_and_keeps_body()
    {
        var store = new FakeSkillStore(new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);
        viewModel.RefreshCommand.Execute(null);
        await viewModel.SelectSkillAsync(viewModel.Skills[0]);
        Assert.Equal("alpha body", viewModel.SkillBody);

        store.SetSkills(new SkillMetadata("alpha", "Alpha skill"), new SkillMetadata("beta", "Beta skill"));
        viewModel.RefreshCommand.Execute(null);

        Assert.Equal("alpha", viewModel.SelectedSkill?.Name);
        Assert.Equal("alpha body", viewModel.SkillBody);
    }

    [Fact]
    public async Task Refresh_clears_selection_when_previously_selected_skill_is_gone()
    {
        var store = new FakeSkillStore(new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);
        viewModel.RefreshCommand.Execute(null);
        await viewModel.SelectSkillAsync(viewModel.Skills[0]);

        store.SetSkills(new SkillMetadata("beta", "Beta skill"));
        viewModel.RefreshCommand.Execute(null);

        Assert.Null(viewModel.SelectedSkill);
        Assert.Equal("Select a skill to inspect its instructions.", viewModel.SkillBody);
    }

    [Fact]
    public async Task SelectSkillAsync_loads_body_from_store()
    {
        var store = new FakeSkillStore(new SkillMetadata("alpha", "Alpha skill"));
        var viewModel = new SkillsViewModel(store);
        viewModel.RefreshCommand.Execute(null);

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
        private SkillMetadata[] _skills = skills;

        public void SetSkills(params SkillMetadata[] skills) => _skills = skills;

        public IReadOnlyList<SkillMetadata> List() => _skills;

        public Task<string> LoadBodyAsync(string name, CancellationToken ct) => Task.FromResult($"{name} body");
    }
}
