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

public sealed class OpenAIProviderTests
{
    [Fact]
    public async Task ListAsync_uses_saved_key_and_optional_openai_headers()
    {
        var credentials = new TestCredentialStore();
        credentials.Save(ProviderCredentialTargets.OpenAIApiKey, "stored-key");
        var handler = new CapturingOpenAIHandler();
        var provider = new OpenAIProvider(
            Options.Create(new ProvidersOptions
            {
                OpenAI = new OpenAIOptions
                {
                    Endpoint = "https://api.openai.test/v1",
                    ApiKey = "config-key",
                    Organization = "org-test",
                    Project = "project-test",
                },
            }),
            credentials,
            new StaticHttpClientFactory(handler),
            NullLoggerFactory.Instance);

        var models = await provider.ListAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("gpt-5.1", model.Id);
        Assert.True(model.Capabilities.SupportsTools);
        Assert.True(model.Capabilities.SupportsReasoning);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.Equal("stored-key", handler.Request.Headers.Authorization?.Parameter);
        Assert.Equal("org-test", Assert.Single(handler.Request.Headers.GetValues("OpenAI-Organization")));
        Assert.Equal("project-test", Assert.Single(handler.Request.Headers.GetValues("OpenAI-Project")));
        Assert.Equal("/v1/models", handler.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ListAsync_without_key_reports_provider_specific_configuration()
    {
        var provider = new OpenAIProvider(
            Options.Create(new ProvidersOptions()),
            new TestCredentialStore(),
            new StaticHttpClientFactory(new CapturingOpenAIHandler()),
            NullLoggerFactory.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ListAsync(CancellationToken.None));

        Assert.Contains("Providers:OpenAI:ApiKey", error.Message, StringComparison.Ordinal);
    }
}

file sealed class CapturingOpenAIHandler : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"object":"list","data":[{"id":"gpt-5.1","object":"model"}]}""",
                Encoding.UTF8,
                "application/json"),
        });
    }
}

file sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

file sealed class TestCredentialStore : IProviderCredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Save(string targetName, string secret) => _values[targetName] = secret;
    public bool TryRead(string targetName, out string secret) => _values.TryGetValue(targetName, out secret!);
    public void Delete(string targetName) => _values.Remove(targetName);
}
