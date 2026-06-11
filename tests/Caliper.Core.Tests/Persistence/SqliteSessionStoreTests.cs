// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Models;
using Caliper.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Persistence;

public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly List<string> _paths = [];

    [Fact]
    public async Task Create_append_and_load_round_trips_messages_in_insertion_order()
    {
        var path = NewDbPath();
        var store = Build(path);
        var sessionId = await store.CreateAsync("test", CancellationToken.None);
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "system", MessageKind.Text),
            new ChatMessage(ChatRole.User, "user", MessageKind.Text),
            new ChatMessage(ChatRole.Assistant, "call", MessageKind.ToolCall),
            new ChatMessage(ChatRole.Tool, "result", MessageKind.ToolResult),
            new ChatMessage(ChatRole.Assistant, "answer", MessageKind.Text),
        };

        foreach (var message in messages)
            await store.AppendAsync(sessionId, message, CancellationToken.None);

        var loaded = await store.LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal(messages, loaded);
    }

    [Fact]
    public async Task List_returns_sessions_newest_first_with_titles()
    {
        var path = NewDbPath();
        var store = Build(path);
        var first = await store.CreateAsync("first", CancellationToken.None);
        await Task.Delay(20);
        var second = await store.CreateAsync(null, CancellationToken.None);

        var sessions = await store.ListAsync(CancellationToken.None);

        Assert.Equal(second, sessions[0].Id);
        Assert.Null(sessions[0].Title);
        Assert.Equal(first, sessions[1].Id);
        Assert.Equal("first", sessions[1].Title);
    }

    [Fact]
    public async Task Persists_across_store_instances()
    {
        var path = NewDbPath();
        var firstStore = Build(path);
        var sessionId = await firstStore.CreateAsync("restart", CancellationToken.None);
        await firstStore.AppendAsync(sessionId, new ChatMessage(ChatRole.User, "hello"), CancellationToken.None);

        var secondStore = Build(path);
        var loaded = await secondStore.LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal([new ChatMessage(ChatRole.User, "hello")], loaded);
    }

    [Fact]
    public async Task Unknown_session_throws()
    {
        var store = Build(NewDbPath());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => store.AppendAsync("missing", new ChatMessage(ChatRole.User, "hello"), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => store.LoadAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task Schema_creation_is_idempotent()
    {
        var path = NewDbPath();

        _ = await Build(path).CreateAsync("one", CancellationToken.None);
        _ = await Build(path).CreateAsync("two", CancellationToken.None);

        var sessions = await Build(path).ListAsync(CancellationToken.None);
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public async Task Create_stores_configured_model()
    {
        var path = NewDbPath();
        var store = Build(path, model: "provider/model");
        _ = await store.CreateAsync("model test", CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT model FROM sessions LIMIT 1;";

        var model = await command.ExecuteScalarAsync();
        Assert.Equal("provider/model", model);
    }

    [Fact]
    public async Task ReplaceWithCompaction_rewrites_messages_and_preserves_payloads()
    {
        var path = NewDbPath();
        var store = Build(path);
        var sessionId = await store.CreateAsync("compact", CancellationToken.None);
        var call = new Caliper.Core.Agents.ToolCall(
            "call_1",
            "echo",
            System.Text.Json.JsonSerializer.SerializeToElement(new { text = "hello" }));
        var summary = new ChatMessage(ChatRole.System, MessageKind.Summary, "Earlier conversation summary.");
        var keptCall = ChatMessage.FromToolCall(call);
        var keptResult = ChatMessage.FromToolResult(call, new ToolResult(true, "ok"));

        await store.AppendAsync(sessionId, new ChatMessage(ChatRole.User, "old"), CancellationToken.None);
        await store.ReplaceWithCompactionAsync(
            sessionId,
            new ContextFit([summary, keptCall, keptResult], true, 100, 30, 30),
            CancellationToken.None);

        var loaded = await store.LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal([MessageKind.Summary, MessageKind.ToolCall, MessageKind.ToolResult], loaded.Select(message => message.Kind));
        Assert.NotNull(loaded[1].Payload);
        Assert.NotNull(loaded[2].Payload);
    }

    [Fact]
    public async Task ReplaceWithCompaction_preserves_messages_before_reset_boundary()
    {
        var path = NewDbPath();
        var store = Build(path);
        var sessionId = await store.CreateAsync("clear then compact", CancellationToken.None);
        var beforeClear = new ChatMessage(ChatRole.User, "before clear");
        var reset = new ChatMessage(ChatRole.System, MessageKind.Summary, Caliper.Core.Agents.AgentRunner.ContextResetMarker);
        var summary = new ChatMessage(ChatRole.System, MessageKind.Summary, "post-clear summary");

        await store.AppendAsync(sessionId, beforeClear, CancellationToken.None);
        await store.AppendAsync(sessionId, reset, CancellationToken.None);
        await store.AppendAsync(sessionId, new ChatMessage(ChatRole.User, "post clear old"), CancellationToken.None);
        await store.ReplaceWithCompactionAsync(
            sessionId,
            new ContextFit([summary], true, 100, 20, 20, RawEstimatedPromptTokens: 18, ActiveStartIndex: 2),
            CancellationToken.None);

        var loaded = await store.LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal("before clear", loaded[0].Content);
        Assert.Equal(Caliper.Core.Agents.AgentRunner.ContextResetMarker, loaded[1].Content);
        Assert.Equal("post-clear summary", loaded[2].Content);
    }

    private static SqliteSessionStore Build(string path, string model = "test-model") =>
        new(
            Options.Create(new PersistenceOptions { SqlitePath = path }),
            Options.Create(new CaliperOptions { Model = model }),
            NullLogger<SqliteSessionStore>.Instance);

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "caliper-" + Guid.NewGuid().ToString("N") + ".db");
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
