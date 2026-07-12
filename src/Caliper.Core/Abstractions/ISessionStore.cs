// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using Caliper.Core.Context;

namespace Caliper.Core.Abstractions;

public interface ISessionStore
{
    Task<string> CreateAsync(string? title, CancellationToken ct);
    async Task<SessionSummary> CreateWithSummaryAsync(string? title, CancellationToken ct)
    {
        var id = await CreateAsync(title, ct).ConfigureAwait(false);
        return (await ListAsync(ct).ConfigureAwait(false))
            .First(item => string.Equals(item.Id, id, StringComparison.Ordinal));
    }
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
    Task RenameAsync(string sessionId, string title, CancellationToken ct);
    Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct);
}
