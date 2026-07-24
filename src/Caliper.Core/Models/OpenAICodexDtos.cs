// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;

namespace Caliper.Core.Models;

internal sealed record CodexDeviceCodeRequest(
    [property: JsonPropertyName("client_id")] string ClientId);

internal sealed record CodexDeviceCodeResponse(
    [property: JsonPropertyName("device_auth_id")] string DeviceAuthId,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("interval")] int Interval);

internal sealed record CodexDeviceTokenRequest(
    [property: JsonPropertyName("device_auth_id")] string DeviceAuthId,
    [property: JsonPropertyName("user_code")] string UserCode);

internal sealed record CodexDeviceTokenResponse(
    [property: JsonPropertyName("authorization_code")] string AuthorizationCode,
    [property: JsonPropertyName("code_verifier")] string CodeVerifier,
    [property: JsonPropertyName("code_challenge")] string? CodeChallenge);

internal sealed record CodexRefreshRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

internal sealed record CodexTokenResponse(
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

internal sealed record CodexModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<CodexModel>? Models);

internal sealed record CodexModel(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("context_window")] int? ContextWindow,
    [property: JsonPropertyName("supported_reasoning_levels")] IReadOnlyList<CodexReasoningLevel>? SupportedReasoningLevels);

internal sealed record CodexReasoningLevel(
    [property: JsonPropertyName("effort")] string Effort);
