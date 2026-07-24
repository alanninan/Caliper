// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Models;

public sealed class ProviderStatusServiceTests
{
    [Fact]
    public async Task ListAsync_reports_all_four_providers_in_stable_order()
    {
        var credentials = new StatusCredentialStore();
        credentials.Save(ProviderCredentialTargets.OpenRouterApiKey, "router-key");
        credentials.Save(ProviderCredentialTargets.OpenAIApiKey, "openai-key");
        var service = new ProviderStatusService(
            ProviderIds.All.Select(id => new StatusModelProvider(
                id,
                id == ProviderIds.OpenAICodex
                    ? ProviderAuthenticationKind.OAuth
                    : ProviderAuthenticationKind.ApiKey)),
            Options.Create(new ProvidersOptions
            {
                Gemini = new GeminiOptions { ApiKey = "gemini-key" },
            }),
            credentials,
            new StatusCodexAuthService(new(true, "Signed in", "developer@example.com")));

        var statuses = await service.ListAsync(CancellationToken.None);

        Assert.Equal(ProviderIds.All, statuses.Select(status => status.Id));
        Assert.All(statuses, status => Assert.True(status.IsReady));
        Assert.Equal("developer@example.com", statuses[^1].Account);
    }
}

file sealed class StatusModelProvider(
    string id,
    ProviderAuthenticationKind authenticationKind) : IModelProvider
{
    public string Id => id;
    public string DisplayName => id;
    public ProviderAuthenticationKind AuthenticationKind => authenticationKind;
    public IChatClient GetClient(string modelSlug) => null!;
    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(new ModelCapabilities(true, false, false, 1));
    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);
    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct) =>
        ListAsync(ct);
}

file sealed class StatusCodexAuthService(OpenAICodexAuthStatus status) : IOpenAICodexAuthService
{
    public Task<OpenAICodexAuthStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(status);
    public Task<OpenAICodexAuthStatus> LoginWithBrowserAsync(CancellationToken ct) => Task.FromResult(status);
    public Task<OpenAICodexDeviceCode> RequestDeviceCodeAsync(CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<OpenAICodexAuthStatus> CompleteDeviceCodeAsync(
        OpenAICodexDeviceCode code,
        CancellationToken ct) => throw new NotSupportedException();
    public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class StatusCredentialStore : IProviderCredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Save(string targetName, string secret) => _values[targetName] = secret;
    public bool TryRead(string targetName, out string secret) => _values.TryGetValue(targetName, out secret!);
    public void Delete(string targetName) => _values.Remove(targetName);
}
