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

    private sealed class FakeMemoryStore(params MemoryEntry[] entries) : IMemoryStore
    {
        public Task<string> RenderForPromptAsync(string scope, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task RememberAsync(string scope, string key, string value, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. entries.Where(e => string.Equals(e.Scope, scope, StringComparison.Ordinal))]);

        public Task ForgetAsync(string scope, string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeCaliperMdProvider(ProjectMemoryDocument document) : ICaliperMdProvider
    {
        public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) => Task.FromResult(document);
    }
}
