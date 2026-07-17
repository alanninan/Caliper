// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Agents;

public abstract record TurnUpdate;
public sealed record ReasoningDelta(string Text)   : TurnUpdate;
public sealed record ContentDelta(string Text)     : TurnUpdate;
public sealed record TurnCompleted(ModelTurn Turn) : TurnUpdate;

public sealed record ModelTurn(
    string? Content,
    IReadOnlyList<ToolCall> ToolCalls,
    string? ReasoningOpaque,
    UsageInfo Usage);

// MalformedReason (TO_FIX §1): non-null when the model streamed arguments that failed to parse
// (Microsoft.Extensions.AI's adapter swallows the parse failure and hands back an empty object —
// see NativeToolStrategy's FunctionCallContent case). AgentRunner short-circuits these before
// dispatch or permission prompts; a default keeps every pre-existing 3-arg construction compiling.
public sealed record ToolCall(string CallId, string Tool, System.Text.Json.JsonElement Arguments, string? MalformedReason = null);
public sealed record UsageInfo(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
