// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Caliper.Core.Protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using CaliperChatMessage = Caliper.Core.Models.ChatMessage;
using CaliperChatRole = Caliper.Core.Models.ChatRole;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Caliper.Core.Agents;

public sealed class NativeToolStrategy(
    IChatClientProvider chatClients,
    IModelCapabilityProvider capabilityProvider,
    IRuntimeSettings runtimeSettings,
    ILogger<NativeToolStrategy> logger) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var options = runtimeSettings.Caliper;
        var modelSlug = context.Model ?? options.Model;
        var capabilities = await capabilityProvider.GetAsync(modelSlug, ct).ConfigureAwait(false);
        var messages = BuildMessages(context);
        var tools = context.Tools.AsAIFunctions().Cast<AITool>().ToList();
        if (options.TurnStrategy == TurnStrategyKind.Auto && !capabilities.SupportsTools)
        {
            if (tools.Count > 0)
            {
                logger.LogWarning(
                    "Model '{Model}' is not known to support native tools; continuing in respond-only mode.",
                    modelSlug);
            }
            tools = [];
        }

        var chatOptions = new ChatOptions
        {
            Temperature = (float)context.Parameters.Temperature,
            MaxOutputTokens = context.Parameters.MaxOutputTokens,
            Seed = context.Parameters.Seed,
            Tools = tools,
            AllowMultipleToolCalls = true,
            Reasoning = capabilities.SupportsReasoning
                ? new Microsoft.Extensions.AI.ReasoningOptions
            {
                Effort = ToReasoningEffort(options.Reasoning.Effort),
                Output = options.Reasoning.Exclude ? ReasoningOutput.None : ReasoningOutput.Full,
            }
                : null,
        };

        var content = new StringBuilder();
        var calls = new Dictionary<string, ToolCall>(StringComparer.Ordinal);
        var reasoning = new StringBuilder();
        UsageInfo usage = new(null, null, null);

        var chatClient = chatClients.GetClient(modelSlug);
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, ct).ConfigureAwait(false))
        {
            foreach (var item in update.Contents)
            {
                switch (item)
                {
                    case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                        reasoning.Append(reasoningContent.Text);
                        yield return new ReasoningDelta(reasoningContent.Text);
                        break;

                    case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                        content.Append(textContent.Text);
                        yield return new ContentDelta(textContent.Text);
                        break;

                    case FunctionCallContent functionCall:
                        var callId = string.IsNullOrWhiteSpace(functionCall.CallId)
                            ? $"call_{calls.Count + 1}"
                            : functionCall.CallId;

                        // TO_FIX §1: the adapter accumulates streamed argument fragments and parses
                        // them at end-of-stream. On a parse failure it does not throw — it hands back
                        // Arguments = null (which JsonValue.FromValueMap turns into "{}") with the
                        // parse error stashed in .Exception. Surface that instead of silently
                        // dispatching garbage arguments.
                        string? malformedReason = null;
                        if (functionCall.Exception is not null)
                        {
                            malformedReason = string.IsNullOrEmpty(functionCall.Exception.Message)
                                ? "malformed streamed arguments"
                                : functionCall.Exception.Message;
                            logger.LogWarning(
                                functionCall.Exception,
                                "Model streamed malformed arguments for tool '{Tool}' (call {CallId}); the call will not be executed.",
                                functionCall.Name,
                                callId);
                        }

                        calls[callId] = new ToolCall(
                            callId,
                            functionCall.Name,
                            JsonValue.FromValueMap(functionCall.Arguments),
                            malformedReason);
                        break;

                    case UsageContent usageContent:
                        usage = ToUsageInfo(usageContent.Details);
                        break;
                }
            }
        }

        yield return new TurnCompleted(new ModelTurn(
            calls.Count == 0 ? content.ToString() : null,
            [.. calls.Values],
            reasoning.Length > 0 ? reasoning.ToString() : null,
            usage));
    }

    private static List<AIChatMessage> BuildMessages(TurnContext context)
    {
        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.System, context.System),
        };

        foreach (var message in context.Messages)
            messages.Add(ToAiMessage(message));

        return HealDanglingToolCalls(messages);
    }

    // A stored transcript can end an assistant tool_calls message without its matching tool result
    // (a run cancelled or a process killed mid-tool). OpenAI-compatible endpoints reject an
    // assistant tool_calls message that isn't followed by a response for every call id, so a single
    // orphan poisons every later turn until compaction happens to drop it. Synthesize a stand-in
    // result for any call id whose response is missing before the next non-tool message. This also
    // heals sessions already corrupted on disk.
    private static List<AIChatMessage> HealDanglingToolCalls(List<AIChatMessage> messages)
    {
        var healed = new List<AIChatMessage>(messages.Count);
        var pending = new List<string>();

        void FlushPending()
        {
            foreach (var callId in pending)
            {
                healed.Add(new AIChatMessage(AIChatRole.Tool, [
                    new FunctionResultContent(callId, "[no result — run was interrupted]"),
                ]));
            }

            pending.Clear();
        }

        foreach (var message in messages)
        {
            var resultIds = message.Contents.OfType<FunctionResultContent>().Select(c => c.CallId).ToList();
            if (resultIds.Count > 0)
            {
                // A tool result answers pending calls; keep it and drop those ids from pending.
                foreach (var id in resultIds)
                    pending.Remove(id);
                healed.Add(message);
                continue;
            }

            // Any other message (assistant text or new tool_calls, user, system) starts a fresh
            // block, so anything still pending never got its result — fill it in first.
            FlushPending();
            healed.Add(message);
            pending.AddRange(message.Contents.OfType<FunctionCallContent>().Select(c => c.CallId));
        }

        FlushPending();
        return healed;
    }

    private static AIChatMessage ToAiMessage(CaliperChatMessage message)
    {
        if (message.Kind == MessageKind.ToolCall && message.Payload is not null)
            return BuildToolCallMessage(message);
        if (message.Kind == MessageKind.ToolResult && message.Payload is not null)
            return BuildToolResultMessage(message);

        // Text, Summary, or a tool-kind message that lost its payload (e.g. a compaction
        // placeholder). Render as plain content under a role the API accepts standalone —
        // never emit an orphaned Tool-role message with no matching tool call.
        var role = message.Role == CaliperChatRole.Tool ? AIChatRole.Assistant : ToAiRole(message.Role);
        return new AIChatMessage(role, message.Content);
    }

    private static AIChatMessage BuildToolCallMessage(CaliperChatMessage message)
    {
        var payload = ReadToolCallPayload(message);
        var arguments = ToObjectDictionary(payload.Arguments);
        return new AIChatMessage(AIChatRole.Assistant, [
            new FunctionCallContent(payload.CallId, payload.NameOrFallback(message), arguments),
        ]);
    }

    private static AIChatMessage BuildToolResultMessage(CaliperChatMessage message)
    {
        var payload = ReadToolResultPayload(message);
        return new AIChatMessage(AIChatRole.Tool, [
            new FunctionResultContent(payload.CallId, payload.Output),
        ]);
    }

    private static ToolCallPayload ReadToolCallPayload(CaliperChatMessage message)
    {
        if (message.Payload is null)
            throw new InvalidOperationException($"Stored {message.Kind} message is missing payload.");

        return JsonSerializer.Deserialize(
                message.Payload.Value.GetRawText(),
                CaliperJsonContext.Default.ToolCallPayload)
            ?? throw new InvalidOperationException($"Stored {message.Kind} payload could not be deserialized.");
    }

    private static ToolResultPayload ReadToolResultPayload(CaliperChatMessage message)
    {
        if (message.Payload is null)
            throw new InvalidOperationException($"Stored {message.Kind} message is missing payload.");

        return JsonSerializer.Deserialize(
                message.Payload.Value.GetRawText(),
                CaliperJsonContext.Default.ToolResultPayload)
            ?? throw new InvalidOperationException($"Stored {message.Kind} payload could not be deserialized.");
    }

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement arguments)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object)
            return dictionary;

        foreach (var property in arguments.EnumerateObject())
            dictionary[property.Name] = JsonElementToObject(property.Value);

        return dictionary;
    }

    private static object? JsonElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone(),
        };

    private static AIChatRole ToAiRole(CaliperChatRole role) =>
        role switch
        {
            CaliperChatRole.System => AIChatRole.System,
            CaliperChatRole.User => AIChatRole.User,
            CaliperChatRole.Assistant => AIChatRole.Assistant,
            CaliperChatRole.Tool => AIChatRole.Tool,
            _ => AIChatRole.User,
        };

    private static UsageInfo ToUsageInfo(UsageDetails details) =>
        new(ToNullableInt(details.InputTokenCount), ToNullableInt(details.OutputTokenCount), ToNullableInt(details.TotalTokenCount));

    private static int? ToNullableInt(long? value) =>
        value > int.MaxValue ? int.MaxValue : (int?)value;

    private static ReasoningEffort? ToReasoningEffort(string effort) =>
        effort.ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "extra-high" or "extrahigh" => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Medium,
        };
}

internal static class JsonValue
{
    public static JsonElement FromValueMap(IDictionary<string, object?>? values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, values ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        }

        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> values)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in values)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float single:
                writer.WriteNumberValue(single);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteObject(writer, readOnlyDictionary);
                break;
            case IDictionary<string, object?> dictionary:
                WriteObject(writer, dictionary);
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                WriteObject(writer, pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
                break;
            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}

internal static class ToolCallPayloadExtensions
{
    public static string NameOrFallback(this ToolCallPayload payload, CaliperChatMessage message) =>
        string.IsNullOrWhiteSpace(payload.ToolName)
            ? message.ToolName ?? "unknown"
            : payload.ToolName;
}
