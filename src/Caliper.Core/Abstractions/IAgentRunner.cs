// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Agents;
using Caliper.Core.Events;

namespace Caliper.Core.Abstractions;

public interface IAgentRunner
{
    IAsyncEnumerable<AgentEvent> RunAsync(
        string sessionId,
        string userMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Runs one turn loop scoped by <paramref name="spec"/>: model, tool filter, permission
    /// overlay, step budget, subagent depth, and job name. The default implementation ignores
    /// every <see cref="RunSpec"/> field beyond <see cref="RunSpec.SessionId"/>/
    /// <see cref="RunSpec.Prompt"/> and forwards to the legacy overload, so an
    /// <see cref="IAgentRunner"/> implementation written before <see cref="RunSpec"/> existed
    /// (e.g. a test double) keeps compiling and behaving unchanged.
    /// <see cref="Caliper.Core.Agents.AgentRunner"/> overrides this with the real,
    /// scope-aware loop and makes the legacy overload forward to it instead.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(RunSpec spec, CancellationToken ct = default) =>
        RunAsync(spec.SessionId, spec.Prompt, ct);
}
