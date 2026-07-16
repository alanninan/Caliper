// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core;

namespace Caliper.App.Preferences;

public sealed record SessionUsage(int? PromptTokens, int? CompletionTokens);

public interface ISessionUsageStore
{
    IReadOnlyDictionary<string, SessionUsage> LoadAll();
    void Save(string sessionId, SessionUsage usage);
    void Remove(string sessionId);
}

/// <summary>
/// App-local persistence for the chat token-usage footer, keyed by session id. Core's session
/// store persists no usage data, so recomputing it from a stored transcript on restart isn't
/// possible; this stores the raw cumulative counts (not the formatted footer string, so
/// formatting can evolve independently) outside Caliper.Core's config.json, mirroring
/// <see cref="AppPreferencesStore"/>.
/// </summary>
public sealed class SessionUsageStore : ISessionUsageStore
{
    private readonly string _path;
    private readonly Dictionary<string, SessionUsage> _cache;

    public SessionUsageStore()
        : this(Path.Combine(CaliperHome.Resolve(), "app-usage.json"))
    {
    }

    // Test seam: the tests compile this file as linked source, so they can round-trip against a
    // temp file instead of the user's real ~/.caliper/app-usage.json.
    internal SessionUsageStore(string path)
    {
        _path = path;
        _cache = Load(path);
    }

    public IReadOnlyDictionary<string, SessionUsage> LoadAll() => _cache;

    public void Save(string sessionId, SessionUsage usage)
    {
        _cache[sessionId] = usage;
        Persist();
    }

    public void Remove(string sessionId)
    {
        if (_cache.Remove(sessionId))
            Persist();
    }

    private static Dictionary<string, SessionUsage> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, SessionUsage>(StringComparer.Ordinal);

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, SessionUsage>>(json)
                ?? new Dictionary<string, SessionUsage>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, SessionUsage>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed usage write is non-fatal (the footer is presentation-only) but shouldn't be
            // completely silent, or a persistently unwritable file goes unnoticed in diagnosis.
            System.Diagnostics.Debug.WriteLine($"Failed to save session usage: {ex.Message}");
        }
    }
}
