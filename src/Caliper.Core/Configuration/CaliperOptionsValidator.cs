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

        ValidateSubagents(options.Subagents, failures);
        ValidateScheduler(options.Scheduler, failures);
        ValidateExecution(options.Execution, failures);
        ValidateSchedules(options.Schedules, options.Execution, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    // Roadmap §3.3: enum values, positive resource limits, and (only when the container backend is
    // actually selected) a non-blank image/user — a Host-backend config doesn't need those two
    // populated since docker is never invoked.
    private static void ValidateExecution(ExecutionOptions execution, List<string> failures)
    {
        if (!Enum.IsDefined(execution.Backend))
            failures.Add($"{nameof(ExecutionOptions.Backend)} must be Host or Container (was '{execution.Backend}').");

        if (!Enum.IsDefined(execution.Network))
            failures.Add($"{nameof(ExecutionOptions.Network)} must be None or Bridge (was '{execution.Network}').");

        if (execution.Cpus <= 0)
            failures.Add($"{nameof(ExecutionOptions.Cpus)} must be > 0 (was {execution.Cpus}).");

        if (execution.MemoryMb <= 0)
            failures.Add($"{nameof(ExecutionOptions.MemoryMb)} must be > 0 (was {execution.MemoryMb}).");

        if (execution.Backend == ExecutionBackendKind.Container)
        {
            if (string.IsNullOrWhiteSpace(execution.Image))
                failures.Add($"{nameof(ExecutionOptions.Image)} must not be empty when {nameof(ExecutionOptions.Backend)} is Container.");

            if (string.IsNullOrWhiteSpace(execution.User))
                failures.Add($"{nameof(ExecutionOptions.User)} must not be empty when {nameof(ExecutionOptions.Backend)} is Container.");
        }
    }

    private static void ValidateScheduler(SchedulerOptions scheduler, List<string> failures)
    {
        if (scheduler.MaxConcurrentJobs < 1)
            failures.Add($"{nameof(SchedulerOptions.MaxConcurrentJobs)} must be >= 1 (was {scheduler.MaxConcurrentJobs}).");
    }

    // Roadmap §3.2b: the same rules run at binding/startup (here) and at save time
    // (ConfigWriter.SaveSchedulesAsync funnels through SaveCaliperAsync, which calls this
    // validator), so an invalid schedule is rejected in both places by one implementation.
    private static void ValidateSchedules(IList<ScheduleOptions> schedules, ExecutionOptions execution, List<string> failures)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var schedule in schedules)
        {
            if (string.IsNullOrWhiteSpace(schedule.Name))
            {
                failures.Add("Every schedule needs a non-empty Name.");
                continue;
            }

            var name = schedule.Name.Trim();
            if (!seenNames.Add(name))
                failures.Add($"Schedule name '{name}' is duplicated (names are case-insensitive).");

            if (string.IsNullOrWhiteSpace(schedule.Prompt))
                failures.Add($"Schedule '{name}' must have a non-empty Prompt.");

            if (!Scheduling.ScheduleCron.TryParseCron(schedule.Cron, out _, out var cronError))
                failures.Add($"Schedule '{name}' has an invalid Cron expression '{schedule.Cron}': {cronError}");

            if (!Scheduling.ScheduleCron.TryResolveTimeZone(schedule.TimeZone, out _, out var zoneError))
                failures.Add($"Schedule '{name}' has an unresolvable TimeZone '{schedule.TimeZone}': {zoneError}");

            if (schedule.WorkingRoot is { } workingRoot)
            {
                if (string.IsNullOrWhiteSpace(workingRoot))
                {
                    failures.Add($"Schedule '{name}' WorkingRoot must not be blank when set (omit it to use the global working root).");
                }
                else
                {
                    var resolved = Path.GetFullPath(LocalPath.ResolveHome(workingRoot));
                    if (!Directory.Exists(resolved))
                        failures.Add($"Schedule '{name}' WorkingRoot does not exist: {resolved}");
                }
            }

            if (schedule.Model is { } model && string.IsNullOrWhiteSpace(model))
                failures.Add($"Schedule '{name}' Model must not be blank when set (omit it to use the default model).");

            if (schedule.MaxSteps is { } maxSteps && maxSteps <= 0)
                failures.Add($"Schedule '{name}' MaxSteps must be > 0 (was {maxSteps}).");

            // Roadmap §3.3 payoff: a job overlay's own ShellAutoAllowlist is subject to the same
            // wildcard-requires-container rule as the global Permissions section (see
            // ConfigWriter.SavePermissionsAsync) — a schedule always runs unattended.
            if (schedule.Permissions is { } overlay &&
                UnattendedAllowlistGuard.Validate(overlay.ShellAutoAllowlist, execution.Backend, $"Schedule '{name}'") is { } wildcardError)
            {
                failures.Add(wildcardError);
            }
        }
    }

    private static void ValidateSubagents(SubagentsOptions subagents, List<string> failures)
    {
        if (subagents.MaxDepth < 1)
            failures.Add($"{nameof(SubagentsOptions.MaxDepth)} must be >= 1 (was {subagents.MaxDepth}).");

        if (subagents.MaxChildrenPerRun <= 0)
            failures.Add($"{nameof(SubagentsOptions.MaxChildrenPerRun)} must be > 0 (was {subagents.MaxChildrenPerRun}).");

        if (subagents.TimeoutSeconds <= 0)
            failures.Add($"{nameof(SubagentsOptions.TimeoutSeconds)} must be > 0 (was {subagents.TimeoutSeconds}).");

        if (string.IsNullOrWhiteSpace(subagents.DefaultProfile))
            failures.Add($"{nameof(SubagentsOptions.DefaultProfile)} must not be empty.");
        else if (!subagents.Profiles.ContainsKey(subagents.DefaultProfile))
            failures.Add($"{nameof(SubagentsOptions.DefaultProfile)} '{subagents.DefaultProfile}' is not a key in {nameof(SubagentsOptions.Profiles)}.");

        foreach (var (name, profile) in subagents.Profiles)
        {
            if (profile.EnabledTools.Count == 0)
                failures.Add($"Subagent profile '{name}' must list at least one tool in {nameof(SubagentProfileOptions.EnabledTools)}.");

            if (profile.MaxSteps <= 0)
                failures.Add($"Subagent profile '{name}' {nameof(SubagentProfileOptions.MaxSteps)} must be > 0 (was {profile.MaxSteps}).");
        }
    }
}
