// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Agents;
using Caliper.Core.Protocol;

namespace Caliper.Core.Models;

public sealed record ChatMessage(
    ChatRole Role,
    MessageKind Kind,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    JsonElement? Payload = null)
{
    public ChatMessage(ChatRole role, string content, MessageKind kind = MessageKind.Text)
        : this(role, kind, content)
    {
    }

    public static ChatMessage Text(ChatRole role, string content) =>
        new(role, MessageKind.Text, content);

    public static ChatMessage FromToolCall(ToolCall call)
    {
        var payload = JsonSerializer.SerializeToElement(new ToolCallPayload(
            call.CallId,
            call.Tool,
            call.Arguments),
            CaliperJsonContext.Default.ToolCallPayload);
        return new ChatMessage(
            ChatRole.Assistant,
            MessageKind.ToolCall,
            $"[ToolCall: {call.Tool}]\n{call.Arguments.GetRawText()}",
            call.CallId,
            call.Tool,
            payload);
    }

    public static ChatMessage FromToolResult(ToolCall call, ToolResult result)
    {
        var payload = JsonSerializer.SerializeToElement(new ToolResultPayload(
            call.CallId,
            call.Tool,
            result.Success,
            result.Output),
            CaliperJsonContext.Default.ToolResultPayload);
        var prefix = result.Success ? $"[Tool: {call.Tool}]" : $"[Tool: {call.Tool} ERROR]";
        return new ChatMessage(
            ChatRole.Tool,
            MessageKind.ToolResult,
            $"{prefix}\n{result.Output}",
            call.CallId,
            call.Tool,
            payload);
    }
}

public sealed record ToolCallPayload(
    string CallId,
    string ToolName,
    JsonElement Arguments);

public sealed record ToolResultPayload(
    string CallId,
    string ToolName,
    bool Success,
    string Output);
