// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

public sealed partial class SessionItemViewModel(
    SessionSummary summary,
    Func<string, bool> canDelete,
    Action<SessionItemViewModel> requestDelete,
    Func<SessionItemViewModel, string, Task> commitRename) : ObservableObject
{
    public string Id { get; } = summary.Id;
    public string? ParentSessionId { get; } = summary.ParentSessionId;
    public bool IsSubagentRun => ParentSessionId is not null;
    public DateTimeOffset CreatedAt { get; } = summary.CreatedAt;
    public string CreatedText { get; } = summary.CreatedAt.ToLocalTime().ToString("g");
    public string ShortId { get; } = summary.Id[..Math.Min(8, summary.Id.Length)];
    public string ActionsAutomationId => $"SessionActions_{ShortId}";
    public string DeleteAutomationId => $"DeleteSession_{ShortId}";
    public string RenameAutomationId => $"RenameSession_{ShortId}";
    public string DeleteTooltip => IsActiveRun
        ? "Can't delete a session that's currently running"
        : "Delete";

    [ObservableProperty]
    public partial string Title { get; set; } = string.IsNullOrWhiteSpace(summary.Title)
        ? "Untitled session"
        : summary.Title;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActiveRun { get; set; }

    partial void OnIsActiveRunChanged(bool value) => OnPropertyChanged(nameof(DeleteTooltip));

    private bool CanDelete() => canDelete(Id);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => requestDelete(this);

    public void RefreshCanDelete() => DeleteCommand.NotifyCanExecuteChanged();

    public void ApplyTitle(string title) => Title = title;

    public void SetActiveRun(bool value) => IsActiveRun = value;

    [RelayCommand]
    private void BeginRename()
    {
        EditText = Title;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        var trimmed = EditText.Trim();
        IsEditing = false;
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, Title, StringComparison.Ordinal))
            return;

        Title = trimmed;
        await commitRename(this, trimmed);
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;
}
