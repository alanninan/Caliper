// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Configuration;

public sealed class CaliperOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(CaliperOptions options) =>
        new CaliperOptionsValidator().Validate(null, options);

    [Fact]
    public void Valid_options_pass()
    {
        var result = Validate(new CaliperOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Empty_model_fails()
    {
        var result = Validate(new CaliperOptions { Model = " " });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(CaliperOptions.Model), StringComparison.Ordinal));
    }

    [Fact]
    public void Multiple_violations_are_reported()
    {
        var result = Validate(new CaliperOptions
        {
            Provider = "",
            MaxSteps = 0,
            Context = new ContextOptions { CompactAtFraction = 1, ReservedOutputTokens = 0 },
        });

        Assert.False(result.Succeeded);
        Assert.True((result.Failures?.Count() ?? 0) >= 4);
    }

    [Fact]
    public void Default_subagents_options_pass()
    {
        var result = Validate(new CaliperOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Subagents_MaxDepth_below_one_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.MaxDepth = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.MaxDepth), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_MaxChildrenPerRun_non_positive_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.MaxChildrenPerRun = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.MaxChildrenPerRun), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_TimeoutSeconds_non_positive_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.TimeoutSeconds = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.TimeoutSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_DefaultProfile_missing_from_Profiles_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.DefaultProfile = "does-not-exist";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.DefaultProfile), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_profile_with_no_tools_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.Profiles["research"].EnabledTools = [];

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("research", StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_profile_with_non_positive_MaxSteps_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.Profiles["worker"].MaxSteps = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("worker", StringComparison.Ordinal));
    }
}
