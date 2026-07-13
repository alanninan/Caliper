// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Console.Commands;
using Caliper.Core.Configuration;

namespace Caliper.Console.Tests;

public sealed class OneShotPermissionResolverTests
{
    [Fact]
    public void Unattended_without_prompt_is_rejected()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: true, hasPrompt: false, permissionMode: null);

        Assert.False(plan.IsValid);
        Assert.Contains("--prompt", plan.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Attended_prompt_without_explicit_permissions_defaults_to_readonly_Plan()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: false, hasPrompt: true, permissionMode: null);

        Assert.True(plan.IsValid);
        Assert.True(plan.ReadOnlyToolsOnly);
        Assert.Equal(nameof(PermissionMode.Plan), plan.ForcedPermissionMode);
    }

    [Fact]
    public void Unattended_without_explicit_permissions_forces_Auto_and_full_tool_surface()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: true, hasPrompt: true, permissionMode: null);

        Assert.True(plan.IsValid);
        Assert.False(plan.ReadOnlyToolsOnly);
        Assert.Equal(nameof(PermissionMode.Auto), plan.ForcedPermissionMode);
    }

    [Fact]
    public void Unattended_with_explicit_Plan_keeps_Plan_more_restrictive_wins()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: true, hasPrompt: true, permissionMode: "Plan");

        Assert.True(plan.IsValid);
        Assert.False(plan.ReadOnlyToolsOnly);
        Assert.Equal("Plan", plan.ForcedPermissionMode);
    }

    [Fact]
    public void Unattended_with_explicit_AskAlways_is_honored_not_overridden_to_Auto()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: true, hasPrompt: true, permissionMode: "AskAlways");

        Assert.True(plan.IsValid);
        Assert.False(plan.ReadOnlyToolsOnly);
        Assert.Equal("AskAlways", plan.ForcedPermissionMode);
    }

    [Fact]
    public void Attended_prompt_with_explicit_permissions_does_not_force_readonly_tools()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: false, hasPrompt: true, permissionMode: "Auto");

        Assert.True(plan.IsValid);
        Assert.False(plan.ReadOnlyToolsOnly);
        Assert.Equal("Auto", plan.ForcedPermissionMode);
    }

    [Fact]
    public void No_prompt_and_not_unattended_forces_no_override_for_the_interactive_REPL()
    {
        var plan = OneShotPermissionResolver.Resolve(unattended: false, hasPrompt: false, permissionMode: null);

        Assert.True(plan.IsValid);
        Assert.False(plan.ReadOnlyToolsOnly);
        Assert.Null(plan.ForcedPermissionMode);
    }
}
