// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Models;
using Caliper.Core.Protocol;

namespace Caliper.Core.Security;

internal sealed class OpenAICodexAuthService(
    IHttpClientFactory httpClientFactory,
    IProviderCredentialStore credentials,
    TimeProvider timeProvider) : IOpenAICodexAuthService, IDisposable
{
    internal const string HttpClientName = "openai-codex-auth";
    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal const string Issuer = "https://auth.openai.com";
    private const int CallbackPort = 1455;
    private static readonly TimeSpan s_loginTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_deviceTimeout = TimeSpan.FromMinutes(15);

    public Task<OpenAICodexAuthStatus> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryReadToken(out var token))
            return Task.FromResult(new OpenAICodexAuthStatus(false, "Signed out"));

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(token.ExpiresAtUnixMs);
        var account = credentials.TryRead(ProviderCredentialTargets.OpenAICodexEmail, out var email)
            ? email
            : null;
        var status = expiresAt <= timeProvider.GetUtcNow() ? "Refresh required" : "Signed in";
        return Task.FromResult(new OpenAICodexAuthStatus(true, status, account, expiresAt));
    }

    public async Task<OpenAICodexAuthStatus> LoginWithBrowserAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirectUri = $"http://localhost:{CallbackPort}/auth/callback";
        var authorizeUrl = BuildAuthorizeUrl(redirectUri, challenge, state);

        var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        listener.Start();
        try
        {
            OpenBrowser(authorizeUrl);
            using var client = await listener.AcceptTcpClientAsync(ct)
                .AsTask()
                .WaitAsync(s_loginTimeout, timeProvider, ct)
                .ConfigureAwait(false);
            var callback = await ReadCallbackAsync(client, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                await TryWriteBrowserResponseAsync(client, success: false, ct).ConfigureAwait(false);
                throw new InvalidOperationException($"OpenAI sign-in failed: {callback.Error}.");
            }
            if (string.IsNullOrWhiteSpace(callback.Code) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(callback.State ?? string.Empty)))
            {
                await TryWriteBrowserResponseAsync(client, success: false, ct).ConfigureAwait(false);
                throw new InvalidOperationException("OpenAI sign-in callback validation failed.");
            }

            try
            {
                var tokens = await ExchangeCodeAsync(callback.Code, redirectUri, verifier, ct).ConfigureAwait(false);
                SaveTokens(tokens, existingRefreshToken: null);
            }
            catch
            {
                await TryWriteBrowserResponseAsync(client, success: false, ct).ConfigureAwait(false);
                throw;
            }

            await TryWriteBrowserResponseAsync(client, success: true, ct).ConfigureAwait(false);
            return await GetStatusAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<OpenAICodexDeviceCode> RequestDeviceCodeAsync(CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Issuer}/api/accounts/deviceauth/usercode")
        {
            Content = JsonContent.Create(
                new CodexDeviceCodeRequest(ClientId),
                CaliperJsonContext.Default.CodexDeviceCodeRequest),
        };
        AddClientHeaders(request);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("OpenAI device-code login is unavailable. Use browser sign-in.");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(
            CaliperJsonContext.Default.CodexDeviceCodeResponse,
            ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI device-code response was empty.");
        return new OpenAICodexDeviceCode(
            $"{Issuer}/codex/device",
            payload.UserCode,
            payload.DeviceAuthId,
            Math.Max(payload.Interval, 1));
    }

    public async Task<OpenAICodexAuthStatus> CompleteDeviceCodeAsync(
        OpenAICodexDeviceCode code,
        CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + s_deviceTimeout;
        using var client = httpClientFactory.CreateClient(HttpClientName);
        while (timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{Issuer}/api/accounts/deviceauth/token")
            {
                Content = JsonContent.Create(
                    new CodexDeviceTokenRequest(code.DeviceAuthId, code.UserCode),
                    CaliperJsonContext.Default.CodexDeviceTokenRequest),
            };
            AddClientHeaders(request);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync(
                    CaliperJsonContext.Default.CodexDeviceTokenResponse,
                    ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("OpenAI device-code token response was empty.");
                var tokens = await ExchangeCodeAsync(
                    payload.AuthorizationCode,
                    $"{Issuer}/deviceauth/callback",
                    payload.CodeVerifier,
                    ct).ConfigureAwait(false);
                SaveTokens(tokens, existingRefreshToken: null);
                return await GetStatusAsync(ct).ConfigureAwait(false);
            }

            if (response.StatusCode is not HttpStatusCode.Forbidden and not HttpStatusCode.NotFound)
                throw new HttpRequestException(
                    $"OpenAI device-code polling failed with HTTP {(int)response.StatusCode}.");

            await Task.Delay(TimeSpan.FromSeconds(code.IntervalSeconds), timeProvider, ct)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("OpenAI device-code login expired before authorization completed.");
    }

    public Task LogoutAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        credentials.Delete(ProviderCredentialTargets.OpenAICodexAccessToken);
        credentials.Delete(ProviderCredentialTargets.OpenAICodexRefreshToken);
        credentials.Delete(ProviderCredentialTargets.OpenAICodexExpiresAt);
        credentials.Delete(ProviderCredentialTargets.OpenAICodexAccountId);
        credentials.Delete(ProviderCredentialTargets.OpenAICodexEmail);
        return Task.CompletedTask;
    }

    internal async Task<CodexAccessToken> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!TryReadToken(out var token))
            throw new InvalidOperationException(
                "OpenAI Codex is signed out. Sign in with ChatGPT before using this provider.");

        if (DateTimeOffset.FromUnixTimeMilliseconds(token.ExpiresAtUnixMs) >
            timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2))
        {
            return token;
        }

        return await RefreshAsync(token, ct).ConfigureAwait(false);
    }

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private async Task<CodexAccessToken> RefreshAsync(CodexAccessToken observed, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryReadToken(out var latest) &&
                (latest.AccessToken != observed.AccessToken ||
                 DateTimeOffset.FromUnixTimeMilliseconds(latest.ExpiresAtUnixMs) >
                 timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2)))
            {
                return latest;
            }

            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
            {
                Content = JsonContent.Create(
                    new CodexRefreshRequest(ClientId, "refresh_token", observed.RefreshToken),
                    CaliperJsonContext.Default.CodexRefreshRequest),
            };
            AddClientHeaders(request);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    body.Contains("refresh_token_expired", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("refresh_token_reused", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("refresh_token_invalidated", StringComparison.OrdinalIgnoreCase))
                {
                    await LogoutAsync(ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "OpenAI Codex sign-in expired or was revoked. Sign in again.");
                }

                response.EnsureSuccessStatusCode();
            }

            var tokens = await response.Content.ReadFromJsonAsync(
                CaliperJsonContext.Default.CodexTokenResponse,
                ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OpenAI token refresh response was empty.");
            SaveTokens(tokens, observed.RefreshToken);
            return ReadRequiredToken();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CodexTokenResponse> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string verifier,
        CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
            }),
        };
        AddClientHeaders(request);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            CaliperJsonContext.Default.CodexTokenResponse,
            ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OpenAI token exchange response was empty.");
    }

    private void SaveTokens(CodexTokenResponse tokens, string? existingRefreshToken)
    {
        var accessToken = tokens.AccessToken
            ?? throw new InvalidOperationException("OpenAI token response did not include an access token.");
        var refreshToken = tokens.RefreshToken ?? existingRefreshToken
            ?? throw new InvalidOperationException("OpenAI token response did not include a refresh token.");
        var claims = ReadJwtClaims(tokens.IdToken) ?? ReadJwtClaims(accessToken);
        var expiry = ResolveExpiry(tokens, claims);

        credentials.Save(ProviderCredentialTargets.OpenAICodexRefreshToken, refreshToken);
        credentials.Save(ProviderCredentialTargets.OpenAICodexAccessToken, accessToken);
        credentials.Save(
            ProviderCredentialTargets.OpenAICodexExpiresAt,
            expiry.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        var accountId = ReadAccountId(claims);
        if (!string.IsNullOrWhiteSpace(accountId))
            credentials.Save(ProviderCredentialTargets.OpenAICodexAccountId, accountId);
        var email = ReadString(claims, "email");
        if (!string.IsNullOrWhiteSpace(email))
            credentials.Save(ProviderCredentialTargets.OpenAICodexEmail, email);
    }

    private bool TryReadToken(out CodexAccessToken token)
    {
        token = default!;
        if (!credentials.TryRead(ProviderCredentialTargets.OpenAICodexAccessToken, out var access) ||
            !credentials.TryRead(ProviderCredentialTargets.OpenAICodexRefreshToken, out var refresh) ||
            !credentials.TryRead(ProviderCredentialTargets.OpenAICodexExpiresAt, out var expiresText) ||
            !long.TryParse(expiresText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        credentials.TryRead(ProviderCredentialTargets.OpenAICodexAccountId, out var accountId);
        token = new CodexAccessToken(access, refresh, expires, accountId);
        return true;
    }

    private CodexAccessToken ReadRequiredToken() =>
        TryReadToken(out var token)
            ? token
            : throw new InvalidOperationException("OpenAI Codex credentials could not be read after refresh.");

    private DateTimeOffset ResolveExpiry(CodexTokenResponse tokens, JsonElement? claims)
    {
        if (tokens.ExpiresIn is > 0)
            return timeProvider.GetUtcNow() + TimeSpan.FromSeconds(tokens.ExpiresIn.Value);
        if (claims is { } value &&
            value.TryGetProperty("exp", out var exp) &&
            exp.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return timeProvider.GetUtcNow() + TimeSpan.FromHours(1);
    }

    private static JsonElement? ReadJwtClaims(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(PadBase64(parts[1].Replace('-', '+').Replace('_', '/')));
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadAccountId(JsonElement? claims)
    {
        var direct = ReadString(claims, "chatgpt_account_id");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        if (claims is { } value &&
            value.TryGetProperty("https://api.openai.com/auth", out var auth))
        {
            var nested = ReadString(auth, "chatgpt_account_id");
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }

    private static string? ReadString(JsonElement? element, string property) =>
        element is { } value &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var result) &&
        result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static string BuildAuthorizeUrl(string redirectUri, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "caliper",
        };
        return $"{Issuer}/oauth/authorize?{string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Could not open the browser. Open this URL manually: {url}",
                ex);
        }
    }

    private static async Task<OAuthCallback> ReadCallbackAsync(TcpClient client, CancellationToken ct)
    {
        using var reader = new StreamReader(
            client.GetStream(),
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine))
            return new OAuthCallback(null, null, "empty callback");
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 })
        {
            // Drain the browser's HTTP headers before writing the response on the same stream.
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2 ||
            !Uri.TryCreate($"http://localhost{parts[1]}", UriKind.Absolute, out var uri) ||
            !string.Equals(uri.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
        {
            return new OAuthCallback(null, null, "invalid callback path");
        }

        var query = ParseQuery(uri.Query);
        return new OAuthCallback(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("state"),
            query.GetValueOrDefault("error_description") ?? query.GetValueOrDefault("error"));
    }

    private static async Task WriteBrowserResponseAsync(
        TcpClient client,
        bool success,
        CancellationToken ct)
    {
        var body = success
            ? "<html><body><h1>Signed in to Caliper</h1><p>You can close this window.</p></body></html>"
            : "<html><body><h1>Caliper sign-in failed</h1><p>Return to Caliper for details.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task TryWriteBrowserResponseAsync(
        TcpClient client,
        bool success,
        CancellationToken ct)
    {
        try
        {
            await WriteBrowserResponseAsync(client, success, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or SocketException or ObjectDisposedException)
        {
            // Authentication state is authoritative. A browser closing the callback tab early
            // must not turn a successful token exchange into an application-level failure.
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] =
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }
        return values;
    }

    private static void AddClientHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("originator", "caliper");
        request.Headers.TryAddWithoutValidation("User-Agent", "caliper/1.0");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string PadBase64(string value) =>
        value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');

    private sealed record OAuthCallback(string? Code, string? State, string? Error);

    public void Dispose() => _refreshGate.Dispose();
}

internal sealed record CodexAccessToken(
    string AccessToken,
    string RefreshToken,
    long ExpiresAtUnixMs,
    string? AccountId);
