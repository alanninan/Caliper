// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using Caliper.Core.Context;

namespace Caliper.Core.Abstractions;

public interface ISessionStore
{
    Task<string> CreateAsync(string? title, CancellationToken ct);
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct);
    Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct);
}
