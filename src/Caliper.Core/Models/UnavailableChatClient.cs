// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.AI;

namespace Caliper.Core.Models;

/// <summary>
/// A chat client that always throws with <paramref name="reason"/>. Returned by a provider's
/// <c>IChatClientProvider.GetClient</c> when its API key is not configured, so the failure
/// surfaces at the point of use (the first model call) with a clear, actionable message instead
/// of at startup or silently.
/// </summary>
internal sealed class UnavailableChatClient(string reason) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw new InvalidOperationException(reason);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
