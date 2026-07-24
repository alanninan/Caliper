// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Models;

internal sealed class ProviderStatusService(
    IEnumerable<IModelProvider> providers,
    IOptions<ProvidersOptions> options,
    IProviderCredentialStore credentials,
    IOpenAICodexAuthService codexAuth) : IProviderStatusService
{
    private readonly Dictionary<string, IModelProvider> _providers = providers.ToDictionary(
        provider => provider.Id,
        StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ProviderStatus>> ListAsync(CancellationToken ct)
    {
        var results = new List<ProviderStatus>(_providers.Count);
        foreach (var id in ProviderIds.All)
            results.Add(await GetAsync(id, ct).ConfigureAwait(false));
        return results;
    }

    public async Task<ProviderStatus> GetAsync(string provider, CancellationToken ct)
    {
        if (!_providers.TryGetValue(provider, out var registration))
            throw new InvalidOperationException($"Unknown model provider '{provider}'.");

        if (string.Equals(provider, ProviderIds.OpenAICodex, StringComparison.OrdinalIgnoreCase))
        {
            var status = await codexAuth.GetStatusAsync(ct).ConfigureAwait(false);
            return new ProviderStatus(
                registration.Id,
                registration.DisplayName,
                registration.AuthenticationKind,
                status.IsAuthenticated,
                status.Status,
                status.Account);
        }

        var ready = provider switch
        {
            ProviderIds.OpenRouter => HasSecret(
                ProviderCredentialTargets.OpenRouterApiKey,
                options.Value.OpenRouter.ApiKey),
            ProviderIds.Gemini => HasSecret(
                ProviderCredentialTargets.GeminiApiKey,
                options.Value.Gemini.ApiKey),
            ProviderIds.OpenAI => HasSecret(
                ProviderCredentialTargets.OpenAIApiKey,
                options.Value.OpenAI.ApiKey),
            _ => false,
        };
        return new ProviderStatus(
            registration.Id,
            registration.DisplayName,
            registration.AuthenticationKind,
            ready,
            ready ? "Configured" : "API key required");
    }

    private bool HasSecret(string target, string? configured) =>
        credentials.TryRead(target, out var stored)
            ? !string.IsNullOrWhiteSpace(stored)
            : !string.IsNullOrWhiteSpace(configured);
}
