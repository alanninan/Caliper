// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using Caliper.Core.Protocol;

namespace Caliper.Core.Agents;

public sealed class SingleEnvelopeStrategy(IModelClient modelClient) : ITurnStrategy
{
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ModelRequest(
            context.System,
            context.Messages,
            context.ResponseSchema,
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
                        EnvelopeField.Content   => new ContentDelta(delta.Text),
                        _                       => new RationaleDelta(delta.Text),
                    };
                }
            }

            if (chunk.Done)
            {
                promptTokens = chunk.PromptTokens;
                break;
            }
        }

        AgentTurn turn;
        try
        {
            turn = parser.Complete();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse completed turn: {ex.Message}", ex);
        }

        yield return new TurnCompleted(turn, promptTokens);
    }
}
