// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

internal sealed class ProvidersOptionsValidator : IValidateOptions<ProvidersOptions>
{
    public ValidateOptionsResult Validate(string? name, ProvidersOptions options)
    {
        var failures = new List<string>();
        ValidateEndpoint("Providers.OpenRouter.Endpoint", options.OpenRouter.Endpoint, failures);
        ValidateEndpoint("Providers.Gemini.Endpoint", options.Gemini.Endpoint, failures);
        ValidateEndpoint("Providers.OpenAI.Endpoint", options.OpenAI.Endpoint, failures);
        ValidateEndpoint("Providers.OpenAICodex.Endpoint", options.OpenAICodex.Endpoint, failures);
        if (string.IsNullOrWhiteSpace(options.OpenRouter.AppTitle))
            failures.Add("Providers.OpenRouter.AppTitle must not be empty.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEndpoint(string name, string value, List<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            failures.Add($"{name} must be an absolute HTTP or HTTPS URL.");
        }
    }
}
