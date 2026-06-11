// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text;
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

public sealed class ConstrainedEnvelopeStrategy(
    IChatClientProvider chatClients,
    IRuntimeSettings runtimeSettings,
    ILogger<ConstrainedEnvelopeStrategy> logger) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var options = runtimeSettings.Caliper;
        var schema = context.Tools.BuildResponseSchema(context.SkillMenu ?? []);
        var chatOptions = new ChatOptions
        {
            Temperature = (float)context.Parameters.Temperature,
            MaxOutputTokens = context.Parameters.MaxOutputTokens,
            Seed = context.Parameters.Seed,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema,
                "caliper_turn",
                "A constrained Caliper turn envelope."),
        };

        var parser = new StreamingEnvelopeParser();
        var usage = new UsageInfo(null, null, null);
        var raw = new StringBuilder();

        var chatClient = chatClients.GetClient(options.Model);
        await foreach (var update in chatClient.GetStreamingResponseAsync(BuildMessages(context), chatOptions, ct).ConfigureAwait(false))
        {
            foreach (var item in update.Contents)
            {
                switch (item)
                {
                    case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                        yield return new ReasoningDelta(reasoningContent.Text);
                        break;

                    case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                        raw.Append(textContent.Text);
                        foreach (var delta in parser.Push(textContent.Text))
                        {
                            if (delta.Field == EnvelopeField.Rationale)
                                yield return new ReasoningDelta(delta.Text);
                            else if (delta.Field == EnvelopeField.Content)
                                yield return new ContentDelta(delta.Text);
                        }
                        break;

                    case UsageContent usageContent:
                        usage = ToUsageInfo(usageContent.Details);
                        break;
                }
            }
        }

        AgentTurn turn;
        try
        {
            turn = parser.Complete();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogError("Constrained envelope parse failed. Raw response: {Response}", raw.ToString());
            throw new InvalidOperationException($"Constrained envelope parse failed: {ex.Message}", ex);
        }

        yield return new TurnCompleted(ToModelTurn(turn, usage));
    }

    private static List<AIChatMessage> BuildMessages(TurnContext context)
    {
        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.System, BuildSystemPrompt(context)),
        };

        foreach (var message in context.Messages)
            messages.Add(ToAiMessage(message));

        return messages;
    }

    private static string BuildSystemPrompt(TurnContext context)
    {
        var toolMenu = context.Tools.BuildToolMenu();
        if (string.IsNullOrWhiteSpace(toolMenu))
            return context.System;

        return $"{context.System}\n\n## Tools\n{toolMenu}";
    }

    private static AIChatMessage ToAiMessage(CaliperChatMessage message) =>
        new(ToAiRole(message.Role), message.Content);

    private static AIChatRole ToAiRole(CaliperChatRole role) =>
        role switch
        {
            CaliperChatRole.System => AIChatRole.System,
            CaliperChatRole.User => AIChatRole.User,
            CaliperChatRole.Assistant => AIChatRole.Assistant,
            CaliperChatRole.Tool => AIChatRole.Tool,
            _ => AIChatRole.User,
        };

    private static ModelTurn ToModelTurn(AgentTurn turn, UsageInfo usage) =>
        turn switch
        {
            RespondTurn respond => new ModelTurn(
                respond.Content,
                [],
                respond.Rationale,
                usage),
            CallToolTurn call => new ModelTurn(
                null,
                [new ToolCall(Guid.NewGuid().ToString("N"), call.Tool, call.Arguments.Clone())],
                call.Rationale,
                usage),
            LoadSkillTurn skill => new ModelTurn(
                null,
                [new ToolCall(
                    Guid.NewGuid().ToString("N"),
                    "load_skill",
                    JsonSerializer.SerializeToElement(
                        new LoadSkillArguments(skill.Skill),
                        CaliperJsonContext.Default.LoadSkillArguments))],
                skill.Rationale,
                usage),
            _ => throw new InvalidOperationException($"Unsupported turn type: {turn.GetType().Name}"),
        };

    private static UsageInfo ToUsageInfo(UsageDetails details) =>
        new(ToNullableInt(details.InputTokenCount), ToNullableInt(details.OutputTokenCount), ToNullableInt(details.TotalTokenCount));

    private static int? ToNullableInt(long? value) =>
        value > int.MaxValue ? int.MaxValue : (int?)value;
}
