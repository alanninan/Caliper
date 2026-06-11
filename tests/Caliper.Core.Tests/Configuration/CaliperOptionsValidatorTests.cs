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
}
