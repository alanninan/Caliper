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

public sealed record ToolCall(string CallId, string Tool, System.Text.Json.JsonElement Arguments);
public sealed record UsageInfo(int? PromptTokens, int? CompletionTokens, int? TotalTokens);
