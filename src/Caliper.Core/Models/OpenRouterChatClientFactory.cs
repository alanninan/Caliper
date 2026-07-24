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
    IProviderCredentialStore credentials,
    ILoggerFactory loggerFactory) : IChatClientProvider
{
    public IChatClient GetClient(string modelSlug)
    {
        var openRouter = providerOptions.Value.OpenRouter;
        var apiKey = credentials.TryRead(ProviderCredentialTargets.OpenRouterApiKey, out var stored)
            ? stored
            : openRouter.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new UnavailableChatClient("Providers:OpenRouter:ApiKey is not configured.");

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(openRouter.Endpoint) };
        clientOptions.AddPolicy(new OpenRouterAttributionPolicy(openRouter), PipelinePosition.PerCall);

        IChatClient model = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                clientOptions)
            .GetChatClient(modelSlug)
            .AsIChatClient();

        return new ChatClientBuilder(model)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: "Caliper")
            .Build();
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
