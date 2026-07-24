// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using System.Net;
using System.Text;
using Caliper.Core.Abstractions;
using Caliper.Core.Security;
using Microsoft.Extensions.Time.Testing;

namespace Caliper.Core.Tests.Security;

public sealed class OpenAICodexAuthServiceTests
{
    [Fact]
    public async Task GetAccessTokenAsync_refreshes_expiring_token_and_rotates_refresh_token()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var credentials = ExpiringCredentials(now);
        var handler = new TokenHandler(
            HttpStatusCode.OK,
            """{"access_token":"access-new","refresh_token":"refresh-new","expires_in":3600}""");
        using var service = new OpenAICodexAuthService(
            new StaticAuthHttpClientFactory(handler),
            credentials,
            time);

        var token = await service.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("access-new", token.AccessToken);
        Assert.Equal("refresh-new", token.RefreshToken);
        Assert.Equal("access-new", credentials.Read(ProviderCredentialTargets.OpenAICodexAccessToken));
        Assert.Contains("\"grant_type\":\"refresh_token\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("\"refresh_token\":\"refresh-old\"", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal("caliper", Assert.Single(handler.Request!.Headers.GetValues("originator")));
    }

    [Fact]
    public async Task GetAccessTokenAsync_revoked_refresh_logs_out()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var credentials = ExpiringCredentials(now);
        using var service = new OpenAICodexAuthService(
            new StaticAuthHttpClientFactory(new TokenHandler(
                HttpStatusCode.Unauthorized,
                """{"error":"refresh_token_invalidated"}""")),
            credentials,
            new FakeTimeProvider(now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("Sign in again", error.Message, StringComparison.Ordinal);
        Assert.False(credentials.TryRead(ProviderCredentialTargets.OpenAICodexAccessToken, out _));
        Assert.False(credentials.TryRead(ProviderCredentialTargets.OpenAICodexRefreshToken, out _));
    }

    [Fact]
    public async Task LogoutAsync_removes_all_codex_identity_values()
    {
        var credentials = ExpiringCredentials(
            new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        credentials.Save(ProviderCredentialTargets.OpenAICodexAccountId, "account");
        credentials.Save(ProviderCredentialTargets.OpenAICodexEmail, "developer@example.com");
        using var service = new OpenAICodexAuthService(
            new StaticAuthHttpClientFactory(new TokenHandler(HttpStatusCode.OK, "{}")),
            credentials,
            TimeProvider.System);

        await service.LogoutAsync(CancellationToken.None);

        Assert.Empty(credentials.Values);
    }

    private static AuthCredentialStore ExpiringCredentials(DateTimeOffset now)
    {
        var credentials = new AuthCredentialStore();
        credentials.Save(ProviderCredentialTargets.OpenAICodexAccessToken, "access-old");
        credentials.Save(ProviderCredentialTargets.OpenAICodexRefreshToken, "refresh-old");
        credentials.Save(
            ProviderCredentialTargets.OpenAICodexExpiresAt,
            now.AddMinutes(1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return credentials;
    }
}

file sealed class TokenHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string RequestBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

file sealed class StaticAuthHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class AuthCredentialStore : IProviderCredentialStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public void Save(string targetName, string secret) => Values[targetName] = secret;
    public bool TryRead(string targetName, out string secret) => Values.TryGetValue(targetName, out secret!);
    public void Delete(string targetName) => Values.Remove(targetName);
    public string Read(string targetName) => Values[targetName];
}
