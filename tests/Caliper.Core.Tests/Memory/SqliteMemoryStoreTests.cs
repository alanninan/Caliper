// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Memory;

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly List<string> _paths = [];

    [Fact]
    public async Task Remember_recall_forget_and_upsert_round_trip()
    {
        var store = Build(NewDbPath());

        await store.RememberAsync(MemoryScope.Global, "preference", "tabs", CancellationToken.None);
        await store.RememberAsync(MemoryScope.Global, "preference", "spaces", CancellationToken.None);

        var recalled = await store.RecallAsync(MemoryScope.Global, "preference", CancellationToken.None);
        Assert.Single(recalled);
        Assert.Equal("spaces", recalled[0].Value);

        await store.ForgetAsync(MemoryScope.Global, "preference", CancellationToken.None);

        Assert.Empty(await store.RecallAsync(MemoryScope.Global, "preference", CancellationToken.None));
    }

    [Fact]
    public async Task Render_includes_global_and_requested_project_scope_only()
    {
        var store = Build(NewDbPath());
        var projectA = "project:A";
        var projectB = "project:B";

        await store.RememberAsync(MemoryScope.Global, "global_fact", "visible", CancellationToken.None);
        await store.RememberAsync(projectA, "project_fact", "visible", CancellationToken.None);
        await store.RememberAsync(projectB, "other_project", "hidden", CancellationToken.None);

        var rendered = await store.RenderForPromptAsync(projectA, CancellationToken.None);

        Assert.Contains("global_fact: visible", rendered, StringComparison.Ordinal);
        Assert.Contains("project_fact: visible", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("other_project", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_is_bounded()
    {
        var store = Build(NewDbPath(), maxChars: 80);
        await store.RememberAsync(MemoryScope.Global, "big", new string('x', 500), CancellationToken.None);

        var rendered = await store.RenderForPromptAsync(MemoryScope.Global, CancellationToken.None);

        Assert.True(rendered.Length <= 80);
    }

    private static SqliteMemoryStore Build(string path, int maxChars = 4096) =>
        new(
            Options.Create(new PersistenceOptions { SqlitePath = path }),
            Options.Create(new CaliperOptions { ToolOutputMaxChars = maxChars }),
            NullLogger<SqliteMemoryStore>.Instance);

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "caliper-memory-" + Guid.NewGuid().ToString("N") + ".db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths.SelectMany(path => new[] { path, $"{path}-wal", $"{path}-shm" }))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
