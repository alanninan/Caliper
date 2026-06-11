// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Events;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using System.Text.Json;

namespace Caliper.Evals;

public sealed record EvalCase(
    string Id,
    string UserMessage,
    IReadOnlyList<string> ScriptedTurns,
    IReadOnlyDictionary<string, string>? MockToolResponses,
    Func<IReadOnlyList<AgentEvent>, EvalOutcome> Assert,
    IReadOnlyList<string>? ExpectedTools = null,
    Func<JsonElement, bool>? ValidateArgs = null,
    IReadOnlyList<MockToolSpec>? ToolSpecs = null,
    PermissionMode? PermissionMode = null,
    IReadOnlyList<PermissionDecision>? ScriptedPromptDecisions = null,
    Func<IToolRegistry>? ToolRegistryFactory = null,
    Func<ITokenCounter, IContextManager>? ContextFactory = null,
    ModelCapabilities? Capabilities = null,
    Func<ISessionStore, string, CancellationToken, Task>? SeedSessionAsync = null,
    Func<IReadOnlyList<AgentEvent>, int, bool>? PermissionCorrect = null,
    Func<IReadOnlyList<AgentEvent>, bool>? CompactionSafe = null,
    CaliperOptions? RuntimeOptions = null
);

public sealed record MockToolSpec(
    string Name,
    string Description,
    JsonElement Schema,
    string Response,
    SideEffect SideEffect,
    bool IsMcp = false);

public sealed record EvalOutcome(bool Pass, string? Reason = null)
{
    public static EvalOutcome Ok(string? reason = null) => new(true, reason);
    public static EvalOutcome Fail(string reason)       => new(false, reason);
}

public sealed record EvalResult(
    string CaseId,
    EvalOutcome Outcome,
    IReadOnlyList<AgentEvent> Events,
    TimeSpan Elapsed,
    bool? CorrectTool = null,   // null = case sets no ExpectedTools (not applicable)
    bool? ValidArgs = null,     // null = case sets no ValidateArgs (not applicable)
    bool? PermissionCorrect = null,
    bool? CompactionSafe = null,
    int PromptCount = 0
);

public sealed record SuiteResult(
    string SuiteName,
    string? ModelName,
    DateTimeOffset RunAt,
    IReadOnlyList<EvalResult> Results
);
