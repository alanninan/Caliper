// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Globalization;
using Caliper.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

internal sealed class RuntimeSettings(
    IOptions<CaliperOptions> caliper,
    IOptions<PermissionsOptions> permissions) : IRuntimeSettings
{
    private readonly object _gate = new();
    private CaliperOptions _caliper = Clone(caliper.Value);
    private PermissionsOptions _permissions = Clone(permissions.Value);

    public event EventHandler? SettingsChanged;

    public CaliperOptions Caliper
    {
        get { lock (_gate) return Clone(_caliper); }
    }

    public PermissionsOptions Permissions
    {
        get { lock (_gate) return Clone(_permissions); }
    }

    public void SetProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider cannot be empty.", nameof(provider));

        lock (_gate)
            _caliper.Provider = provider.Trim();
        RaiseSettingsChanged();
    }

    public void SetModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model slug cannot be empty.", nameof(model));

        lock (_gate)
            _caliper.Model = model.Trim();
        RaiseSettingsChanged();
    }

    public void SetPermissionMode(PermissionMode mode)
    {
        lock (_gate)
            _permissions.Mode = mode;
        RaiseSettingsChanged();
    }

    public void UpdateCaliper(Action<CaliperOptions> mutate)
    {
        lock (_gate)
            mutate(_caliper);
        RaiseSettingsChanged();
    }

    public void UpdatePermissions(Action<PermissionsOptions> mutate)
    {
        lock (_gate)
            mutate(_permissions);
        RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public bool TrySet(string key, string value, out string message)
    {
        var result = TrySetCore(key, value, out message);
        if (result)
            RaiseSettingsChanged();
        return result;
    }

    private bool TrySetCore(string key, string value, out string message)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            message = "Setting key cannot be empty.";
            return false;
        }

        lock (_gate)
        {
            switch (NormalizeKey(key))
            {
                case "provider":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        message = "Provider cannot be empty.";
                        return false;
                    }
                    _caliper.Provider = value.Trim();
                    message = $"provider = {_caliper.Provider}";
                    return true;

                case "model":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        message = "Model slug cannot be empty.";
                        return false;
                    }
                    _caliper.Model = value.Trim();
                    message = $"model = {_caliper.Model}";
                    return true;

                case "permissions.mode":
                case "permission.mode":
                case "permissions":
                    if (!Enum.TryParse<PermissionMode>(value, ignoreCase: true, out var mode))
                    {
                        message = "Permission mode must be AskAlways, Auto, or Plan.";
                        return false;
                    }
                    _permissions.Mode = mode;
                    message = $"permissions.mode = {_permissions.Mode}";
                    return true;

                case "permissions.rememberapprovals":
                    if (!bool.TryParse(value, out var rememberApprovals))
                    {
                        message = "permissions.rememberApprovals must be true or false.";
                        return false;
                    }
                    _permissions.RememberApprovals = rememberApprovals;
                    message = $"permissions.rememberApprovals = {_permissions.RememberApprovals}";
                    return true;

                case "workingroot":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        message = "Working root cannot be empty.";
                        return false;
                    }
                    var resolvedRoot = Path.GetFullPath(LocalPath.ResolveHome(value.Trim()));
                    if (!Directory.Exists(resolvedRoot))
                    {
                        message = $"Working root does not exist: {resolvedRoot}";
                        return false;
                    }
                    _caliper.WorkingRoot = value.Trim();
                    message = $"workingRoot = {_caliper.WorkingRoot}";
                    return true;

                case "temperature":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) || temperature < 0)
                    {
                        message = "Temperature must be a non-negative number.";
                        return false;
                    }
                    _caliper.Temperature = temperature;
                    message = $"temperature = {_caliper.Temperature}";
                    return true;

                case "seed":
                    if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        _caliper.Seed = null;
                    }
                    else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
                    {
                        _caliper.Seed = seed;
                    }
                    else
                    {
                        message = "Seed must be an integer, null, or none.";
                        return false;
                    }
                    message = $"seed = {_caliper.Seed?.ToString() ?? "none"}";
                    return true;

                case "reasoning.effort":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        message = "Reasoning effort cannot be empty.";
                        return false;
                    }
                    _caliper.Reasoning.Effort = value.Trim();
                    message = $"reasoning.effort = {_caliper.Reasoning.Effort}";
                    return true;

                case "reasoning.exclude":
                    if (!bool.TryParse(value, out var excludeReasoning))
                    {
                        message = "reasoning.exclude must be true or false.";
                        return false;
                    }
                    _caliper.Reasoning.Exclude = excludeReasoning;
                    message = $"reasoning.exclude = {_caliper.Reasoning.Exclude}";
                    return true;

                case "context.autocompact":
                case "ctx.autocompact":
                    if (!bool.TryParse(value, out var autoCompact))
                    {
                        message = "context.autocompact must be true or false.";
                        return false;
                    }
                    _caliper.Context.AutoCompact = autoCompact;
                    message = $"context.autocompact = {_caliper.Context.AutoCompact}";
                    return true;

                case "context.compactatfraction":
                case "ctx.compactatfraction":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || fraction <= 0 || fraction > 1)
                    {
                        message = "context.compactAtFraction must be between 0 and 1.";
                        return false;
                    }
                    _caliper.Context.CompactAtFraction = fraction;
                    message = $"context.compactAtFraction = {_caliper.Context.CompactAtFraction}";
                    return true;

                case "context.reservedoutputtokens":
                case "ctx.reservedoutputtokens":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reserved) || reserved < 0)
                    {
                        message = "context.reservedOutputTokens must be a non-negative integer.";
                        return false;
                    }
                    _caliper.Context.ReservedOutputTokens = reserved;
                    message = $"context.reservedOutputTokens = {_caliper.Context.ReservedOutputTokens}";
                    return true;

                default:
                    message = $"Unsupported runtime setting: {key}";
                    return false;
            }
        }
    }

    private static string NormalizeKey(string key) =>
        key.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static CaliperOptions Clone(CaliperOptions source) =>
        new()
        {
            Provider = source.Provider,
            Model = source.Model,
            SummarizerModel = source.SummarizerModel,
            MaxSteps = source.MaxSteps,
            DuplicateCallLimit = source.DuplicateCallLimit,
            ToolTimeoutSeconds = source.ToolTimeoutSeconds,
            ToolMaxRetries = source.ToolMaxRetries,
            ToolOutputMaxChars = source.ToolOutputMaxChars,
            Temperature = source.Temperature,
            Seed = source.Seed,
            Reasoning = new ReasoningOptions
            {
                Effort = source.Reasoning.Effort,
                Exclude = source.Reasoning.Exclude,
            },
            TurnStrategy = source.TurnStrategy,
            EnabledTools = [.. source.EnabledTools],
            WorkingRoot = source.WorkingRoot,
            SkillsDirectory = source.SkillsDirectory,
            SkillSelector = source.SkillSelector,
            MaxSurfacedSkills = source.MaxSurfacedSkills,
            Context = new ContextOptions
            {
                AutoCompact = source.Context.AutoCompact,
                CompactAtFraction = source.Context.CompactAtFraction,
                KeepRecentTurns = source.Context.KeepRecentTurns,
                ReservedOutputTokens = source.Context.ReservedOutputTokens,
            },
            Memory = new MemoryOptions
            {
                Enabled = source.Memory.Enabled,
                GlobalDir = source.Memory.GlobalDir,
                ProjectFile = source.Memory.ProjectFile,
            },
            Subagents = CloneSubagents(source.Subagents),
            Scheduler = new SchedulerOptions
            {
                MaxConcurrentJobs = source.Scheduler.MaxConcurrentJobs,
            },
            Schedules = CloneSchedules(source.Schedules),
        };

    internal static List<ScheduleOptions> CloneSchedules(IList<ScheduleOptions> source) =>
        source.Select(schedule => new ScheduleOptions
        {
            Name = schedule.Name,
            Cron = schedule.Cron,
            TimeZone = schedule.TimeZone,
            Prompt = schedule.Prompt,
            WorkingRoot = schedule.WorkingRoot,
            Model = schedule.Model,
            MaxSteps = schedule.MaxSteps,
            Enabled = schedule.Enabled,
            Permissions = schedule.Permissions is { } permissions ? Clone(permissions) : null,
        }).ToList();

    private static SubagentsOptions CloneSubagents(SubagentsOptions source)
    {
        var profiles = new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, profile) in source.Profiles)
        {
            profiles[name] = new SubagentProfileOptions
            {
                EnabledTools = [.. profile.EnabledTools],
                MaxSteps = profile.MaxSteps,
                Mode = profile.Mode,
            };
        }

        return new SubagentsOptions
        {
            MaxDepth = source.MaxDepth,
            MaxChildrenPerRun = source.MaxChildrenPerRun,
            DefaultProfile = source.DefaultProfile,
            TimeoutSeconds = source.TimeoutSeconds,
            Profiles = profiles,
        };
    }

    private static PermissionsOptions Clone(PermissionsOptions source) =>
        new()
        {
            Mode = source.Mode,
            RememberApprovals = source.RememberApprovals,
            ShellAutoAllowlist = [.. source.ShellAutoAllowlist],
            ShellDenylist = [.. source.ShellDenylist],
            AutoAllowFileRoots = [.. source.AutoAllowFileRoots],
        };
}
