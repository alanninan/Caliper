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

    /// <summary>
    /// Roadmap §3.4 durable execution: resumes a run left <c>interrupted</c> (by the startup sweep)
    /// by id — loads the run row, appends a resume note to its transcript, and continues the loop
    /// with the remaining step budget. The default implementation here (kept so a pre-existing
    /// <see cref="IConversationOrchestrator"/> test double compiles unchanged — matching the pattern
    /// on <see cref="IAgentRunner.RunAsync(RunSpec, CancellationToken)"/>) always reports "not
    /// supported": resuming fundamentally needs an <c>IRunStore</c>, which the interface itself
    /// doesn't carry. <c>ConversationOrchestrator</c> overrides this with the real implementation.
    /// </summary>
    Task<ConversationRunResult> ResumeAsync(
        string runId,
        Func<AgentEvent, CancellationToken, ValueTask>? onEvent,
        CancellationToken ct) =>
        Task.FromResult(new ConversationRunResult(
            null,
            $"Resume is not supported by this orchestrator (run '{runId}').",
            null,
            []));
}
