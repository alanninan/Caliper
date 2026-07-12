// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;

namespace Caliper.Core.Abstractions;

public interface IAgentRunner
{
    IAsyncEnumerable<AgentEvent> RunAsync(
        string sessionId,
        string userMessage,
        CancellationToken ct = default);
}
