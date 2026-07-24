// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;

namespace Caliper.Core.Tests.Configuration;

public sealed class ProvidersOptionsValidatorTests
{
    [Fact]
    public void Validate_defaults_succeed()
    {
        var result = new ProvidersOptionsValidator().Validate(null, new ProvidersOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_rejects_non_http_provider_endpoint()
    {
        var options = new ProvidersOptions
        {
            OpenAI = new OpenAIOptions { Endpoint = "file:///tmp/openai" },
        };

        var result = new ProvidersOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("Providers.OpenAI.Endpoint", StringComparison.Ordinal));
    }
}
