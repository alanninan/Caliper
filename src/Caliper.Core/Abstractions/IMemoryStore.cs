// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Abstractions;

public interface IMemoryStore
{
    Task<string> RenderForPromptAsync(string scope, CancellationToken ct);
    Task RememberAsync(string scope, string key, string value, CancellationToken ct);
    Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct);
    Task ForgetAsync(string scope, string key, CancellationToken ct);
}

public sealed record MemoryEntry(string Scope, string Key, string Value, DateTimeOffset UpdatedAt);
