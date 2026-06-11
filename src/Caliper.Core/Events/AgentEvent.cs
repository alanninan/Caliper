// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;

namespace Caliper.Core.Events;

public abstract record AgentEvent;
public sealed record TurnStarted(int Step)                                      : AgentEvent;
public sealed record ReasoningDelta(string Text)                                : AgentEvent;
public sealed record ReasoningCompleted(string Text)                            : AgentEvent;
public sealed record AssistantMessageDelta(string Text)                         : AgentEvent;
public sealed record AssistantMessage(string Content)                           : AgentEvent;
public sealed record ToolInvoked(string CallId, string Tool, JsonElement Arguments) : AgentEvent;
public sealed record ToolSucceeded(string CallId, string Tool, string Output)   : AgentEvent;
public sealed record ToolFailed(string CallId, string Tool, string Error)       : AgentEvent;
public sealed record PermissionRequested(PermissionRequest Request)             : AgentEvent;
public sealed record PermissionResolved(string Tool, PermissionDecision Decision): AgentEvent;
public sealed record SkillLoaded(string Skill)                                  : AgentEvent;
public sealed record ContextCompacted(int BeforeTokens, int AfterTokens)        : AgentEvent;
public sealed record McpServerConnected(string Name, int ToolCount)             : AgentEvent;
public sealed record McpServerFailed(string Name, string Error)                 : AgentEvent;
public sealed record UsageReported(int? Prompt, int? Completion)                : AgentEvent;
public sealed record RunCompleted(CompletionReason Reason)                      : AgentEvent;
public sealed record RunFailed(string Error)                                    : AgentEvent;

public sealed record PermissionRequest(string Tool, SideEffect Effect, JsonElement Arguments, string? Reason);
public enum PermissionDecision { Allow, AllowForSession, Deny }
