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

    [Fact]
    public void Default_subagents_options_pass()
    {
        var result = Validate(new CaliperOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Subagents_MaxDepth_below_one_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.MaxDepth = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.MaxDepth), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_MaxChildrenPerRun_non_positive_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.MaxChildrenPerRun = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.MaxChildrenPerRun), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_TimeoutSeconds_non_positive_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.TimeoutSeconds = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.TimeoutSeconds), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_DefaultProfile_missing_from_Profiles_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.DefaultProfile = "does-not-exist";

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SubagentsOptions.DefaultProfile), StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_profile_with_no_tools_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.Profiles["research"].EnabledTools = [];

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("research", StringComparison.Ordinal));
    }

    [Fact]
    public void Subagents_profile_with_non_positive_MaxSteps_fails()
    {
        var options = new CaliperOptions();
        options.Subagents.Profiles["worker"].MaxSteps = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("worker", StringComparison.Ordinal));
    }

    private static ScheduleOptions Schedule(string name = "job", string cron = "0 6 * * *") =>
        new() { Name = name, Cron = cron, Prompt = "do the thing" };

    [Fact]
    public void Valid_schedule_passes()
    {
        var options = new CaliperOptions { Schedules = [Schedule()] };

        var result = Validate(options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Schedule_with_invalid_cron_fails()
    {
        var options = new CaliperOptions { Schedules = [Schedule(cron: "not a cron")] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Cron", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_with_duplicate_name_fails_case_insensitively()
    {
        var options = new CaliperOptions { Schedules = [Schedule("Nightly"), Schedule("nightly")] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_with_empty_name_fails()
    {
        var options = new CaliperOptions { Schedules = [Schedule(name: " ")] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_with_unresolvable_time_zone_fails()
    {
        var schedule = Schedule();
        schedule.TimeZone = "Not/A_Zone";
        var options = new CaliperOptions { Schedules = [schedule] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("TimeZone", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_time_zone_local_and_system_ids_pass()
    {
        var local = Schedule("a");
        local.TimeZone = "local";
        var utc = Schedule("b");
        utc.TimeZone = "UTC";
        var options = new CaliperOptions { Schedules = [local, utc] };

        var result = Validate(options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Schedule_with_missing_working_root_fails()
    {
        var schedule = Schedule();
        schedule.WorkingRoot = Path.Combine(Path.GetTempPath(), "caliper-missing-" + Guid.NewGuid().ToString("N"));
        var options = new CaliperOptions { Schedules = [schedule] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("WorkingRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_with_blank_model_fails()
    {
        var schedule = Schedule();
        schedule.Model = "  ";
        var options = new CaliperOptions { Schedules = [schedule] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Model", StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_with_empty_prompt_fails()
    {
        var schedule = Schedule();
        schedule.Prompt = "";
        var options = new CaliperOptions { Schedules = [schedule] };

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Scheduler_MaxConcurrentJobs_below_one_fails()
    {
        var options = new CaliperOptions();
        options.Scheduler.MaxConcurrentJobs = 0;

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains(nameof(SchedulerOptions.MaxConcurrentJobs), StringComparison.Ordinal));
    }
}
