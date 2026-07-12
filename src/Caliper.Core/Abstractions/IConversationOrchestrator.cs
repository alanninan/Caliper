// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Context;

namespace Caliper.Core.Abstractions;

public interface IConversationOrchestrator
{
    Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct);
}
