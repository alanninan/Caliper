// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;

namespace Caliper.Core.Abstractions;

/// <summary>
/// One complete model-provider registration. Authentication, chat transport, catalog, and
/// capability behavior stay together so provider-specific behavior cannot leak into another
/// provider that happens to use a similar wire protocol.
/// </summary>
public interface IModelProvider : IChatClientProvider, IModelCapabilityProvider, IModelCatalog
{
    string Id { get; }
    string DisplayName { get; }
    ProviderAuthenticationKind AuthenticationKind { get; }
}

public enum ProviderAuthenticationKind
{
    ApiKey,
    OAuth,
}

public static class ProviderIds
{
    public const string OpenRouter = "OpenRouter";
    public const string Gemini = "Gemini";
    public const string OpenAI = "OpenAI";
    public const string OpenAICodex = "OpenAICodex";

    public static IReadOnlyList<string> All { get; } =
        [OpenRouter, Gemini, OpenAI, OpenAICodex];
}
