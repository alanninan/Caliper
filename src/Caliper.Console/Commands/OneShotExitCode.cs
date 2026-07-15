// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Console.Commands;

/// <summary>
/// Process exit codes for the non-interactive entry points (<c>--prompt</c>, <c>--unattended</c>,
/// <c>--resume</c>), so cron/CI automation can distinguish outcomes without parsing stderr:
/// <c>0</c> clean, <c>1</c> the run reported an error, <c>2</c> the run completed but one or more
/// actions were denied (audit follow-up on §3.2a's deny+report contract — the report now includes
/// the exit code). Pure so the mapping is unit-testable without building the host.
/// </summary>
public static class OneShotExitCode
{
    public const int Success = 0;
    public const int RunError = 1;
    public const int ActionsDenied = 2;

    /// <summary>
    /// <paramref name="reportDenialsInExitCode"/> is true for unattended one-shots and resumes
    /// (no human saw the denials happen); an attended one-shot keeps exit 0 on denials — a human
    /// interactively denied those actions, which is an expected outcome, not a signal.
    /// An error always wins over denials: a failed run may also have denials, but automation
    /// should see the failure first.
    /// </summary>
    public static int From(string? error, int denialCount, bool reportDenialsInExitCode) =>
        error is not null ? RunError
        : reportDenialsInExitCode && denialCount > 0 ? ActionsDenied
        : Success;
}
