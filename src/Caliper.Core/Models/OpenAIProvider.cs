// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Caliper.Core.Models;

#pragma warning disable OPENAI001 // Responses SDK/MEAI adapter is the supported OpenAI path for this provider.
/// <summary>OpenAI Platform provider using API-key authentication and the Responses API.</summary>
internal sealed class OpenAIProvider(
    IOptions<ProvidersOptions> providerOptions,
    IProviderCredentialStore credentials,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : IModelProvider
{
    private const int FallbackContextWindow = 128_000;

    public const string HttpClientName = "openai-meta";
    public string Id => ProviderIds.OpenAI;
    public string DisplayName => "OpenAI";
    public ProviderAuthenticationKind AuthenticationKind => ProviderAuthenticationKind.ApiKey;

    public IChatClient GetClient(string modelSlug)
    {
        var options = providerOptions.Value.OpenAI;
        var apiKey = GetApiKey(options);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new UnavailableChatClient("Providers:OpenAI:ApiKey is not configured.");

        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) };
        clientOptions.AddPolicy(new OpenAIHeadersPolicy(options), PipelinePosition.PerCall);

        IChatClient model = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetResponsesClient()
            .AsIChatClient(modelSlug);

        return new ChatClientBuilder(model)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: "Caliper")
            .Build();
    }

    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(CapabilitiesFor(modelSlug));

    public async Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct)
    {
        var options = providerOptions.Value.OpenAI;
        var apiKey = GetApiKey(options);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Providers:OpenAI:ApiKey is not configured.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            options.Endpoint.TrimEnd('/') + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        AddOptionalHeaders(request, options);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync(
            CaliperJsonContext.Default.OpenAIModelsResponse,
            ct).ConfigureAwait(false);

        return (models?.Data ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(model => new ModelCatalogEntry(model.Id, CapabilitiesFor(model.Id)))
            .ToList();
    }

    private string? GetApiKey(OpenAIOptions options) =>
        credentials.TryRead(ProviderCredentialTargets.OpenAIApiKey, out var stored)
            ? stored
            : options.ApiKey;

    private static void AddOptionalHeaders(HttpRequestMessage request, OpenAIOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Organization))
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", options.Organization);
        if (!string.IsNullOrWhiteSpace(options.Project))
            request.Headers.TryAddWithoutValidation("OpenAI-Project", options.Project);
    }

    private static ModelCapabilities CapabilitiesFor(string modelSlug)
    {
        var reasoning =
            modelSlug.StartsWith("o", StringComparison.OrdinalIgnoreCase) ||
            modelSlug.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
        return new ModelCapabilities(
            SupportsTools: true,
            SupportsReasoning: reasoning,
            SupportsStructuredOutputs: true,
            ContextWindowTokens: FallbackContextWindow);
    }

    private sealed class OpenAIHeadersPolicy(OpenAIOptions options) : PipelinePolicy
    {
        public override void Process(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            AddHeaders(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            AddHeaders(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }

        private void AddHeaders(PipelineMessage message)
        {
            if (!string.IsNullOrWhiteSpace(options.Organization))
                message.Request.Headers.Set("OpenAI-Organization", options.Organization);
            if (!string.IsNullOrWhiteSpace(options.Project))
                message.Request.Headers.Set("OpenAI-Project", options.Project);
        }
    }
}
#pragma warning restore OPENAI001
