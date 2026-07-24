// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net.Http.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Models;

internal sealed class OpenRouterCapabilityProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ProvidersOptions> providerOptions,
    ILogger<OpenRouterCapabilityProvider> logger) : IModelCapabilityProvider, IModelCatalog, IDisposable
{
    public const string HttpClientName = "openrouter-meta";
    private const int FallbackContextWindow = 32768;
    private IReadOnlyDictionary<string, ModelCapabilities>? _cache;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public async Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct)
    {
        try
        {
            var cache = await GetCacheAsync(ct).ConfigureAwait(false);
            if (cache.TryGetValue(modelSlug, out var capabilities))
                return capabilities;

            logger.LogWarning("OpenRouter model '{Model}' was not present in the models metadata; using fallback capabilities.", modelSlug);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning("Could not load OpenRouter model capabilities: {Message}. Using fallback capabilities.", ex.Message);
        }

        return Fallback();
    }

    public async Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct)
    {
        var cache = await GetCacheAsync(ct).ConfigureAwait(false);
        return cache
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ModelCatalogEntry(entry.Key, entry.Value))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, ModelCapabilities>> GetCacheAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
                return _cache;

            var endpoint = providerOptions.Value.OpenRouter.Endpoint.TrimEnd('/') + "/";
            using var http = httpClientFactory.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(endpoint);
            var response = await http.GetFromJsonAsync("models", CaliperJsonContext.Default.OpenRouterModelsResponse, ct)
                .ConfigureAwait(false);

            var data = response?.Data ?? [];
            _cache = data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .ToDictionary(
                    m => m.Id,
                    m =>
                    {
                        var supported = new HashSet<string>(m.SupportedParameters ?? [], StringComparer.OrdinalIgnoreCase);
                        return new ModelCapabilities(
                            SupportsTools: supported.Contains("tools"),
                            SupportsReasoning: supported.Contains("reasoning") || supported.Contains("reasoning_effort"),
                            SupportsStructuredOutputs: supported.Contains("structured_outputs") || supported.Contains("response_format"),
                            ContextWindowTokens: m.ContextLength.GetValueOrDefault(FallbackContextWindow));
                    },
                    StringComparer.OrdinalIgnoreCase);

            return _cache;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    // Optimistic on tools: if we could not confirm capabilities (metadata fetch failed, or the
    // slug is missing from the catalog) we assume tools are supported rather than silently
    // dropping every tool. A model that genuinely lacks tools surfaces a clear API error instead.
    private static ModelCapabilities Fallback() =>
        new(SupportsTools: true, SupportsReasoning: false, SupportsStructuredOutputs: false, ContextWindowTokens: FallbackContextWindow);

    public void Dispose() => _cacheGate.Dispose();

}
