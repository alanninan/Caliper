// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;

namespace Caliper.Evals;

// Replays scripted JSON strings turn-by-turn, one string per model call.
// The full JSON is emitted as a single streaming chunk, which exercises the
// legacy local-model completion path
// without requiring Ollama. Char-by-char streaming is covered by unit tests.
internal sealed class FakeModelClient(IReadOnlyList<string> scriptedTurns) : IModelClient
{
    private int _index;

    private string Dequeue() =>
        _index < scriptedTurns.Count
            ? scriptedTurns[_index++]
            : """{"rationale":"","action":"respond","content":"[script exhausted]"}""";

#pragma warning disable CS1998
    public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var json = Dequeue();
        yield return new ModelStreamChunk(json, Done: false, PromptTokens: null,  OutputTokens: null);
        yield return new ModelStreamChunk("",   Done: true,  PromptTokens: 0,     OutputTokens: json.Length);
    }
#pragma warning restore CS1998

    public Task<RawModelCompletion> CompleteAsync(ModelRequest request, CancellationToken ct) =>
        Task.FromResult(new RawModelCompletion(Dequeue(), PromptTokens: 0, OutputTokens: 0));
}
