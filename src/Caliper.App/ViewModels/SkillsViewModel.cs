// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    // U9: re-lists from the store (picks up skills added/removed on disk since the page loaded).
    // PITFALL: SelectedSkill/SkillBody must survive a refresh sensibly — if the previously selected
    // skill's name still exists in the new list, re-select it by name and keep the already-loaded
    // body (no re-fetch); otherwise fall back to the unselected placeholder state.
    [RelayCommand]
    private void Refresh()
    {
        var previousName = SelectedSkill?.Name;
        Skills.Clear();
        foreach (var skill in skillStore.List().OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            Skills.Add(new SkillItemViewModel(skill));
        StatusMessage = $"{Skills.Count:N0} skills discovered.";
        OnPropertyChanged(nameof(HasSkills));

        var restored = previousName is null
            ? null
            : Skills.FirstOrDefault(skill => string.Equals(skill.Name, previousName, StringComparison.Ordinal));
        if (restored is not null)
        {
            SelectedSkill = restored;
        }
        else
        {
            SelectedSkill = null;
            SkillBody = "Select a skill to inspect its instructions.";
        }
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
