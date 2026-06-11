// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Text;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Models;

public sealed class OpenRouterCapabilityProviderTests
{
    [Fact]
    public async Task Parses_model_metadata_into_capabilities()
    {
        var provider = BuildProvider("""
            {
              "data": [
                {
                  "id": "test/model",
                  "supported_parameters": ["tools", "reasoning", "response_format"],
                  "context_length": 12345
                }
              ]
            }
            """);

        var capabilities = await provider.GetAsync("test/model", CancellationToken.None);

        Assert.True(capabilities.SupportsTools);
        Assert.True(capabilities.SupportsReasoning);
        Assert.True(capabilities.SupportsStructuredOutputs);
        Assert.Equal(12345, capabilities.ContextWindowTokens);
    }

    [Fact]
    public async Task Fallback_is_respond_only_when_metadata_is_unavailable()
    {
        var provider = BuildProvider("""{"data": []}""");

        var capabilities = await provider.GetAsync("missing/model", CancellationToken.None);

        Assert.False(capabilities.SupportsTools);
        Assert.False(capabilities.SupportsReasoning);
        Assert.False(capabilities.SupportsStructuredOutputs);
        Assert.Equal(32768, capabilities.ContextWindowTokens);
    }

    private static OpenRouterCapabilityProvider BuildProvider(string json)
    {
        var handler = new StaticJsonHandler(json);
        return new OpenRouterCapabilityProvider(
            new HttpClient(handler),
            Options.Create(new CaliperOptions { Provider = "OpenRouter" }),
            Options.Create(new ProvidersOptions
            {
                OpenRouter = new OpenRouterOptions { Endpoint = "https://openrouter.ai/api/v1" },
            }),
            NullLogger<OpenRouterCapabilityProvider>.Instance);
    }
}

file sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("/api/v1/models", request.RequestUri?.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}
