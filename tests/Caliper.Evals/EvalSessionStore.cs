// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Context;
using Caliper.Core.Models;

namespace Caliper.Evals;

internal sealed class EvalSessionStore : ISessionStore
{
    private readonly Dictionary<string, List<ChatMessage>> _sessions = [];
    private readonly Lock _lock = new();

    public Task<string> CreateAsync(string? title, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock) _sessions[id] = [];
        return Task.FromResult(id);
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var list))
                list = _sessions[sessionId] = [];
            list.Add(message);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<ChatMessage> msgs = _sessions.TryGetValue(sessionId, out var list) ? [..list] : [];
            return Task.FromResult(msgs);
        }
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        lock (_lock) _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        lock (_lock)
        {
            var prefix = _sessions.TryGetValue(sessionId, out var existing)
                ? existing.Take(Math.Max(0, fit.ActiveStartIndex)).ToList()
                : [];
            prefix.AddRange(fit.Messages);
            _sessions[sessionId] = prefix;
        }
        return Task.CompletedTask;
    }
}
