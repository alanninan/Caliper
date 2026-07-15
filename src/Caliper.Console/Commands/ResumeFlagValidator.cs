// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Console.Commands;

/// <summary>
/// Validates how <c>--resume</c> composes with the existing one-shot/serve flags (roadmap §3.4).
/// Pure and side-effect free so the composition rule is unit-testable without building the host,
/// matching <see cref="OneShotPermissionResolver"/>'s pattern.
/// </summary>
public static class ResumeFlagValidator
{
    /// <summary>
    /// <c>--resume</c> is one-shot style itself (resume, print, exit), so it cannot be combined with
    /// <c>--prompt</c> (which one-shot run would even run?) or <c>--serve</c> (the headless scheduler
    /// host has no single run to resume — it ticks jobs). It composes freely with <c>--print</c> and
    /// with no other flag at all. Returns null when valid, or a human-readable error otherwise.
    /// </summary>
    public static string? Validate(bool hasResume, bool hasPrompt, bool serve)
    {
        if (!hasResume)
            return null;

        if (serve)
            return "--resume cannot be combined with --serve.";
        if (hasPrompt)
            return "--resume cannot be combined with --prompt.";

        return null;
    }
}
