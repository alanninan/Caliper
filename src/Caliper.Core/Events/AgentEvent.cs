// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.Core.Events;

public abstract record AgentEvent;
public sealed record TurnStarted(int Step)                                      : AgentEvent;
public sealed record ReasoningDelta(string Text)                                : AgentEvent;
public sealed record ReasoningCompleted(string Text)                            : AgentEvent;
public sealed record AssistantMessageDelta(string Text)                         : AgentEvent;
public sealed record AssistantMessage(string Content)                           : AgentEvent;
public sealed record ToolInvoked(string CallId, string Tool, JsonElement Arguments) : AgentEvent;
public sealed record ToolSucceeded(
    string CallId,
    string Tool,
    string Output,
    Caliper.Core.Models.FileChange? FileChange = null)                         : AgentEvent;
public sealed record ToolFailed(string CallId, string Tool, string Error)       : AgentEvent;
public sealed record PermissionRequested(PermissionRequest Request)             : AgentEvent;
public sealed record PermissionResolved(string Tool, PermissionDecision Decision, string? RequestId = null): AgentEvent;
public sealed record SkillLoaded(string Skill)                                  : AgentEvent;
public sealed record ContextCompacted(int BeforeTokens, int AfterTokens)        : AgentEvent;
public sealed record McpServerConnected(string Name, int ToolCount)             : AgentEvent;
public sealed record McpServerFailed(string Name, string Error)                 : AgentEvent;
public sealed record UsageReported(int? Prompt, int? Completion, int? CumulativePrompt, int? CumulativeCompletion) : AgentEvent;
public sealed record RunCompleted(CompletionReason Reason)                      : AgentEvent;
public sealed record RunFailed(string Error)                                    : AgentEvent;

public sealed record PermissionRequest(
    string Tool,
    SideEffect Effect,
    JsonElement Arguments,
    string? Reason,
    string? RequestId = null)
{
    /// <summary>
    /// False for MCP-provided tools, whose self-declared read-only annotation is an untrusted
    /// hint. Untrusted read-only tools do not get the free auto-allow that built-ins receive.
    /// </summary>
    public bool TrustedReadOnly { get; init; } = true;

    /// <summary>
    /// The session this request belongs to. "Allow for session" approvals are scoped by this id so
    /// a grant made to a run in one session cannot leak into — or be revoked by switching to —
    /// another. Null (e.g. a host that doesn't track sessions) falls back to a single shared scope.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Per-run permission overlay (<c>RunSpec.PermissionsOverlay</c>). Null falls back to
    /// <c>runtimeSettings.Permissions</c>. <c>PermissionGate</c> still merges the global shell
    /// denylist into an overlay's own denylist (union, never replace) as a safety floor.
    /// </summary>
    public PermissionsOptions? Overlay { get; init; }
}
public enum PermissionDecision { Allow, AllowForSession, Deny }
