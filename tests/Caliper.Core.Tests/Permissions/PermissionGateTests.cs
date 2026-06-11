// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Permissions;

public sealed class PermissionGateTests
{
    [Fact]
    public async Task AskAlways_prompts_for_write_but_not_readonly()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var gate = Build(PermissionMode.AskAlways, prompt);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Request("read_file", SideEffect.ReadOnly), CancellationToken.None));
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Request("write_file", SideEffect.Write), CancellationToken.None));
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public async Task Auto_shell_allowlist_runs_without_prompt_and_unknown_prompts()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var gate = Build(PermissionMode.Auto, prompt);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Shell("dotnet test tests"), CancellationToken.None));
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Shell("echo hello"), CancellationToken.None));
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public async Task Auto_execute_tool_without_shell_command_prompts()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var gate = Build(PermissionMode.Auto, prompt);
        var request = new PermissionRequest(
            "server__destructive",
            SideEffect.Execute,
            JsonDocument.Parse("""{"target":"value"}""").RootElement.Clone(),
            null);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(request, CancellationToken.None));
        Assert.Equal(1, prompt.Count);
        Assert.Equal("server__destructive", prompt.Requests[0].Tool);
    }

    [Fact]
    public async Task Auto_shell_denylist_never_remembers_session_approval()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.AllowForSession, PermissionDecision.Deny);
        var gate = Build(PermissionMode.Auto, prompt);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Shell("Remove-Item -Recurse temp"), CancellationToken.None));
        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(Shell("Remove-Item -Recurse temp"), CancellationToken.None));
        Assert.Equal(2, prompt.Count);
        Assert.All(prompt.Requests, request => Assert.StartsWith(PermissionGate.DenylistReasonPrefix, request.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskAlways_shell_denylist_never_remembers_session_approval()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.AllowForSession, PermissionDecision.Deny);
        var gate = Build(PermissionMode.AskAlways, prompt);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Shell("Remove-Item -Recurse temp"), CancellationToken.None));
        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(Shell("Remove-Item -Recurse temp"), CancellationToken.None));
        Assert.Equal(2, prompt.Count);
        Assert.All(prompt.Requests, request => Assert.StartsWith(PermissionGate.DenylistReasonPrefix, request.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_denies_side_effects_and_allows_readonly()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var gate = Build(PermissionMode.Plan, prompt);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Request("read_file", SideEffect.ReadOnly), CancellationToken.None));
        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(Request("write_file", SideEffect.Write), CancellationToken.None));
        Assert.Equal(0, prompt.Count);
    }

    [Fact]
    public async Task AllowForSession_suppresses_second_prompt()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.AllowForSession);
        var gate = Build(PermissionMode.AskAlways, prompt);
        var request = Request("write_file", SideEffect.Write);

        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(request, CancellationToken.None));
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(request, CancellationToken.None));
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public async Task Runtime_mode_change_affects_next_evaluation()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var services = new ServiceCollection()
            .AddSingleton<IPermissionPrompt>(prompt)
            .BuildServiceProvider();
        var settings = new RuntimeSettings(
            Options.Create(new CaliperOptions { WorkingRoot = "." }),
            Options.Create(new PermissionsOptions { Mode = PermissionMode.Plan }));
        var gate = new PermissionGate(settings, services);

        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(Request("write_file", SideEffect.Write), CancellationToken.None));

        settings.SetPermissionMode(PermissionMode.AskAlways);
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Request("write_file", SideEffect.Write), CancellationToken.None));
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public async Task Auto_outside_working_root_file_access_prompts_unless_root_is_configured()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "caliper-root-" + Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "caliper-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var outsideFile = Path.Combine(outsideRoot, "note.txt");
            var prompt = new ScriptedPrompt(PermissionDecision.Allow);
            var gated = Build(PermissionMode.Auto, prompt, workingRoot: tempRoot);

            Assert.Equal(PermissionDecision.Allow, await gated.EvaluateAsync(FileRead(outsideFile), CancellationToken.None));
            Assert.Equal(1, prompt.Count);

            var configured = Build(PermissionMode.Auto, new ScriptedPrompt(PermissionDecision.Deny), workingRoot: tempRoot, autoRoots: [outsideRoot]);
            Assert.Equal(PermissionDecision.Allow, await configured.EvaluateAsync(FileRead(outsideFile), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    private static PermissionGate Build(
        PermissionMode mode,
        IPermissionPrompt prompt,
        string workingRoot = ".",
        string[]? autoRoots = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(prompt)
            .BuildServiceProvider();
        return new PermissionGate(
            new RuntimeSettings(
                Options.Create(new CaliperOptions { WorkingRoot = workingRoot }),
                Options.Create(new PermissionsOptions
                {
                    Mode = mode,
                    AutoAllowFileRoots = autoRoots ?? [],
                })),
            services);
    }

    private static PermissionRequest Request(string tool, SideEffect effect) =>
        new(tool, effect, JsonDocument.Parse("""{"path":"file.txt"}""").RootElement.Clone(), null);

    private static PermissionRequest Shell(string command) =>
        new("powershell", SideEffect.Execute, JsonSerializer.SerializeToElement(new { command }), null);

    private static PermissionRequest FileRead(string path) =>
        new("read_file", SideEffect.ReadOnly, JsonSerializer.SerializeToElement(new { path }), null);
}

file sealed class ScriptedPrompt(params PermissionDecision[] decisions) : IPermissionPrompt
{
    private readonly Queue<PermissionDecision> _decisions = new(decisions);
    public int Count { get; private set; }
    public List<PermissionRequest> Requests { get; } = [];

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Count++;
        Requests.Add(request);
        return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : PermissionDecision.Deny);
    }
}
