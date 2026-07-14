// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;

namespace Caliper.Core.Abstractions;

public interface IConversationOrchestrator
{
    // Carried-forward item A (roadmap §3.1): these two overloads previously existed only on the
    // concrete ConversationOrchestrator class. The subagent tool resolves IConversationOrchestrator
    // through IServiceProvider (see SubagentTool's DI-cycle note) and needs a RunSpec-based
    // RunToCompletionAsync to run a scoped child, so both move onto the interface here. Any
    // IConversationOrchestrator test double must implement both now (see FakeConversationOrchestrator
    // in Caliper.App.Tests).
    Task<ConversationRunResult> RunToCompletionAsync(
        string sessionId,
        string prompt,
        Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
        CancellationToken ct);

    Task<ConversationRunResult> RunToCompletionAsync(
        RunSpec spec,
        Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
        CancellationToken ct);

    Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct);
}
