// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Caliper.Core.Models;

internal sealed class OpenRouterChatClientProvider(
    IOptions<ProvidersOptions> providerOptions,
    ILoggerFactory loggerFactory) : IChatClientProvider
{
    private readonly Dictionary<string, IChatClient> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IChatClient GetClient(string modelSlug)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(modelSlug, out var cached))
                return cached;

            var created = Create(modelSlug);
            _cache[modelSlug] = created;
            return created;
        }
    }

    private IChatClient Create(string modelSlug)
    {
        var openRouter = providerOptions.Value.OpenRouter;
        if (string.IsNullOrWhiteSpace(openRouter.ApiKey))
            return new UnavailableChatClient("Providers:OpenRouter:ApiKey is not configured.");

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(openRouter.Endpoint) };
        clientOptions.AddPolicy(new OpenRouterAttributionPolicy(openRouter), PipelinePosition.PerCall);

        IChatClient model = new OpenAIClient(
                new ApiKeyCredential(openRouter.ApiKey),
                clientOptions)
            .GetChatClient(modelSlug)
            .AsIChatClient();

        return new ChatClientBuilder(model)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: "Caliper")
            .Build();
    }

    private sealed class UnavailableChatClient(string reason) : IChatClient
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

    private sealed class OpenRouterAttributionPolicy(OpenRouterOptions options) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            AddHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            AddHeaders(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private void AddHeaders(PipelineMessage message)
        {
            message.Request.Headers.Set("X-OpenRouter-Title", options.AppTitle);
            message.Request.Headers.Set("X-Title", options.AppTitle);
            if (!string.IsNullOrWhiteSpace(options.AppReferer))
                message.Request.Headers.Set("HTTP-Referer", options.AppReferer);
        }
    }
}
