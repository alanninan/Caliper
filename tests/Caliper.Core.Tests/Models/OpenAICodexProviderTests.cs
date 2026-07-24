// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Caliper.Core.Tests.Models;

#pragma warning disable OPENAI001 // Verifies the experimental Responses adapter used by the provider.
public sealed class OpenAICodexProviderTests
{
    [Fact]
    public void ConfigureCodexOptions_removes_unsupported_generation_fields()
    {
        var options = new ChatOptions
        {
            Temperature = 0,
            TopP = 0.9f,
            Seed = 42,
            MaxOutputTokens = 8_192,
        };

        OpenAICodexProvider.ConfigureCodexOptions(options);

        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
        Assert.Null(options.Seed);
        Assert.Null(options.MaxOutputTokens);
    }

    [Fact]
    public void ConfigureCodexOptions_uses_stateless_encrypted_reasoning()
    {
        var options = new ChatOptions();

        OpenAICodexProvider.ConfigureCodexOptions(options);
        var raw = Assert.IsType<CreateResponseOptions>(
            options.RawRepresentationFactory!(null!));

        Assert.False(raw.StoredOutputEnabled);
        Assert.Contains(
            IncludedResponseProperty.ReasoningEncryptedContent,
            raw.IncludedProperties);
    }

    [Fact]
    public void ConfigureCodexOptions_preserves_existing_response_options()
    {
        var existing = new CreateResponseOptions
        {
            Instructions = "Keep this instruction.",
        };
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ => existing,
        };

        OpenAICodexProvider.ConfigureCodexOptions(options);
        var raw = Assert.IsType<CreateResponseOptions>(
            options.RawRepresentationFactory!(null!));

        Assert.Same(existing, raw);
        Assert.Equal("Keep this instruction.", raw.Instructions);
        Assert.False(raw.StoredOutputEnabled);
    }

    [Fact]
    public void MoveSystemMessagesToInstructions_builds_required_codex_shape()
    {
        var messages = new[]
        {
            new AIChatMessage(AIChatRole.System, "System prompt"),
            new AIChatMessage(AIChatRole.User, "Hello"),
        };
        var options = new ChatOptions
        {
            Instructions = "Per-turn instruction",
        };

        var input = OpenAICodexProvider.MoveSystemMessagesToInstructions(messages, options);

        var message = Assert.Single(input);
        Assert.Equal(AIChatRole.User, message.Role);
        Assert.Equal(
            $"System prompt{Environment.NewLine}Per-turn instruction",
            options.Instructions);
    }

    [Fact]
    public void MoveSystemMessagesToInstructions_without_system_uses_nonempty_fallback()
    {
        var options = new ChatOptions();

        var input = OpenAICodexProvider.MoveSystemMessagesToInstructions(
            [new AIChatMessage(AIChatRole.User, "Hello")],
            options);

        Assert.Single(input);
        Assert.False(string.IsNullOrWhiteSpace(options.Instructions));
    }
}
#pragma warning restore OPENAI001
