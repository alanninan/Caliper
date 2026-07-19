// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels.Settings;

/// <summary>
/// P4: settings surface for <c>Caliper:Subagents</c> (roadmap §3.1) — global limits plus a
/// master-detail editor over <c>SubagentsOptions.Profiles</c>, mirroring
/// <see cref="McpServersSettingsViewModel"/>'s shape (a flat in-memory list edited freely,
/// persisted as a whole by one Save) with add/remove/rename bookkeeping closer to
/// <c>SchedulesViewModel</c>'s (Add/Discard/Save, per-item live validation). The whole section is
/// a live seam — <c>SubagentTool</c> reads it fresh per <c>task</c> call — so saves here never
/// need a restart.
/// </summary>
public sealed partial class SubagentsSettingsViewModel(
    IConfigWriter configWriter,
    IToolRegistry? toolRegistry = null) : ObservableObject
{
    public ObservableCollection<SubagentProfileItemViewModel> Profiles { get; } = [];

    /// <summary>Backing item source for the DefaultProfile ComboBox — kept in sync with <see cref="Profiles"/>' names on every add/remove/rename.</summary>
    public ObservableCollection<string> ProfileNameOptions { get; } = [];

    [ObservableProperty] public partial SubagentProfileItemViewModel? SelectedProfile { get; set; }
    [ObservableProperty] public partial double MaxDepth { get; set; } = 2;
    [ObservableProperty] public partial double MaxChildrenPerRun { get; set; } = 8;
    [ObservableProperty] public partial double TimeoutSeconds { get; set; } = 600;
    [ObservableProperty] public partial string DefaultProfile { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    private string _loadedSnapshot = string.Empty;

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// Which profile object currently backs <see cref="DefaultProfile"/> — tracked by reference
    /// (not name) so a rename of *that* profile can carry <see cref="DefaultProfile"/> along with
    /// it, without a rename of some *other* profile to the old default name hijacking it.
    /// </summary>
    private SubagentProfileItemViewModel? _defaultProfileItem;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        var subagents = await configWriter.LoadSubagentsAsync(ct);
        MaxDepth = subagents.MaxDepth;
        MaxChildrenPerRun = subagents.MaxChildrenPerRun;
        TimeoutSeconds = subagents.TimeoutSeconds;

        Profiles.Clear();
        foreach (var (name, profile) in subagents.Profiles)
            Profiles.Add(SubagentProfileItemViewModel.FromOptions(
                name, profile, OnProfileChanged, toolRegistry?.All.Select(static tool => tool.Name)));

        RefreshProfileNameOptions();
        DefaultProfile = subagents.DefaultProfile;
        _defaultProfileItem = FindProfile(DefaultProfile);
        SelectedProfile = Profiles.FirstOrDefault();

        foreach (var profile in Profiles)
            RecomputeProfile(profile);

        OnPropertyChanged(nameof(HasProfiles));
        SaveCommand.NotifyCanExecuteChanged();
        _loadedSnapshot = CaptureSnapshot();
        IsDirty = false;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new SubagentProfileItemViewModel(
            OnProfileChanged, toolRegistry?.All.Select(static tool => tool.Name))
        {
            Name = NextDefaultName(),
            EnabledToolsText = "read_file",
        };
        profile.InitializeToolChoices();
        Profiles.Add(profile);
        RefreshProfileNameOptions();
        SelectedProfile = profile;
        RecomputeProfile(profile);
        OnPropertyChanged(nameof(HasProfiles));
        SaveCommand.NotifyCanExecuteChanged();
        UpdateDirty();
    }

    /// <summary>Called by the page after the user confirms a delete dialog — never directly from XAML.</summary>
    [RelayCommand]
    private void RemoveSelectedProfile()
    {
        if (SelectedProfile is not { } profile || IsDefaultProfile(profile))
            return;

        Profiles.Remove(profile);
        RefreshProfileNameOptions();
        SelectedProfile = Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
        SaveCommand.NotifyCanExecuteChanged();
        UpdateDirty();
    }

    public bool CanRemoveSelected => SelectedProfile is { } profile && !IsDefaultProfile(profile);

    public bool HasRemoveBlockedReason => SelectedProfile is { } profile && IsDefaultProfile(profile);

    public string RemoveBlockedReason => HasRemoveBlockedReason
        ? "This is the default profile — change \"Default profile\" below before removing it."
        : string.Empty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        RestartRequired = false;
        var options = ToOptions();
        var result = await configWriter.SaveSubagentsAsync(options, CancellationToken.None);
        StatusIsError = !result.Success;
        RestartRequired = result.Success && result.RestartRequired;
        StatusMessage = result.Success
            ? "Saved. Changes apply to the next task tool call."
            : result.Error ?? "Save failed.";
        if (result.Success)
        {
            _loadedSnapshot = CaptureSnapshot();
            IsDirty = false;
        }
    }

    [RelayCommand]
    private async Task DiscardAsync(CancellationToken ct)
    {
        StatusMessage = string.Empty;
        StatusIsError = false;
        await LoadAsync(ct);
    }

    private bool CanSave()
    {
        if (MaxDepth < 1 || MaxChildrenPerRun < 1 || TimeoutSeconds < 1)
            return false;

        if (Profiles.Count == 0)
            return false;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                return false;
            if (!names.Add(profile.Name.Trim()))
                return false;
            if (profile.EnabledToolsList.Count == 0)
                return false;
        }

        return !string.IsNullOrWhiteSpace(DefaultProfile) && names.Contains(DefaultProfile.Trim());
    }

    partial void OnMaxDepthChanged(double value) { SaveCommand.NotifyCanExecuteChanged(); UpdateDirty(); }
    partial void OnMaxChildrenPerRunChanged(double value) { SaveCommand.NotifyCanExecuteChanged(); UpdateDirty(); }
    partial void OnTimeoutSecondsChanged(double value) { SaveCommand.NotifyCanExecuteChanged(); UpdateDirty(); }

    partial void OnDefaultProfileChanged(string value)
    {
        // The DefaultProfile ComboBox pushes a transient null/empty through its TwoWay
        // SelectedItem binding whenever ProfileNameOptions rebuilds (clearing a ComboBox's
        // ItemsSource resets its selection) — keep the last real default item through that so
        // rename-following isn't lost, and never Trim a null.
        if (!string.IsNullOrEmpty(value))
            _defaultProfileItem = FindProfile(value);
        NotifyRemoveGuardChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UpdateDirty();
    }

    partial void OnSelectedProfileChanged(SubagentProfileItemViewModel? value) => NotifyRemoveGuardChanged();

    private void NotifyRemoveGuardChanged()
    {
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(HasRemoveBlockedReason));
        OnPropertyChanged(nameof(RemoveBlockedReason));
    }

    private SubagentsOptions ToOptions()
    {
        var profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
            profiles[profile.Name.Trim()] = profile.ToOptions();

        return new SubagentsOptions
        {
            MaxDepth = (int)MaxDepth,
            MaxChildrenPerRun = (int)MaxChildrenPerRun,
            DefaultProfile = DefaultProfile.Trim(),
            TimeoutSeconds = (int)TimeoutSeconds,
            Profiles = profiles,
        };
    }

    private void OnProfileChanged(SubagentProfileItemViewModel profile)
    {
        // Options must be rebuilt BEFORE DefaultProfile follows a rename: a ComboBox rejects a
        // SelectedItem that isn't in its ItemsSource yet (coercing the selection — and the TwoWay
        // binding — back to null).
        RefreshProfileNameOptions();

        // Keep DefaultProfile following a rename of the profile that IS the default — compared by
        // object identity (_defaultProfileItem), not by name, so renaming a *different* profile to
        // the old default name can't hijack DefaultProfile.
        if (ReferenceEquals(profile, _defaultProfileItem) &&
            !string.Equals(DefaultProfile, profile.Name, StringComparison.Ordinal))
        {
            DefaultProfile = profile.Name;
        }

        foreach (var item in Profiles)
            RecomputeProfile(item);

        NotifyRemoveGuardChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UpdateDirty();
    }

    private void RecomputeProfile(SubagentProfileItemViewModel profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.ValidationMessage = "Name is required.";
        else if (IsDuplicateName(profile))
            profile.ValidationMessage = $"Another profile is already named '{profile.Name.Trim()}'.";
        else if (profile.EnabledToolsList.Count == 0)
            profile.ValidationMessage = "At least one tool is required.";
        else
            profile.ValidationMessage = string.Empty;
    }

    private bool IsDuplicateName(SubagentProfileItemViewModel profile) =>
        Profiles.Count(other => string.Equals(other.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1;

    // string.IsNullOrEmpty guards below: DefaultProfile is declared non-nullable, but the TwoWay
    // ComboBox binding can shove a runtime null through it while ProfileNameOptions rebuilds.
    private bool IsDefaultProfile(SubagentProfileItemViewModel profile) =>
        !string.IsNullOrEmpty(DefaultProfile) &&
        string.Equals(profile.Name.Trim(), DefaultProfile.Trim(), StringComparison.OrdinalIgnoreCase);

    private SubagentProfileItemViewModel? FindProfile(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private void RefreshProfileNameOptions()
    {
        var names = Profiles.Select(static p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.SequenceEqual(ProfileNameOptions, StringComparer.Ordinal))
            return;

        // Clearing the ItemsSource resets the bound ComboBox's selection, and the TwoWay binding
        // pushes that null back into DefaultProfile — capture and restore it around the rebuild so
        // an unrelated edit (tools, steps, another profile's name) can't wipe the default.
        var current = DefaultProfile;
        ProfileNameOptions.Clear();
        foreach (var name in names)
            ProfileNameOptions.Add(name);

        if (!string.IsNullOrEmpty(current) && names.Contains(current, StringComparer.OrdinalIgnoreCase))
            DefaultProfile = current;
    }

    private string NextDefaultName()
    {
        var index = Profiles.Count + 1;
        string candidate;
        do
        {
            candidate = $"profile-{index}";
            index++;
        }
        while (Profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }

    private string CaptureSnapshot()
    {
        var profileText = string.Join("|", Profiles
            .OrderBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => $"{profile.Name}\u001f{profile.EnabledToolsText}\u001f{profile.MaxSteps}\u001f{profile.ModeText}"));
        return $"{MaxDepth}\u001e{MaxChildrenPerRun}\u001e{TimeoutSeconds}\u001e{DefaultProfile}\u001e{profileText}";
    }

    private void UpdateDirty()
    {
        if (!string.IsNullOrEmpty(_loadedSnapshot))
            IsDirty = !string.Equals(CaptureSnapshot(), _loadedSnapshot, StringComparison.Ordinal);
    }
}

/// <summary>
/// One subagent profile's editable form state (list row + detail pane). "(inherit)" in
/// <see cref="ModeText"/> maps to a null <see cref="SubagentProfileOptions.Mode"/> ("don't tighten
/// beyond the parent's own effective mode" per its doc comment). <see cref="EnabledToolsText"/> is
/// one tool name per line — same convention as <c>ScheduleItemViewModel.ShellAutoAllowlistText</c>
/// and <c>McpServerSettingViewModel.ArgsText</c> — rather than inventing a checkbox grid.
/// </summary>
public sealed partial class SubagentProfileItemViewModel(
    Action<SubagentProfileItemViewModel> onChanged,
    IEnumerable<string>? availableToolNames = null) : ObservableObject
{
    public const string InheritModeText = "Inherit parent";
    public const string AskModeText = "Ask before actions";
    public const string AutoModeText = "Allow safe and approved actions";
    public const string PlanModeText = "Planning only";

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string EnabledToolsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ToolSearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial double MaxSteps { get; set; } = 15;
    [ObservableProperty] public partial string ModeText { get; set; } = InheritModeText;
    [ObservableProperty] public partial string ValidationMessage { get; set; } = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public IReadOnlyList<string> EnabledToolsList => ParseLines(EnabledToolsText);
    public ObservableCollection<SubagentToolChoiceViewModel> ToolChoices { get; } = [];
    public ObservableCollection<SubagentToolChoiceViewModel> FilteredToolChoices { get; } = [];

    public void InitializeToolChoices()
    {
        ToolChoices.Clear();
        foreach (var toolName in (availableToolNames ?? EnabledToolsList)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var choice = new SubagentToolChoiceViewModel(toolName)
            {
                IsEnabled = EnabledToolsList.Contains(toolName, StringComparer.OrdinalIgnoreCase),
            };
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SubagentToolChoiceViewModel.IsEnabled))
                    EnabledToolsText = string.Join(Environment.NewLine,
                        ToolChoices.Where(static item => item.IsEnabled).Select(static item => item.Name));
            };
            ToolChoices.Add(choice);
        }
        FilterTools();
    }

    public string SummaryCaption
    {
        get
        {
            var toolCount = EnabledToolsList.Count;
            var steps = (int)MaxSteps;
            return $"{toolCount} tool{(toolCount == 1 ? "" : "s")} · {steps} step{(steps == 1 ? "" : "s")}";
        }
    }

    partial void OnNameChanged(string value) => onChanged(this);

    partial void OnEnabledToolsTextChanged(string value)
    {
        OnPropertyChanged(nameof(SummaryCaption));
        onChanged(this);
    }

    partial void OnToolSearchTextChanged(string value) => FilterTools();

    partial void OnMaxStepsChanged(double value)
    {
        OnPropertyChanged(nameof(SummaryCaption));
        onChanged(this);
    }

    partial void OnModeTextChanged(string value) => onChanged(this);

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));

    public SubagentProfileOptions ToOptions() => new()
    {
        EnabledTools = ParseLines(EnabledToolsText),
        MaxSteps = (int)MaxSteps,
        Mode = string.Equals(ModeText, InheritModeText, StringComparison.Ordinal)
            ? null
            : ModeText switch
            {
                AskModeText => PermissionMode.AskAlways,
                AutoModeText => PermissionMode.Auto,
                PlanModeText => PermissionMode.Plan,
                _ => Enum.TryParse<PermissionMode>(ModeText, out var mode) ? mode : null,
            },
    };

    public static SubagentProfileItemViewModel FromOptions(
        string name, SubagentProfileOptions options, Action<SubagentProfileItemViewModel> onChanged,
        IEnumerable<string>? availableToolNames = null)
    {
        var item = new SubagentProfileItemViewModel(onChanged, availableToolNames)
        {
            Name = name,
            EnabledToolsText = string.Join(Environment.NewLine, options.EnabledTools),
            MaxSteps = options.MaxSteps,
            ModeText = options.Mode switch
            {
                PermissionMode.AskAlways => AskModeText,
                PermissionMode.Auto => AutoModeText,
                PermissionMode.Plan => PlanModeText,
                _ => InheritModeText,
            },
        };
        item.InitializeToolChoices();
        return item;
    }

    private void FilterTools()
    {
        FilteredToolChoices.Clear();
        foreach (var choice in ToolChoices.Where(item =>
                     string.IsNullOrWhiteSpace(ToolSearchText) ||
                     item.Name.Contains(ToolSearchText, StringComparison.OrdinalIgnoreCase)))
            FilteredToolChoices.Add(choice);
    }

    private static string[] ParseLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed partial class SubagentToolChoiceViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}
