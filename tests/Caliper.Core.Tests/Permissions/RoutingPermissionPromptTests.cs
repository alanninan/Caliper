// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Events;
using Caliper.Core.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caliper.Core.Tests.Permissions;

public sealed class RoutingPermissionPromptTests
{
    [Fact]
    public async Task AskAsync_unattended_request_is_denied_without_reaching_the_interactive_prompt()
    {
        var interactive = new RecordingPrompt(PermissionDecision.Allow);
        var prompt = new RoutingPermissionPrompt(
            interactive,
            new UnattendedPermissionPrompt(NullLogger<UnattendedPermissionPrompt>.Instance));

        var decision = await prompt.AskAsync(
            Request() with { Unattended = true },
            CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(0, interactive.Count);
    }

    [Fact]
    public async Task AskAsync_attended_request_reaches_the_interactive_prompt()
    {
        var interactive = new RecordingPrompt(PermissionDecision.AllowForSession);
        var prompt = new RoutingPermissionPrompt(
            interactive,
            new UnattendedPermissionPrompt(NullLogger<UnattendedPermissionPrompt>.Instance));

        var decision = await prompt.AskAsync(Request(), CancellationToken.None);

        Assert.Equal(PermissionDecision.AllowForSession, decision);
        Assert.Equal(1, interactive.Count);
    }

    private static PermissionRequest Request() =>
        new("write_file", SideEffect.Write, JsonDocument.Parse("""{"path":"file.txt"}""").RootElement.Clone(), null);
}

file sealed class RecordingPrompt(PermissionDecision decision) : IPermissionPrompt
{
    public int Count { get; private set; }

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Count++;
        return Task.FromResult(decision);
    }
}
