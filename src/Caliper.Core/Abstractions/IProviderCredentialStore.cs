// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Abstractions;

/// <summary>
/// Host-neutral storage for provider secrets. Desktop hosts can use an OS credential manager;
/// the console falls back to a hardened user-local file store.
/// </summary>
public interface IProviderCredentialStore
{
    void Save(string targetName, string secret);
    bool TryRead(string targetName, out string secret);
    void Delete(string targetName);
}

public static class ProviderCredentialTargets
{
    public const string OpenRouterApiKey = "Caliper/Providers/OpenRouter/ApiKey";
    public const string GeminiApiKey = "Caliper/Providers/Gemini/ApiKey";
    public const string OpenAIApiKey = "Caliper/Providers/OpenAI/ApiKey";
    public const string OpenAICodexAccessToken = "Caliper/Providers/OpenAICodex/AccessToken";
    public const string OpenAICodexRefreshToken = "Caliper/Providers/OpenAICodex/RefreshToken";
    public const string OpenAICodexExpiresAt = "Caliper/Providers/OpenAICodex/ExpiresAt";
    public const string OpenAICodexAccountId = "Caliper/Providers/OpenAICodex/AccountId";
    public const string OpenAICodexEmail = "Caliper/Providers/OpenAICodex/Email";
}
