// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

internal sealed class CaliperOptionsValidator : IValidateOptions<CaliperOptions>
{
    private static readonly string[] s_reasoningEfforts =
        ["none", "low", "medium", "high", "extra-high", "extrahigh"];

    public ValidateOptionsResult Validate(string? name, CaliperOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Provider))
            failures.Add($"{nameof(CaliperOptions.Provider)} must not be empty.");

        if (options.Temperature is < 0 or > 2)
            failures.Add($"{nameof(CaliperOptions.Temperature)} must be between 0 and 2 (was {options.Temperature}).");

        if (!s_reasoningEfforts.Contains(options.Reasoning.Effort, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{nameof(ReasoningOptions.Effort)} must be one of: {string.Join(", ", s_reasoningEfforts)} (was '{options.Reasoning.Effort}').");

        if (string.IsNullOrWhiteSpace(options.Model))
            failures.Add($"{nameof(CaliperOptions.Model)} must not be empty.");

        if (options.MaxSteps <= 0)
            failures.Add($"{nameof(CaliperOptions.MaxSteps)} must be > 0 (was {options.MaxSteps}).");

        if (options.DuplicateCallLimit < 1)
            failures.Add($"{nameof(CaliperOptions.DuplicateCallLimit)} must be >= 1 (was {options.DuplicateCallLimit}).");

        if (options.ToolTimeoutSeconds <= 0)
            failures.Add($"{nameof(CaliperOptions.ToolTimeoutSeconds)} must be > 0 (was {options.ToolTimeoutSeconds}).");

        if (options.ToolMaxRetries < 0)
            failures.Add($"{nameof(CaliperOptions.ToolMaxRetries)} must be >= 0 (was {options.ToolMaxRetries}).");

        if (options.ToolOutputMaxChars <= 0)
            failures.Add($"{nameof(CaliperOptions.ToolOutputMaxChars)} must be > 0 (was {options.ToolOutputMaxChars}).");

        if (options.MaxSurfacedSkills <= 0)
            failures.Add($"{nameof(CaliperOptions.MaxSurfacedSkills)} must be > 0 (was {options.MaxSurfacedSkills}).");

        if (options.Context.CompactAtFraction is <= 0 or >= 1)
            failures.Add($"{nameof(ContextOptions.CompactAtFraction)} must be > 0 and < 1 (was {options.Context.CompactAtFraction}).");

        if (options.Context.KeepRecentTurns <= 0)
            failures.Add($"{nameof(ContextOptions.KeepRecentTurns)} must be > 0 (was {options.Context.KeepRecentTurns}).");

        if (options.Context.ReservedOutputTokens <= 0)
            failures.Add($"{nameof(ContextOptions.ReservedOutputTokens)} must be > 0 (was {options.Context.ReservedOutputTokens}).");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
