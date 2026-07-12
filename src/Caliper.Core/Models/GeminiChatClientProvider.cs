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
        var gemini = providerOptions.Value.Gemini;
        if (string.IsNullOrWhiteSpace(gemini.ApiKey))
            return new UnavailableChatClient("Providers:Gemini:ApiKey is not configured.");

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(gemini.Endpoint) };

        IChatClient model = new OpenAIClient(
                new ApiKeyCredential(gemini.ApiKey),
                clientOptions)
            .GetChatClient(modelSlug)
            .AsIChatClient();

        return new ChatClientBuilder(model)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: "Caliper")
            .Build();
    }
}
