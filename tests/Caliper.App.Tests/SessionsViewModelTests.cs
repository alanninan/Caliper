// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Context;
using Caliper.Core.Models;

namespace Caliper.App.Tests;

public sealed class SessionsViewModelTests
{
    [Fact]
    public async Task NewSession_creates_selects_and_displays_new_session()
    {
        var store = new FakeSessionStore();
        _ = await store.CreateAsync("existing", CancellationToken.None);
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(store, chat, new FakePreferencesStore(), TimeProvider.System);
        await sessions.InitializeAsync(CancellationToken.None);

        await sessions.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal(2, sessions.Items.Count);
        Assert.Equal("New session", sessions.SelectedSession?.Title);
        Assert.Equal(sessions.SelectedSession?.Id, chat.CurrentSessionId);
    }

    [Fact]
    public async Task ConfirmDelete_running_session_is_preserved()
    {
        var store = new FakeSessionStore();
        _ = await store.CreateAsync("running", CancellationToken.None);
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(store, chat, new FakePreferencesStore(), TimeProvider.System);
        await sessions.InitializeAsync(CancellationToken.None);
        var selected = Assert.IsType<SessionItemViewModel>(sessions.SelectedSession);
        chat.RunningSessionId = selected.Id;

        await sessions.ConfirmDeleteAsync(selected, CancellationToken.None);

        Assert.Single(sessions.Items);
        Assert.Single(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmDelete_selected_session_selects_next_session()
    {
        var store = new FakeSessionStore();
        var firstId = await store.CreateAsync("first", CancellationToken.None);
        var secondId = await store.CreateAsync("second", CancellationToken.None);
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(store, chat, new FakePreferencesStore(), TimeProvider.System);
        await sessions.InitializeAsync(CancellationToken.None);
        Assert.Equal(secondId, sessions.SelectedSession?.Id);

        await sessions.ConfirmDeleteAsync(sessions.SelectedSession!, CancellationToken.None);

        Assert.Single(sessions.Items);
        Assert.Equal(firstId, sessions.SelectedSession?.Id);
        Assert.Equal(firstId, chat.CurrentSessionId);
    }

    [Fact]
    public async Task Chat_session_renamed_updates_matching_item_title()
    {
        var store = new FakeSessionStore();
        _ = await store.CreateAsync("original", CancellationToken.None);
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(store, chat, new FakePreferencesStore(), TimeProvider.System);
        await sessions.InitializeAsync(CancellationToken.None);
        var item = sessions.Items.Single();

        chat.RaiseSessionRenamed(item.Id, "Renamed via chat");

        Assert.Equal("Renamed via chat", item.Title);
    }

    [Fact]
    public async Task Search_text_filters_items_by_title()
    {
        var store = new FakeSessionStore();
        _ = await store.CreateAsync("alpha task", CancellationToken.None);
        _ = await store.CreateAsync("beta task", CancellationToken.None);
        var chat = new FakeChatSessionController();
        var sessions = new SessionsViewModel(store, chat, new FakePreferencesStore(), TimeProvider.System);
        await sessions.InitializeAsync(CancellationToken.None);

        sessions.SearchText = "alpha";

        Assert.Contains(sessions.FilteredItems, item =>
            item is SessionItemViewModel session && session.Title == "alpha task");
        Assert.DoesNotContain(sessions.FilteredItems, item =>
            item is SessionItemViewModel session && session.Title == "beta task");
    }

    [Fact]
    public void TogglePane_flips_and_persists_collapsed_state()
    {
        var store = new FakeSessionStore();
        var chat = new FakeChatSessionController();
        var preferences = new FakePreferencesStore();
        var sessions = new SessionsViewModel(store, chat, preferences, TimeProvider.System);

        sessions.TogglePaneCommand.Execute(null);

        Assert.True(sessions.IsPaneCollapsed);
        Assert.True(preferences.Saved?.SessionsPaneCollapsed);
    }

    private sealed class FakeChatSessionController : IChatSessionController
    {
        public string? CurrentSessionId { get; private set; }
        public string? RunningSessionId { get; set; }
        public event EventHandler? RunActivityChanged;
        public event EventHandler<SessionCreatedEventArgs>? SessionCreated;
        public event EventHandler<SessionRenamedEventArgs>? SessionRenamed;

        public Task SelectSessionAsync(string sessionId, CancellationToken ct)
        {
            CurrentSessionId = sessionId;
            return Task.CompletedTask;
        }

        public bool CanDeleteSession(string sessionId) =>
            !string.Equals(RunningSessionId, sessionId, StringComparison.Ordinal);

        public void RemoveSession(string sessionId)
        {
            if (string.Equals(CurrentSessionId, sessionId, StringComparison.Ordinal))
                CurrentSessionId = null;
        }

        public void ClearSessionSelection() => CurrentSessionId = null;

        public void RaiseRunActivityChanged() =>
            RunActivityChanged?.Invoke(this, EventArgs.Empty);

        public void RaiseSessionCreated(SessionSummary summary) =>
            SessionCreated?.Invoke(this, new SessionCreatedEventArgs(summary));

        public void RaiseSessionRenamed(string sessionId, string title) =>
            SessionRenamed?.Invoke(this, new SessionRenamedEventArgs(sessionId, title));
    }

    private sealed class FakePreferencesStore : IAppPreferencesStore
    {
        public AppPreferences? Saved { get; private set; }

        public AppPreferences Load() => Saved ?? new AppPreferences();

        public void Save(AppPreferences preferences) => Saved = preferences;
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly List<SessionSummary> _sessions = [];
        private readonly Dictionary<string, List<ChatMessage>> _messages = [];
        private int _nextId;

        public Task<string> CreateAsync(string? title, CancellationToken ct)
        {
            var id = $"session-{++_nextId}";
            _sessions.Insert(0, new SessionSummary(
                id,
                title,
                new DateTimeOffset(2026, 6, 22, 12, _nextId, 0, TimeSpan.Zero)));
            _messages[id] = [];
            return Task.FromResult(id);
        }

        public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
        {
            _messages[sessionId].Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([.. _messages[sessionId]]);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([.. _sessions]);

        public Task DeleteAsync(string sessionId, CancellationToken ct)
        {
            _sessions.RemoveAll(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            _messages.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string sessionId, string title, CancellationToken ct)
        {
            var index = _sessions.FindIndex(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            if (index >= 0)
                _sessions[index] = _sessions[index] with { Title = title };
            return Task.CompletedTask;
        }

        public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
