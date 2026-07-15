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

    // A4: when CaliperOptions itself is invalid, IOptions<CaliperOptions>.Value throws
    // OptionsValidationException on access. The cross-read in Validate must not let that
    // exception escape — it should Skip (CaliperOptionsValidator reports the real error).
    [Fact]
    public void Invalid_caliper_options_does_not_throw_and_skips()
    {
        var result = new PermissionsOptionsValidator(new ThrowingOptions())
            .Validate(null, new PermissionsOptions { ShellAutoAllowlist = ["*"] });

        Assert.True(result.Skipped);
    }

    private sealed class ThrowingOptions : IOptions<CaliperOptions>
    {
        public CaliperOptions Value => throw new OptionsValidationException(
            Microsoft.Extensions.Options.Options.DefaultName, typeof(CaliperOptions), ["Model must not be empty."]);
    }
}
