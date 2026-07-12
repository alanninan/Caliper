// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Caliper.App.Preferences;
using Caliper.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Caliper.App.ViewModels;

public sealed partial class SessionsViewModel : ObservableObject, IDisposable
{
    private readonly ISessionStore _sessions;
    private readonly IChatSessionController _chat;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly TimeProvider _timeProvider;
    private bool _initialized;

    public SessionsViewModel(
        ISessionStore sessions,
        IChatSessionController chat,
        IAppPreferencesStore preferencesStore,
        TimeProvider timeProvider)
    {
        _sessions = sessions;
        _chat = chat;
        _preferencesStore = preferencesStore;
        _timeProvider = timeProvider;
        _chat.RunActivityChanged += Chat_RunActivityChanged;
        _chat.SessionCreated += Chat_SessionCreated;
        _chat.SessionRenamed += Chat_SessionRenamed;
        Items.CollectionChanged += Items_CollectionChanged;
    }

    public ObservableCollection<SessionItemViewModel> Items { get; } = [];
    public event EventHandler<SessionDeleteRequestedEventArgs>? DeleteRequested;
    public bool IsUpdatingSelection { get; private set; }

    [ObservableProperty]
    public partial SessionItemViewModel? SelectedSession { get; set; }

    [ObservableProperty]
    public partial bool IsPaneCollapsed { get; set; } = false;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<object> FilteredItems { get; set; } = [];

    // True only when an active search matched nothing, so the pane can show a "No sessions match"
    // hint instead of a blank list.
    public bool HasNoMatches => !string.IsNullOrWhiteSpace(SearchText) && FilteredItems.Count == 0;

    partial void OnIsPaneCollapsedChanged(bool value) =>
        _preferencesStore.Save(_preferencesStore.Load() with { SessionsPaneCollapsed = value });

    partial void OnSearchTextChanged(string value) => RebuildFilteredItems();

    partial void OnFilteredItemsChanged(IReadOnlyList<object> value) => OnPropertyChanged(nameof(HasNoMatches));

    [RelayCommand]
    private void TogglePane() => IsPaneCollapsed = !IsPaneCollapsed;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        _initialized = true;
        IsPaneCollapsed = _preferencesStore.Load().SessionsPaneCollapsed;
        var summaries = await _sessions.ListAsync(ct);
        foreach (var summary in summaries)
            Items.Add(CreateItem(summary));

        if (Items.FirstOrDefault() is { } first)
            await SelectProgrammaticallyAsync(first, ct);
    }

    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var summary = await _sessions.CreateWithSummaryAsync("New session", CancellationToken.None);
        var item = CreateItem(summary);
        IsUpdatingSelection = true;
        try
        {
            Items.Insert(0, item);
            await _chat.SelectSessionAsync(item.Id, CancellationToken.None);
            SelectedSession = item;
        }
        finally
        {
            IsUpdatingSelection = false;
        }
    }

    public async Task SelectAsync(SessionItemViewModel item, CancellationToken ct)
    {
        await _chat.SelectSessionAsync(item.Id, ct);
        SelectedSession = item;
    }

    public async Task ConfirmDeleteAsync(SessionItemViewModel item, CancellationToken ct)
    {
        if (!_chat.CanDeleteSession(item.Id))
            return;

        await _sessions.DeleteAsync(item.Id, ct);
        var removedIndex = Items.IndexOf(item);
        Items.Remove(item);
        _chat.RemoveSession(item.Id);

        if (!ReferenceEquals(SelectedSession, item))
            return;

        var next = Items.Count == 0
            ? null
            : Items[Math.Clamp(removedIndex, 0, Items.Count - 1)];
        if (next is null)
        {
            SelectedSession = null;
            _chat.ClearSessionSelection();
        }
        else
            await SelectProgrammaticallyAsync(next, ct);
    }

    private SessionItemViewModel CreateItem(Caliper.Core.Models.SessionSummary summary) =>
        new(summary, _chat.CanDeleteSession, RequestDelete, CommitRenameAsync);

    private void RequestDelete(SessionItemViewModel item) =>
        DeleteRequested?.Invoke(this, new SessionDeleteRequestedEventArgs(item));

    private Task CommitRenameAsync(SessionItemViewModel item, string title) =>
        _sessions.RenameAsync(item.Id, title, CancellationToken.None);

    private void Chat_RunActivityChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
        {
            item.RefreshCanDelete();
            item.SetActiveRun(string.Equals(item.Id, _chat.RunningSessionId, StringComparison.Ordinal));
        }
    }

    private void Chat_SessionCreated(object? sender, SessionCreatedEventArgs e)
    {
        if (Items.Any(item => string.Equals(item.Id, e.Summary.Id, StringComparison.Ordinal)))
            return;

        var item = CreateItem(e.Summary);
        IsUpdatingSelection = true;
        try
        {
            Items.Insert(0, item);
            SelectedSession = item;
        }
        finally
        {
            IsUpdatingSelection = false;
        }
    }

    private void Chat_SessionRenamed(object? sender, SessionRenamedEventArgs e)
    {
        var item = Items.FirstOrDefault(i => string.Equals(i.Id, e.SessionId, StringComparison.Ordinal));
        item?.ApplyTitle(e.Title);
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RebuildFilteredItems();

    private void RebuildFilteredItems()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? Items.AsEnumerable()
            : Items.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

        var today = _timeProvider.GetLocalNow().Date;
        var result = new List<object>();
        string? lastGroup = null;
        foreach (var item in filtered.OrderByDescending(item => item.CreatedAt))
        {
            var group = GroupFor(item.CreatedAt, today);
            if (!string.Equals(group, lastGroup, StringComparison.Ordinal))
            {
                result.Add(group);
                lastGroup = group;
            }

            result.Add(item);
        }

        FilteredItems = result;
    }

    private static string GroupFor(DateTimeOffset createdAt, DateTime today)
    {
        var local = createdAt.ToLocalTime().Date;
        if (local == today)
            return "Today";
        if (local == today.AddDays(-1))
            return "Yesterday";
        if (local > today.AddDays(-7))
            return "This week";
        return "Older";
    }

    private async Task SelectProgrammaticallyAsync(SessionItemViewModel item, CancellationToken ct)
    {
        IsUpdatingSelection = true;
        try
        {
            await _chat.SelectSessionAsync(item.Id, ct);
            SelectedSession = item;
        }
        finally
        {
            IsUpdatingSelection = false;
        }
    }

    public void Dispose()
    {
        _chat.RunActivityChanged -= Chat_RunActivityChanged;
        _chat.SessionCreated -= Chat_SessionCreated;
        _chat.SessionRenamed -= Chat_SessionRenamed;
        Items.CollectionChanged -= Items_CollectionChanged;
    }
}

public sealed class SessionDeleteRequestedEventArgs(SessionItemViewModel session) : EventArgs
{
    public SessionItemViewModel Session { get; } = session;
}
