// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Text;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Models;

public sealed class ModelProviderRouterTests
{
    [Fact]
    public async Task Gemini_provider_dispatches_chat_client_and_capabilities_to_gemini()
    {
        var (router, _) = Build(provider: "Gemini");

        // No Gemini key configured: the Gemini chat client is an UnavailableChatClient whose
        // message names the Gemini config key — proving GetClient reached the Gemini provider,
        // not OpenRouter.
        var client = router.GetClient("gemini-2.5-flash");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([]));
        Assert.Contains("Providers:Gemini:ApiKey", ex.Message, StringComparison.Ordinal);

        var capabilities = await router.GetAsync("gemini-2.5-flash", CancellationToken.None);
        Assert.Equal(1_048_576, capabilities.ContextWindowTokens);

        var entries = await router.ListAsync(CancellationToken.None);
        Assert.Contains(entries, entry => entry.Id == "gemini-2.5-flash");
    }

    [Fact]
    public async Task OpenRouter_provider_dispatches_chat_client_and_capabilities_to_openrouter()
    {
        var (router, _) = Build(provider: "OpenRouter", openRouterJson: """{"data": []}""");

        var client = router.GetClient("test/model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([]));
        Assert.Contains("Providers:OpenRouter:ApiKey", ex.Message, StringComparison.Ordinal);

        var capabilities = await router.GetAsync("test/model", CancellationToken.None);
        // OpenRouter's own optimistic fallback (32768), distinct from Gemini's (1_048_576),
        // confirming this call reached the OpenRouter concrete.
        Assert.Equal(32768, capabilities.ContextWindowTokens);
    }

    [Fact]
    public async Task Unrecognized_provider_fails_explicitly()
    {
        var (router, _) = Build(provider: "SomethingElse", openRouterJson: """{"data": []}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.GetAsync("test/model", CancellationToken.None));

        Assert.Contains("Unknown model provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_provider_switch_is_reflected_on_the_next_call()
    {
        var (router, runtimeSettings) = Build(provider: "OpenRouter", openRouterJson: """{"data": []}""");

        var beforeSwitch = await router.GetAsync("test/model", CancellationToken.None);
        Assert.Equal(32768, beforeSwitch.ContextWindowTokens);

        runtimeSettings.SetProvider("Gemini");

        // Same router instance, no rebuild: the switch is picked up on the very next call.
        var afterSwitch = await router.GetAsync("gemini-2.5-flash", CancellationToken.None);
        Assert.Equal(1_048_576, afterSwitch.ContextWindowTokens);
    }

    private static (ModelProviderRouter Router, RuntimeSettings RuntimeSettings) Build(
        string provider,
        string openRouterJson = """{"data": []}""")
    {
        var caliperOptions = Options.Create(new CaliperOptions { Provider = provider });
        var providersOptions = Options.Create(new ProvidersOptions
        {
            OpenRouter = new OpenRouterOptions { Endpoint = "https://openrouter.ai/api/v1" },
            Gemini = new GeminiOptions { Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/" },
        });
        var runtimeSettings = new RuntimeSettings(caliperOptions, Options.Create(new PermissionsOptions()));

        var credentials = new TestProviderCredentialStore();
        var openRouterChat = new OpenRouterChatClientProvider(
            providersOptions,
            credentials,
            NullLoggerFactory.Instance);
        var geminiChat = new GeminiChatClientProvider(
            providersOptions,
            credentials,
            NullLoggerFactory.Instance);
        var openRouterCapabilities = new OpenRouterCapabilityProvider(
            new StaticHttpClientFactory(new StaticJsonHandler(openRouterJson)),
            providersOptions,
            NullLogger<OpenRouterCapabilityProvider>.Instance);
        var geminiCapabilities = new GeminiCapabilityProvider();

        var router = new ModelProviderRouter(
            runtimeSettings,
            [
                new CompositeModelProvider(
                    ProviderIds.OpenRouter,
                    "OpenRouter",
                    ProviderAuthenticationKind.ApiKey,
                    openRouterChat,
                    openRouterCapabilities,
                    openRouterCapabilities),
                new CompositeModelProvider(
                    ProviderIds.Gemini,
                    "Google Gemini",
                    ProviderAuthenticationKind.ApiKey,
                    geminiChat,
                    geminiCapabilities,
                    geminiCapabilities),
            ]);

        return (router, runtimeSettings);
    }
}

file sealed class TestProviderCredentialStore : IProviderCredentialStore
{
    public void Save(string targetName, string secret)
    {
    }

    public bool TryRead(string targetName, out string secret)
    {
        secret = string.Empty;
        return false;
    }

    public void Delete(string targetName)
    {
    }
}

file sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

file sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
}
