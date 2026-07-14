// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Configuration;

public sealed class PermissionsOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(PermissionsOptions permissions, ExecutionBackendKind backend) =>
        new PermissionsOptionsValidator(Options.Create(new CaliperOptions { Execution = new ExecutionOptions { Backend = backend } }))
            .Validate(null, permissions);

    [Fact]
    public void Global_wildcard_allowlist_fails_under_Host_backend()
    {
        var result = Validate(new PermissionsOptions { ShellAutoAllowlist = ["*"] }, ExecutionBackendKind.Host);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("wildcard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Global_wildcard_allowlist_passes_under_Container_backend()
    {
        var result = Validate(new PermissionsOptions { ShellAutoAllowlist = ["*"] }, ExecutionBackendKind.Container);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Global_non_wildcard_allowlist_passes_under_Host_backend()
    {
        var result = Validate(new PermissionsOptions { ShellAutoAllowlist = ["git status", "dotnet build"] }, ExecutionBackendKind.Host);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Default_permissions_options_pass()
    {
        var result = Validate(new PermissionsOptions(), ExecutionBackendKind.Host);

        Assert.True(result.Succeeded);
    }
}
