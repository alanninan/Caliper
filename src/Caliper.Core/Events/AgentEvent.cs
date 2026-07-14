// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;

namespace Caliper.Core.Events;

/// <summary>
/// Every new subtype needs a decision (even if it's "no-op") in each of the three consumers, or a
/// host silently drops it:
/// <list type="bullet">
/// <item>Console <c>Rendering/EventRenderer.cs</c> — render or intentionally ignore.</item>
/// <item>App <c>ViewModels/AgentEventMapper.cs</c> (transcript, live) and, if the effect should
/// survive a reload, <c>ViewModels/PersistedTranscriptFactory.cs</c> (stored messages, replay).</item>
/// <item><c>tests/Caliper.Evals</c> — anywhere events are pattern-matched exhaustively; a default/
/// ignore arm is fine, it just needs to exist deliberately rather than by omission.</item>
/// </list>
/// </summary>
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

/// <summary>
/// Raised by the <c>task</c> subagent tool (roadmap §3.1) via <c>ToolContext.Emit</c> — see that
/// method's doc comment for why a tool surfaces events this way instead of an AgentRunner special
/// case. <paramref name="CallId"/> correlates back to the owning <c>ToolInvoked</c>/<c>ToolSucceeded</c>
/// call.
/// </summary>
public sealed record SubagentStarted(string CallId, string ChildSessionId, string Title)       : AgentEvent;
public sealed record SubagentCompleted(string CallId, string ChildSessionId, CompletionReason? Reason) : AgentEvent;

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

    /// <summary>
    /// Per-run working root (<c>RunSpec.WorkingRoot</c>). Null falls back to
    /// <c>runtimeSettings.Caliper.WorkingRoot</c>. <c>PermissionGate</c> builds its
    /// <c>FileAccessPolicy</c> against this root, so a scheduled job's in-root file writes
    /// auto-allow under <c>Auto</c> relative to the job's own root, not the global one.
    /// </summary>
    public string? WorkingRoot { get; init; }

    /// <summary>
    /// True when this request belongs to an unattended run (<c>RunSpec.Unattended</c>: scheduled
    /// jobs and manual <c>/schedule run</c> triggers). A <c>RoutingPermissionPrompt</c> uses it to
    /// send the request to the deny+report <c>UnattendedPermissionPrompt</c> instead of an
    /// interactive prompt; the gate's own policy evaluation ignores it.
    /// </summary>
    public bool Unattended { get; init; }
}
public enum PermissionDecision { Allow, AllowForSession, Deny }
