// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Abstractions;

public interface IOpenAICodexAuthService
{
    Task<OpenAICodexAuthStatus> GetStatusAsync(CancellationToken ct);
    Task<OpenAICodexAuthStatus> LoginWithBrowserAsync(CancellationToken ct);
    Task<OpenAICodexDeviceCode> RequestDeviceCodeAsync(CancellationToken ct);
    Task<OpenAICodexAuthStatus> CompleteDeviceCodeAsync(OpenAICodexDeviceCode code, CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
}

public sealed record OpenAICodexAuthStatus(
    bool IsAuthenticated,
    string Status,
    string? Account = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record OpenAICodexDeviceCode(
    string VerificationUrl,
    string UserCode,
    string DeviceAuthId,
    int IntervalSeconds);
