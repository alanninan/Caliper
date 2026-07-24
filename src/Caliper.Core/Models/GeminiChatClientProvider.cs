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

namespace Caliper.Core.Models;

/// <summary>
/// Talks to Google Gemini through its OpenAI-compatible endpoint, reusing the same OpenAI client
/// pipeline as <see cref="OpenRouterChatClientProvider"/> — no dedicated Gemini SDK or DTOs.
/// </summary>
internal sealed class GeminiChatClientProvider(
    IOptions<ProvidersOptions> providerOptions,
    IProviderCredentialStore credentials,
    ILoggerFactory loggerFactory) : IChatClientProvider
{
    public IChatClient GetClient(string modelSlug)
    {
        var gemini = providerOptions.Value.Gemini;
        var apiKey = credentials.TryRead(ProviderCredentialTargets.GeminiApiKey, out var stored)
            ? stored
            : gemini.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new UnavailableChatClient("Providers:Gemini:ApiKey is not configured.");

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(gemini.Endpoint) };

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
}
