// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;

namespace Caliper.App.Tests;

/// <summary>
/// In-memory fake for <see cref="ISessionUsageStore"/> so ChatViewModel tests can seed persisted
/// usage before construction and assert on Save/Remove calls without touching disk.
/// </summary>
internal sealed class FakeSessionUsageStore : ISessionUsageStore
{
    private readonly Dictionary<string, SessionUsage> _entries = new(StringComparer.Ordinal);

    public List<string> RemovedSessionIds { get; } = [];
    public List<(string SessionId, SessionUsage Usage)> SavedCalls { get; } = [];

    public void Seed(string sessionId, SessionUsage usage) => _entries[sessionId] = usage;

    public IReadOnlyDictionary<string, SessionUsage> LoadAll() => _entries;

    public void Save(string sessionId, SessionUsage usage)
    {
        _entries[sessionId] = usage;
        SavedCalls.Add((sessionId, usage));
    }

    public void Remove(string sessionId)
    {
        _entries.Remove(sessionId);
        RemovedSessionIds.Add(sessionId);
    }
}
