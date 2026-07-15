// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Collections.ObjectModel;
using System.Text.Json;
using Caliper.Core.Agents;
using Caliper.Core.Models;

namespace Caliper.App.ViewModels;

public static class PersistedTranscriptFactory
{
    // Auto-compaction stores the summary as "Earlier conversation summary:\n{summary}" (see
    // AutoCompactingContextManager). The divider label already conveys the "earlier" framing, so the
    // redundant prefix is trimmed for display; unrecognised content is shown verbatim.
    private const string SummaryPrefix = "Earlier conversation summary:";

    public static ObservableCollection<ChatItemViewModel> Create(
        IReadOnlyList<ChatMessage> messages)
    {
        ObservableCollection<ChatItemViewModel> items = [];
        ToolActivityViewModel? activity = null;
        foreach (var message in messages)
        {
            switch (message.Kind)
            {
                case MessageKind.Text:
                    activity = null;
                    AddText(items, message);
                    break;
                case MessageKind.ToolCall:
                    AddToolCall(items, ref activity, message);
                    break;
                case MessageKind.ToolResult:
                    AddToolResult(items, ref activity, message);
                    break;
                case MessageKind.Summary:
                    activity = null;
                    items.Add(message.Content.StartsWith(AgentRunner.ContextResetMarker, StringComparison.Ordinal)
                        ? new CompactionMarkerViewModel("Context cleared")
                        : new CompactionMarkerViewModel(
                            "Conversation compacted",
                            summary: StripSummaryPrefix(message.Content)));
                    break;
            }
        }

        return items;
    }

    private static string StripSummaryPrefix(string content) =>
        content.StartsWith(SummaryPrefix, StringComparison.Ordinal)
            ? content[SummaryPrefix.Length..].TrimStart('\r', '\n', ' ')
            : content;

    private static ToolActivityViewModel EnsureActivity(
        ObservableCollection<ChatItemViewModel> items,
        ref ToolActivityViewModel? activity)
    {
        if (activity is not null)
            return activity;

        activity = new ToolActivityViewModel();
        items.Add(activity);
        return activity;
    }

    private static void AddText(
        ObservableCollection<ChatItemViewModel> items,
        ChatMessage message)
    {
        switch (message.Role)
        {
            case ChatRole.User:
                items.Add(new UserMessageViewModel(message.Content));
                break;
            case ChatRole.Assistant:
                items.Add(new AssistantMessageViewModel
                {
                    Content = message.Content,
                    IsStreaming = false,
                });
                break;
            default:
                items.Add(new RunStatusViewModel(message.Role.ToString(), message.Content));
                break;
        }
    }

    private static void AddToolCall(
        ObservableCollection<ChatItemViewModel> items,
        ref ToolActivityViewModel? activity,
        ChatMessage message)
    {
        var callId = message.ToolCallId ?? ReadString(message.Payload, "CallId") ?? string.Empty;
        var toolName = message.ToolName ?? ReadString(message.Payload, "ToolName") ?? "tool";
        var arguments = ReadRaw(message.Payload, "Arguments") ?? message.Content;
        EnsureActivity(items, ref activity).Add(new ToolCallViewModel(callId, toolName, arguments));
    }

    private static void AddToolResult(
        ObservableCollection<ChatItemViewModel> items,
        ref ToolActivityViewModel? activity,
        ChatMessage message)
    {
        var callId = message.ToolCallId ?? ReadString(message.Payload, "CallId") ?? string.Empty;
        var toolName = message.ToolName ?? ReadString(message.Payload, "ToolName") ?? "tool";
        var success = ReadBoolean(message.Payload, "Success");
        var output = ReadString(message.Payload, "Output") ?? message.Content;
        var tool = items.OfType<ToolActivityViewModel>()
            .SelectMany(item => item.Calls)
            .LastOrDefault(item => string.Equals(item.CallId, callId, StringComparison.Ordinal));
        if (tool is null)
        {
            tool = new ToolCallViewModel(callId, toolName, string.Empty);
            EnsureActivity(items, ref activity).Add(tool);
        }

        // Distinguish a permission denial from an ordinary failure so a reloaded session shows *why*
        // the tool didn't run, not just that it failed.
        var denied = success == false && ToolCallStatus.IsDenial(output);
        tool.Status = (success, denied) switch
        {
            (_, true) => ToolCallStatus.Denied,
            (true, _) => ToolCallStatus.Succeeded,
            (false, _) => ToolCallStatus.Failed,
            (null, _) => ToolCallStatus.Completed,
        };
        tool.Output = output;
        tool.IsExpanded = success == false;
        tool.SetFileChange(ReadFileChange(message.Payload));
    }

    private static string? ReadString(JsonElement? payload, string propertyName) =>
        TryGetProperty(payload, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBoolean(JsonElement? payload, string propertyName) =>
        TryGetProperty(payload, propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadRaw(JsonElement? payload, string propertyName) =>
        TryGetProperty(payload, propertyName, out var value)
            ? value.GetRawText()
            : null;

    private static FileChange? ReadFileChange(JsonElement? payload)
    {
        if (!TryGetProperty(payload, "FileChange", out var change) ||
            change.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = ReadNestedString(change, "Path");
        var before = ReadNestedString(change, "Before");
        var after = ReadNestedString(change, "After");
        if (path is null || before is null || after is null)
            return null;

        var truncated = TryGetNestedProperty(change, "Truncated", out var truncatedElement) &&
            truncatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            truncatedElement.GetBoolean();
        return new FileChange(path, before, after, truncated);
    }

    private static string? ReadNestedString(JsonElement element, string propertyName) =>
        TryGetNestedProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetNestedProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        var camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return element.TryGetProperty(camelCase, out value);
    }

    private static bool TryGetProperty(
        JsonElement? payload,
        string propertyName,
        out JsonElement value)
    {
        if (payload is { ValueKind: JsonValueKind.Object } element)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;

            var camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            if (element.TryGetProperty(camelCase, out value))
                return true;
        }

        value = default;
        return false;
    }
}
