// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Caliper.App.ViewModels;

public sealed partial class SkillsViewModel(ISkillStore skillStore) : ObservableObject
{
    public ObservableCollection<SkillItemViewModel> Skills { get; } = [];

    [ObservableProperty]
    public partial SkillItemViewModel? SelectedSkill { get; set; }

    [ObservableProperty]
    public partial string SkillBody { get; set; } = "Select a skill to inspect its instructions.";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasSkills => Skills.Count > 0;

    public void LoadSkills()
    {
        Skills.Clear();
        foreach (var skill in skillStore.List().OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            Skills.Add(new SkillItemViewModel(skill));
        StatusMessage = $"{Skills.Count:N0} skills discovered.";
        OnPropertyChanged(nameof(HasSkills));
    }

    public async Task SelectSkillAsync(SkillItemViewModel? skill)
    {
        SelectedSkill = skill;
        SkillBody = skill is null
            ? "Select a skill to inspect its instructions."
            : await skillStore.LoadBodyAsync(skill.Name, CancellationToken.None);
    }
}

public sealed class SkillItemViewModel(SkillMetadata metadata)
{
    public string Name { get; } = metadata.Name;
    public string Description { get; } = metadata.Description;
}
