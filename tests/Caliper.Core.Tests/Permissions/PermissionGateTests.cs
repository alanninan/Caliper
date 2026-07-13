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
    public async Task Denylist_matches_command_boundaries_not_substrings()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Deny, PermissionDecision.Allow);
        var gate = Build(PermissionMode.Auto, prompt);

        // "dd " fires on a real dd command...
        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(Shell("dd if=/dev/zero of=x"), CancellationToken.None));
        Assert.StartsWith(PermissionGate.DenylistReasonPrefix, prompt.Requests[0].Reason, StringComparison.Ordinal);

        // ...but not as a substring inside an unrelated command like "git add .".
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(Shell("git add ."), CancellationToken.None));
        Assert.Null(prompt.Requests[1].Reason);
    }

    [Fact]
    public async Task Auto_does_not_auto_allow_chained_allowlisted_command()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Deny);
        var gate = Build(PermissionMode.Auto, prompt);

        // "git status" is allowlisted, but a chaining operator must not smuggle a second command
        // through on its coattails.
        var decision = await gate.EvaluateAsync(Shell("git status; echo pwned"), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(1, prompt.Count);
    }

    [Fact]
    public async Task Untrusted_readonly_tool_is_not_auto_allowed()
    {
        var untrusted = Request("server__search", SideEffect.ReadOnly) with { TrustedReadOnly = false };

        // AskAlways: an MCP self-declared read-only tool still prompts.
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        Assert.Equal(PermissionDecision.Allow, await Build(PermissionMode.AskAlways, prompt).EvaluateAsync(untrusted, CancellationToken.None));
        Assert.Equal(1, prompt.Count);

        // Plan: it is denied, not silently allowed.
        Assert.Equal(PermissionDecision.Deny, await Build(PermissionMode.Plan, new ScriptedPrompt(PermissionDecision.Allow)).EvaluateAsync(untrusted, CancellationToken.None));

        // A trusted built-in read-only tool is still auto-allowed, even in Plan.
        Assert.Equal(PermissionDecision.Allow, await Build(PermissionMode.Plan, new ScriptedPrompt(PermissionDecision.Deny)).EvaluateAsync(Request("read_file", SideEffect.ReadOnly), CancellationToken.None));
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

    [Fact]
    public async Task Overlay_allowlist_is_honored_when_global_allowlist_does_not_match()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Deny);
        var gate = Build(PermissionMode.Auto, prompt);
        var overlay = new PermissionsOptions
        {
            Mode = PermissionMode.Auto,
            ShellAutoAllowlist = ["my-custom-tool"],
        };
        var request = Shell("my-custom-tool --flag") with { Overlay = overlay };

        var decision = await gate.EvaluateAsync(request, CancellationToken.None);

        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(0, prompt.Count);
    }

    [Fact]
    public async Task Overlay_does_not_bypass_global_denylist()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Deny);
        var gate = Build(PermissionMode.Auto, prompt);
        // The overlay's own denylist is empty and its allowlist would otherwise permit the
        // command, but the global denylist (which bans "rm -rf") must still be merged in.
        var overlay = new PermissionsOptions
        {
            Mode = PermissionMode.Auto,
            ShellAutoAllowlist = ["rm -rf"],
            ShellDenylist = [],
        };
        var request = Shell("rm -rf /tmp/whatever") with { Overlay = overlay };

        var decision = await gate.EvaluateAsync(request, CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(1, prompt.Count);
        Assert.StartsWith(PermissionGate.DenylistReasonPrefix, prompt.Requests[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlay_null_falls_back_to_global_settings()
    {
        var prompt = new ScriptedPrompt(PermissionDecision.Allow);
        var gate = Build(PermissionMode.Plan, prompt);
        var request = Request("write_file", SideEffect.Write);

        // No overlay: Plan mode denies, exactly like the existing global-settings-only path.
        Assert.Equal(PermissionDecision.Deny, await gate.EvaluateAsync(request, CancellationToken.None));
        Assert.Equal(0, prompt.Count);

        // The identical request with an overlay that raises the mode to Auto is honored instead,
        // proving the fallback above wasn't a coincidence of some other short-circuit.
        var overlaid = request with { Overlay = new PermissionsOptions { Mode = PermissionMode.Auto } };
        Assert.Equal(PermissionDecision.Allow, await gate.EvaluateAsync(overlaid, CancellationToken.None));
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
