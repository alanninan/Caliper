// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;

namespace Caliper.App.Tests;

public sealed class SessionUsageStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "caliper-tests", Path.GetRandomFileName());

    private SessionUsageStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SessionUsageStore(Path.Combine(_directory, "app-usage.json"));
    }

    [Fact]
    public void Save_then_load_from_a_new_store_round_trips_raw_counts()
    {
        var path = Path.Combine(_directory, "app-usage.json");
        var store = CreateStore();

        store.Save("session-1", new SessionUsage(100, 50));
        store.Save("session-2", new SessionUsage(null, null));

        var reloaded = new SessionUsageStore(path);
        var all = reloaded.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Equal(new SessionUsage(100, 50), all["session-1"]);
        Assert.Equal(new SessionUsage(null, null), all["session-2"]);
    }

    [Fact]
    public void Remove_deletes_entry_and_persists_for_the_next_load()
    {
        var path = Path.Combine(_directory, "app-usage.json");
        var store = CreateStore();
        store.Save("session-1", new SessionUsage(10, 5));
        store.Save("session-2", new SessionUsage(20, 15));

        store.Remove("session-1");

        var reloaded = new SessionUsageStore(path);
        var all = reloaded.LoadAll();
        Assert.False(all.ContainsKey("session-1"));
        Assert.True(all.ContainsKey("session-2"));
    }

    [Fact]
    public void Remove_of_an_unknown_session_does_not_throw_and_does_not_rewrite()
    {
        var store = CreateStore();
        store.Save("session-1", new SessionUsage(10, 5));

        var exception = Record.Exception(() => store.Remove("no-such-session"));

        Assert.Null(exception);
        Assert.True(store.LoadAll().ContainsKey("session-1"));
    }

    [Fact]
    public void Load_missing_file_returns_empty()
    {
        var store = CreateStore();

        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Load_corrupt_file_returns_empty()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "app-usage.json");
        File.WriteAllText(path, "not valid json {{{");

        var store = new SessionUsageStore(path);

        Assert.Empty(store.LoadAll());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test over it.
        }
    }
}
