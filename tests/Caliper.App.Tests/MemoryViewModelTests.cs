// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Memory;
using Microsoft.Extensions.Options;

namespace Caliper.App.Tests;

public sealed class MemoryViewModelTests
{
    [Fact]
    public async Task RefreshMemoryAsync_populates_memories_and_project_document()
    {
        var memoryStore = new FakeMemoryStore(
            new MemoryEntry("global", "style", "concise", DateTimeOffset.UtcNow));
        var caliperMdProvider = new FakeCaliperMdProvider(
            new ProjectMemoryDocument("/repo/CALIPER.md", "# Notes", Truncated: false));
        var runtimeSettings = new TestRuntimeSettings();
        var viewModel = new MemoryViewModel(memoryStore, caliperMdProvider, runtimeSettings);

        await viewModel.RefreshMemoryCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasMemories);
        Assert.Equal("style", Assert.Single(viewModel.Memories).Key);
        Assert.Equal("# Notes", viewModel.ProjectDocument);
        Assert.Equal("/repo/CALIPER.md", viewModel.ProjectDocumentPath);
    }

    [Fact]
    public async Task RefreshMemoryAsync_no_document_shows_placeholder()
    {
        var viewModel = new MemoryViewModel(
            new FakeMemoryStore(),
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings());

        await viewModel.RefreshMemoryCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasMemories);
        Assert.Equal("No project memory document was found.", viewModel.ProjectDocument);
    }

    [Fact]
    public async Task ForgetAsync_calls_store_and_refreshes()
    {
        var memoryStore = new FakeMemoryStore(new MemoryEntry("global", "style", "concise", DateTimeOffset.UtcNow));
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings());
        await viewModel.RefreshMemoryCommand.ExecuteAsync(null);
        var item = Assert.Single(viewModel.Memories);

        await viewModel.ForgetCommand.ExecuteAsync(item);

        Assert.Equal([("global", "style")], memoryStore.ForgottenCalls);
        Assert.False(viewModel.HasMemories);
        Assert.Equal("Forgot 'style'.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ForgetAsync_failure_surfaces_error_and_keeps_entries()
    {
        var memoryStore = new FakeMemoryStore(new MemoryEntry("global", "style", "concise", DateTimeOffset.UtcNow))
        {
            NextForgetException = new IOException("disk full"),
        };
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings());
        await viewModel.RefreshMemoryCommand.ExecuteAsync(null);
        var item = Assert.Single(viewModel.Memories);

        await viewModel.ForgetCommand.ExecuteAsync(item);

        Assert.Equal("disk full", viewModel.StatusMessage);
        Assert.True(viewModel.HasMemories);
        Assert.Single(viewModel.Memories);
    }

    [Fact]
    public async Task RememberAsync_trims_key_and_upserts_then_clears_fields_on_success()
    {
        var memoryStore = new FakeMemoryStore();
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings())
        {
            MemoryScopeIndex = 0,
            MemoryKey = "  style  ",
            MemoryValue = "concise",
        };

        await viewModel.RememberCommand.ExecuteAsync(null);

        Assert.Equal([("global", "style", "concise")], memoryStore.RememberedCalls);
        Assert.Equal("Saved 'style'.", viewModel.StatusMessage);
        Assert.Equal(string.Empty, viewModel.MemoryKey);
        Assert.Equal(string.Empty, viewModel.MemoryValue);
        Assert.True(viewModel.HasMemories);
    }

    [Fact]
    public async Task RememberAsync_project_scope_uses_working_root()
    {
        var memoryStore = new FakeMemoryStore();
        var runtimeSettings = new TestRuntimeSettings();
        runtimeSettings.Caliper.WorkingRoot = "/repo";
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            runtimeSettings)
        {
            MemoryScopeIndex = 1,
            MemoryKey = "convention",
            MemoryValue = "tabs",
        };

        await viewModel.RememberCommand.ExecuteAsync(null);

        var call = Assert.Single(memoryStore.RememberedCalls);
        Assert.Equal(MemoryScope.Project("/repo"), call.Scope);
    }

    [Fact]
    public async Task RememberAsync_empty_key_or_value_never_hits_the_store()
    {
        var memoryStore = new FakeMemoryStore();
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings())
        {
            MemoryKey = "   ",
            MemoryValue = "concise",
        };

        await viewModel.RememberCommand.ExecuteAsync(null);

        Assert.Empty(memoryStore.RememberedCalls);
        Assert.Equal("Enter both a key and a value before remembering.", viewModel.StatusMessage);

        viewModel.MemoryKey = "style";
        viewModel.MemoryValue = "   ";

        await viewModel.RememberCommand.ExecuteAsync(null);

        Assert.Empty(memoryStore.RememberedCalls);
    }

    [Fact]
    public async Task RememberAsync_failure_keeps_the_users_input()
    {
        var memoryStore = new FakeMemoryStore { NextRememberException = new IOException("disk full") };
        var viewModel = new MemoryViewModel(
            memoryStore,
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings())
        {
            MemoryKey = "style",
            MemoryValue = "concise",
        };

        await viewModel.RememberCommand.ExecuteAsync(null);

        Assert.Equal("disk full", viewModel.StatusMessage);
        Assert.Equal("style", viewModel.MemoryKey);
        Assert.Equal("concise", viewModel.MemoryValue);
    }

    [Fact]
    public void PrefillFromEntry_maps_global_and_project_scope_to_the_scope_index()
    {
        var viewModel = new MemoryViewModel(
            new FakeMemoryStore(),
            new FakeCaliperMdProvider(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false)),
            new TestRuntimeSettings());

        viewModel.PrefillFromEntry(new MemoryItemViewModel("global", "style", "concise", "now"));
        Assert.Equal(0, viewModel.MemoryScopeIndex);

        viewModel.PrefillFromEntry(new MemoryItemViewModel(MemoryScope.Project("/repo"), "convention", "tabs", "now"));
        Assert.Equal(1, viewModel.MemoryScopeIndex);
        Assert.Equal("convention", viewModel.MemoryKey);
        Assert.Equal("tabs", viewModel.MemoryValue);
    }

    private sealed class FakeMemoryStore(params MemoryEntry[] entries) : IMemoryStore
    {
        private readonly List<MemoryEntry> _entries = [.. entries];

        public List<(string Scope, string Key, string Value)> RememberedCalls { get; } = [];
        public List<(string Scope, string Key)> ForgottenCalls { get; } = [];
        public Exception? NextRememberException { get; set; }
        public Exception? NextForgetException { get; set; }

        public Task<string> RenderForPromptAsync(string scope, CancellationToken ct) => Task.FromResult(string.Empty);

        public Task RememberAsync(string scope, string key, string value, CancellationToken ct)
        {
            RememberedCalls.Add((scope, key, value));
            if (NextRememberException is { } ex)
            {
                NextRememberException = null;
                throw ex;
            }

            _entries.RemoveAll(e => string.Equals(e.Scope, scope, StringComparison.Ordinal) && string.Equals(e.Key, key, StringComparison.Ordinal));
            _entries.Add(new MemoryEntry(scope, key, value, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. _entries.Where(e => string.Equals(e.Scope, scope, StringComparison.Ordinal))]);

        public Task ForgetAsync(string scope, string key, CancellationToken ct)
        {
            ForgottenCalls.Add((scope, key));
            if (NextForgetException is { } ex)
            {
                NextForgetException = null;
                throw ex;
            }

            _entries.RemoveAll(e => string.Equals(e.Scope, scope, StringComparison.Ordinal) && string.Equals(e.Key, key, StringComparison.Ordinal));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCaliperMdProvider(ProjectMemoryDocument document) : ICaliperMdProvider
    {
        public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) => Task.FromResult(document);
    }
}
