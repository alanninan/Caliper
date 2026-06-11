// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

internal sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelName))
            failures.Add($"{nameof(AgentOptions.ModelName)} must not be empty.");

        if (options.MaxSteps <= 0)
            failures.Add($"{nameof(AgentOptions.MaxSteps)} must be > 0 (was {options.MaxSteps}).");

        if (options.ContextWindowTokens < options.MaxOutputTokens + options.SafetyMarginTokens)
            failures.Add(
                $"{nameof(AgentOptions.ContextWindowTokens)} ({options.ContextWindowTokens}) must be " +
                $">= {nameof(AgentOptions.MaxOutputTokens)} ({options.MaxOutputTokens}) + " +
                $"{nameof(AgentOptions.SafetyMarginTokens)} ({options.SafetyMarginTokens}).");

        if (options.DuplicateCallLimit < 1)
            failures.Add($"{nameof(AgentOptions.DuplicateCallLimit)} must be >= 1 (was {options.DuplicateCallLimit}).");

        if (options.ToolTimeoutSeconds <= 0)
            failures.Add($"{nameof(AgentOptions.ToolTimeoutSeconds)} must be > 0 (was {options.ToolTimeoutSeconds}).");

        if (options.ToolMaxRetries < 0)
            failures.Add($"{nameof(AgentOptions.ToolMaxRetries)} must be >= 0 (was {options.ToolMaxRetries}).");

        if (options.ToolOutputMaxChars <= 0)
            failures.Add($"{nameof(AgentOptions.ToolOutputMaxChars)} must be > 0 (was {options.ToolOutputMaxChars}).");

        if (options.MaxSurfacedSkills <= 0)
            failures.Add($"{nameof(AgentOptions.MaxSurfacedSkills)} must be > 0 (was {options.MaxSurfacedSkills}).");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
