// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using Caliper.Core.Protocol;
using Caliper.Core.Tools;

namespace Caliper.Core.Agents;

public sealed class TwoPhaseStrategy(IModelClient modelClient) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var decisionRequest = new ModelRequest(
            context.System,
            context.Messages,
            ProtocolBuilder.BuildDecisionSchema(context.Tools ?? [], context.Skills ?? []),
            context.Parameters,
            ValidateAgentTurn: false);

        var decisionTurn = await modelClient.CompleteAsync(decisionRequest, ct).ConfigureAwait(false);
        var decision = ParseDecision(decisionTurn.RawJson);

        switch (decision.Action)
        {
            case "respond":
                await foreach (var update in StreamRespondAsync(context, ct).ConfigureAwait(false))
                    yield return update;
                yield break;

            case "call_tool":
                if (string.IsNullOrWhiteSpace(decision.Tool))
                    throw new JsonException("Two-phase decision selected call_tool without a tool.");

                var tool = (context.Tools ?? []).FirstOrDefault(t =>
                    string.Equals(t.Name, decision.Tool, StringComparison.Ordinal));
                if (tool.Name is null)
                    throw new JsonException($"Two-phase decision selected unknown tool '{decision.Tool}'.");

                var arguments = await CompleteToolArgumentsAsync(context, tool, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(decision.Rationale))
                    yield return new RationaleDelta(decision.Rationale);
                yield return new TurnCompleted(
                    new CallToolTurn
                    {
                        Rationale = decision.Rationale,
                        Tool = tool.Name,
                        Arguments = arguments,
                    },
                    decisionTurn.PromptTokens);
                yield break;

            case "load_skill":
                if (string.IsNullOrWhiteSpace(decision.Skill))
                    throw new JsonException("Two-phase decision selected load_skill without a skill.");

                var skillTurn = await CompleteBranchAsync(
                    context,
                    ProtocolBuilder.BuildSkillSchema(decision.Skill),
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(decision.Rationale))
                    yield return new RationaleDelta(decision.Rationale);
                yield return new TurnCompleted(skillTurn, decisionTurn.PromptTokens);
                yield break;

            default:
                throw new JsonException($"Unknown two-phase action '{decision.Action}'.");
        }
    }

    private async IAsyncEnumerable<TurnUpdate> StreamRespondAsync(
        TurnContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ModelRequest(
            context.System,
            context.Messages,
            ProtocolBuilder.BuildRespondSchema(),
            context.Parameters);

        var parser = new StreamingEnvelopeParser();
        int? promptTokens = null;

        await foreach (var chunk in modelClient.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
            {
                foreach (var delta in parser.Push(chunk.TextDelta.AsSpan()))
                {
                    yield return delta.Field switch
                    {
                        EnvelopeField.Rationale => new RationaleDelta(delta.Text),
                        EnvelopeField.Content => new ContentDelta(delta.Text),
                        _ => new RationaleDelta(delta.Text),
                    };
                }
            }

            if (chunk.Done)
            {
                promptTokens = chunk.PromptTokens;
                break;
            }
        }

        yield return new TurnCompleted(parser.Complete(), promptTokens);
    }

    private async Task<AgentTurn> CompleteBranchAsync(
        TurnContext context,
        JsonElement schema,
        CancellationToken ct)
    {
        var request = new ModelRequest(
            context.System,
            context.Messages,
            schema,
            context.Parameters);

        var turn = await modelClient.CompleteAsync(request, ct).ConfigureAwait(false);
        return AgentTurnParser.Parse(turn.RawJson);
    }

    private async Task<JsonElement> CompleteToolArgumentsAsync(
        TurnContext context,
        (string Name, JsonElement ArgumentSchema) tool,
        CancellationToken ct)
    {
        string? validationError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new ModelRequest(
                BuildToolArgumentSystem(tool, validationError),
                BuildToolArgumentMessages(context.Messages, tool.Name, validationError),
                tool.ArgumentSchema,
                context.Parameters,
                ValidateAgentTurn: false);

            var turn = await modelClient.CompleteAsync(request, ct).ConfigureAwait(false);
            var arguments = ParseArguments(turn.RawJson);
            validationError = ToolArgumentValidator.Validate(arguments, tool.ArgumentSchema);
            if (validationError is null)
                return arguments;
        }

        throw new JsonException($"Invalid arguments for tool '{tool.Name}': {validationError}");
    }

    private static JsonElement ParseArguments(string rawJson)
    {
        using var document = JsonDocument.Parse(AgentTurnParser.ExtractJsonObject(rawJson));
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<ChatMessage> BuildToolArgumentMessages(
        IReadOnlyList<ChatMessage> messages,
        string toolName,
        string? validationError)
    {
        if (validationError is null)
            return messages;

        return
        [
            .. messages,
            new ChatMessage(
                ChatRole.User,
                $"The previous arguments for tool '{toolName}' were invalid: {validationError}. Return corrected JSON arguments only."),
        ];
    }

    private static string BuildToolArgumentSystem(
        (string Name, JsonElement ArgumentSchema) tool,
        string? validationError)
    {
        var prompt = $"""
            You are generating JSON arguments for exactly one tool: {tool.Name}.
            Return JSON only. Do not return an action envelope.
            The JSON object must match this schema:
            {tool.ArgumentSchema.GetRawText()}

            Do not make assumptions about missing values.
            Do not wrap scalar values in arrays.
            Do not add fields that are not in the schema.
            {ToolSpecificHint(tool.Name)}
            """;

        if (validationError is null)
            return prompt;

        return prompt + $"\nPrevious validation error: {validationError}\nReturn corrected JSON arguments only.";
    }

    private static string ToolSpecificHint(string toolName) =>
        toolName switch
        {
            "fetch_url" => """Example: {"url":"https://example.com/article"}. The url field must be a single string, not an array.""",
            "search" => """Example: {"query":"current weather in Paris"}. The query field must be a single string, not an array.""",
            _ => string.Empty,
        };

    private static Decision ParseDecision(string rawJson)
    {
        using var doc = JsonDocument.Parse(AgentTurnParser.ExtractJsonObject(rawJson));
        var root = doc.RootElement;
        var action = root.GetProperty("action").GetString()
            ?? throw new JsonException("Decision action was null.");
        var rationale = root.TryGetProperty("rationale", out var rationaleProp)
            ? rationaleProp.GetString() ?? string.Empty
            : string.Empty;
        var tool = root.TryGetProperty("tool", out var toolProp) ? toolProp.GetString() : null;
        var skill = root.TryGetProperty("skill", out var skillProp) ? skillProp.GetString() : null;
        return new Decision(action, rationale, tool, skill);
    }

    private sealed record Decision(string Action, string Rationale, string? Tool, string? Skill);
}
