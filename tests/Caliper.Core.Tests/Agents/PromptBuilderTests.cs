// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;

namespace Caliper.Core.Tests.Agents;

public sealed class PromptBuilderTests
{
    [Fact]
    public void Build_includes_explicit_runtime_identity()
    {
        var options = new CaliperOptions
        {
            Provider = ProviderIds.OpenAICodex,
            Model = "configured-model",
        };

        var prompt = PromptBuilder.Build(
            options,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            string.Empty,
            "Answer the user.",
            "gpt-5.6-terra");

        Assert.Contains("Harness: Caliper agent runtime", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Provider: OpenAI Codex (provider ID: OpenAICodex)",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Active model identifier: gpt-5.6-terra",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Active model identifier: configured-model",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "do not claim the model name is unavailable",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Build_without_override_uses_configured_model()
    {
        var options = new CaliperOptions
        {
            Provider = ProviderIds.OpenAI,
            Model = "gpt-5.6-sol",
        };

        var prompt = PromptBuilder.Build(
            options,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            string.Empty,
            "Answer the user.");

        Assert.Contains("Provider: OpenAI Platform (provider ID: OpenAI)", prompt, StringComparison.Ordinal);
        Assert.Contains("Active model identifier: gpt-5.6-sol", prompt, StringComparison.Ordinal);
    }
}
