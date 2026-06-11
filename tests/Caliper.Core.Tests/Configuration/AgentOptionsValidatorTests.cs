// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Configuration;

public sealed class AgentOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(AgentOptions options)
    {
        var validator = new AgentOptionsValidator();
        return validator.Validate(null, options);
    }

    private static AgentOptions Valid() => new()
    {
        ModelName             = "local-q1",
        ContextWindowTokens   = 32768,
        MaxOutputTokens       = 1024,
        SafetyMarginTokens    = 512,
        MaxSteps              = 8,
        DuplicateCallLimit    = 2,
        ToolTimeoutSeconds    = 30,
        ToolMaxRetries        = 2,
        MaxSurfacedSkills     = 12,
    };

    [Fact]
    public void Valid_options_pass()
    {
        var result = Validate(Valid());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Empty_model_name_fails()
    {
        var opts = Valid();
        opts.ModelName = "  ";
        var result = Validate(opts);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void MaxSteps_zero_fails()
    {
        var opts = Valid();
        opts.MaxSteps = 0;
        var result = Validate(opts);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Context_window_too_small_fails()
    {
        var opts = Valid();
        // 1000 < 1024 + 512
        opts.ContextWindowTokens = 1000;
        var result = Validate(opts);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void DuplicateCallLimit_zero_fails()
    {
        var opts = Valid();
        opts.DuplicateCallLimit = 0;
        var result = Validate(opts);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Multiple_violations_are_all_reported()
    {
        var opts = Valid();
        opts.ModelName = "";
        opts.MaxSteps  = -1;
        var result = Validate(opts);
        Assert.False(result.Succeeded);
        Assert.True((result.Failures?.Count() ?? 0) >= 2);
    }
}
