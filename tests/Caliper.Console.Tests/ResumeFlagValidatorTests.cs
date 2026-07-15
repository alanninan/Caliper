// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Console.Commands;

namespace Caliper.Console.Tests;

public sealed class ResumeFlagValidatorTests
{
    [Fact]
    public void No_resume_flag_is_always_valid_regardless_of_other_flags()
    {
        Assert.Null(ResumeFlagValidator.Validate(hasResume: false, hasPrompt: true, serve: true));
        Assert.Null(ResumeFlagValidator.Validate(hasResume: false, hasPrompt: false, serve: false));
    }

    [Fact]
    public void Resume_alone_is_valid()
    {
        Assert.Null(ResumeFlagValidator.Validate(hasResume: true, hasPrompt: false, serve: false));
    }

    [Fact]
    public void Resume_with_serve_is_rejected()
    {
        var error = ResumeFlagValidator.Validate(hasResume: true, hasPrompt: false, serve: true);

        Assert.NotNull(error);
        Assert.Contains("--serve", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_with_prompt_is_rejected()
    {
        var error = ResumeFlagValidator.Validate(hasResume: true, hasPrompt: true, serve: false);

        Assert.NotNull(error);
        Assert.Contains("--prompt", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_with_both_prompt_and_serve_is_rejected_reporting_serve_first()
    {
        var error = ResumeFlagValidator.Validate(hasResume: true, hasPrompt: true, serve: true);

        Assert.NotNull(error);
        Assert.Contains("--serve", error, StringComparison.Ordinal);
    }
}
