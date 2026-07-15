// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Console.Commands;

namespace Caliper.Console.Tests;

public sealed class OneShotExitCodeTests
{
    [Fact]
    public void From_clean_run_returns_success() =>
        Assert.Equal(OneShotExitCode.Success, OneShotExitCode.From(error: null, denialCount: 0, reportDenialsInExitCode: true));

    [Fact]
    public void From_error_returns_run_error() =>
        Assert.Equal(OneShotExitCode.RunError, OneShotExitCode.From("boom", denialCount: 0, reportDenialsInExitCode: true));

    [Fact]
    public void From_denials_under_unattended_returns_actions_denied() =>
        Assert.Equal(OneShotExitCode.ActionsDenied, OneShotExitCode.From(error: null, denialCount: 3, reportDenialsInExitCode: true));

    [Fact]
    public void From_denials_on_attended_run_returns_success() =>
        Assert.Equal(OneShotExitCode.Success, OneShotExitCode.From(error: null, denialCount: 3, reportDenialsInExitCode: false));

    [Fact]
    public void From_error_wins_over_denials() =>
        Assert.Equal(OneShotExitCode.RunError, OneShotExitCode.From("boom", denialCount: 3, reportDenialsInExitCode: true));
}
