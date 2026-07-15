// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Tests.Configuration;

public sealed class ConfigWriterTests
{
    private static (ConfigWriter Writer, FakeConfigFileStore Files, RuntimeSettings Runtime) Build(
        CaliperOptions? caliper = null,
        PermissionsOptions? permissions = null)
    {
        var files = new FakeConfigFileStore();
        var runtime = new RuntimeSettings(
            Options.Create(caliper ?? new CaliperOptions()),
            Options.Create(permissions ?? new PermissionsOptions()));
        var writer = new ConfigWriter(files, runtime, new CaliperOptionsValidator(), new SearchOptionsValidator());
        return (writer, files, runtime);
    }

    [Fact]
    public async Task SaveCaliperAsync_valid_options_round_trips_through_the_file()
    {
        var (writer, files, _) = Build();

        var result = await writer.SaveCaliperAsync(new CaliperOptions { Model = "new-model", MaxSteps = 10 }, CancellationToken.None);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(files.Content);
        Assert.Equal("new-model", document.RootElement.GetProperty("Caliper").GetProperty("Model").GetString());
    }

    [Fact]
    public async Task LoadCaliperAsync_reflects_the_most_recent_save_not_runtime_settings()
    {
        var (writer, _, runtime) = Build();
        // EnabledTools is not in the live-copyable subset, so runtimeSettings.Caliper never
        // reflects a save of it — Load must still see the freshest file content.
        await writer.SaveCaliperAsync(new CaliperOptions { EnabledTools = ["search", "grep"] }, CancellationToken.None);

        var loaded = await writer.LoadCaliperAsync(CancellationToken.None);

        Assert.Equal(["search", "grep"], loaded.EnabledTools);
        Assert.NotEqual(loaded.EnabledTools, runtime.Caliper.EnabledTools);
    }

    [Fact]
    public async Task SaveCaliperAsync_invalid_options_does_not_write()
    {
        var (writer, files, _) = Build();
        var before = files.Content;

        var result = await writer.SaveCaliperAsync(new CaliperOptions { MaxSteps = 0 }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(before, files.Content);
    }

    [Fact]
    public async Task SaveCaliperAsync_applies_live_fields_to_runtime_settings()
    {
        var (writer, _, runtime) = Build();

        await writer.SaveCaliperAsync(new CaliperOptions { Model = "live-model", MaxSteps = 42 }, CancellationToken.None);

        Assert.Equal("live-model", runtime.Caliper.Model);
        Assert.Equal(42, runtime.Caliper.MaxSteps);
    }

    [Fact]
    public async Task SaveCaliperAsync_flags_restart_required_when_enabled_tools_change()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveCaliperAsync(
            new CaliperOptions { EnabledTools = ["search"] },
            CancellationToken.None);

        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task SaveCaliperAsync_does_not_flag_restart_when_only_live_fields_change()
    {
        var (writer, _, _) = Build();
        // Establish the "previous" state in the file (RestartRequired compares against what was
        // last persisted, not the RuntimeSettings seed), then save again changing only a live field.
        await writer.SaveCaliperAsync(new CaliperOptions { EnabledTools = ["search"] }, CancellationToken.None);

        var result = await writer.SaveCaliperAsync(
            new CaliperOptions { EnabledTools = ["search"], MaxSteps = 5 },
            CancellationToken.None);

        Assert.False(result.RestartRequired);
    }

    [Fact]
    public async Task SavePermissionsAsync_is_never_restart_required()
    {
        var (writer, _, runtime) = Build();

        var result = await writer.SavePermissionsAsync(
            new PermissionsOptions { Mode = PermissionMode.Auto, RememberApprovals = false },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        Assert.Equal(PermissionMode.Auto, runtime.Permissions.Mode);
        Assert.False(runtime.Permissions.RememberApprovals);
    }

    [Fact]
    public async Task SaveProvidersAsync_blank_api_key_is_not_written_as_empty_string()
    {
        var (writer, files, _) = Build();

        var result = await writer.SaveProvidersAsync(
            new ProvidersOptions { OpenRouter = new OpenRouterOptions { ApiKey = "" } },
            CancellationToken.None);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(files.Content);
        var openRouter = document.RootElement.GetProperty("Providers").GetProperty("OpenRouter");
        Assert.True(
            !openRouter.TryGetProperty("ApiKey", out var apiKey) || apiKey.ValueKind == JsonValueKind.Null,
            "blank API key must not be persisted as an empty string");
    }

    [Fact]
    public async Task SaveProvidersAsync_does_not_flag_restart_when_nothing_material_changes()
    {
        var (writer, _, _) = Build();
        var providers = new ProvidersOptions { OpenRouter = new OpenRouterOptions { Endpoint = "https://example/api" } };
        await writer.SaveProvidersAsync(providers, CancellationToken.None);

        var result = await writer.SaveProvidersAsync(providers, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
    }

    [Fact]
    public async Task SaveProvidersAsync_flags_restart_when_endpoint_changes()
    {
        var (writer, _, _) = Build();
        await writer.SaveProvidersAsync(
            new ProvidersOptions { OpenRouter = new OpenRouterOptions { Endpoint = "https://one/api" } },
            CancellationToken.None);

        var result = await writer.SaveProvidersAsync(
            new ProvidersOptions { OpenRouter = new OpenRouterOptions { Endpoint = "https://two/api" } },
            CancellationToken.None);

        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task SaveProvidersAsync_blank_endpoint_fails_validation()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveProvidersAsync(
            new ProvidersOptions { OpenRouter = new OpenRouterOptions { Endpoint = " " } },
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SaveMcpAsync_duplicate_or_missing_fields_fail_validation()
    {
        var (writer, _, _) = Build();
        var options = new McpOptions();
        options.Servers[""] = new McpServerOptions { Type = "stdio", Command = "tool" };

        var result = await writer.SaveMcpAsync(options, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SaveMcpAsync_stdio_without_command_fails_validation()
    {
        var (writer, _, _) = Build();
        var options = new McpOptions();
        options.Servers["local"] = new McpServerOptions { Type = "stdio" };

        var result = await writer.SaveMcpAsync(options, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SaveMcpAsync_valid_server_writes_and_requires_restart()
    {
        var (writer, files, _) = Build();
        var options = new McpOptions();
        options.Servers["local"] = new McpServerOptions { Type = "stdio", Command = "npx" };

        var result = await writer.SaveMcpAsync(options, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
        using var document = JsonDocument.Parse(files.Content);
        Assert.True(document.RootElement.GetProperty("Mcp").GetProperty("Servers").TryGetProperty("local", out _));
    }

    [Fact]
    public async Task SaveSearchAsync_blank_key_with_tavily_backend_does_not_false_fail()
    {
        var (writer, files, _) = Build();

        var result = await writer.SaveSearchAsync(
            new SearchOptions { Backend = "Tavily", ApiKey = "" },
            CancellationToken.None);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(files.Content);
        var search = document.RootElement.GetProperty("Search");
        Assert.True(
            !search.TryGetProperty("ApiKey", out var apiKey) || apiKey.ValueKind == JsonValueKind.Null,
            "blank API key must not be persisted as an empty string");
    }

    [Fact]
    public async Task SaveSearchAsync_invalid_topic_fails_validation()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveSearchAsync(
            new SearchOptions { Topic = "not-a-real-topic" },
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SavePersistenceAsync_blank_path_fails_validation()
    {
        var (writer, _, _) = Build();

        var result = await writer.SavePersistenceAsync(new PersistenceOptions { SqlitePath = " " }, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SavePersistenceAsync_valid_path_requires_restart()
    {
        var (writer, files, _) = Build();

        var result = await writer.SavePersistenceAsync(new PersistenceOptions { SqlitePath = "db.sqlite" }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
        using var document = JsonDocument.Parse(files.Content);
        Assert.Equal("db.sqlite", document.RootElement.GetProperty("Persistence").GetProperty("SqlitePath").GetString());
    }

    [Fact]
    public async Task SaveSubagentsAsync_round_trips_through_the_Caliper_section_without_disturbing_other_fields()
    {
        var (writer, files, _) = Build();
        await writer.SaveCaliperAsync(new CaliperOptions { Model = "kept-model" }, CancellationToken.None);

        var subagents = new SubagentsOptions { MaxDepth = 3, MaxChildrenPerRun = 4 };
        var result = await writer.SaveSubagentsAsync(subagents, CancellationToken.None);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(files.Content);
        var caliper = document.RootElement.GetProperty("Caliper");
        Assert.Equal("kept-model", caliper.GetProperty("Model").GetString());
        Assert.Equal(3, caliper.GetProperty("Subagents").GetProperty("MaxDepth").GetInt32());
    }

    [Fact]
    public async Task SaveSubagentsAsync_is_live_no_restart_required()
    {
        var (writer, _, runtime) = Build();

        var result = await writer.SaveSubagentsAsync(new SubagentsOptions { MaxDepth = 3 }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        Assert.Equal(3, runtime.Caliper.Subagents.MaxDepth);
    }

    [Fact]
    public async Task SaveSubagentsAsync_invalid_default_profile_fails_validation()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveSubagentsAsync(
            new SubagentsOptions { DefaultProfile = "missing-profile" },
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SaveSchedulesAsync_valid_schedule_round_trips_and_is_live()
    {
        var (writer, files, runtime) = Build();
        var schedules = new List<ScheduleOptions>
        {
            new() { Name = "nightly", Cron = "0 6 * * *", Prompt = "check deps", TimeZone = "UTC" },
        };

        var result = await writer.SaveSchedulesAsync(schedules, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        using var document = JsonDocument.Parse(files.Content);
        var persisted = document.RootElement.GetProperty("Caliper").GetProperty("Schedules");
        Assert.Equal(1, persisted.GetArrayLength());
        Assert.Equal("nightly", persisted[0].GetProperty("Name").GetString());
        // Live seam: the scheduler re-reads runtimeSettings.Caliper.Schedules every tick, so the
        // save must have pushed the new list into the runtime clone as well.
        Assert.Equal("nightly", Assert.Single(runtime.Caliper.Schedules).Name);
    }

    [Fact]
    public async Task SaveSchedulesAsync_invalid_cron_is_rejected_and_not_written()
    {
        var (writer, files, _) = Build();
        var before = files.Content;

        var result = await writer.SaveSchedulesAsync(
            [new ScheduleOptions { Name = "bad", Cron = "not a cron", Prompt = "p" }],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cron", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, files.Content);
    }

    [Fact]
    public async Task SaveSchedulesAsync_duplicate_names_are_rejected()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveSchedulesAsync(
            [
                new ScheduleOptions { Name = "Job", Cron = "0 6 * * *", Prompt = "p" },
                new ScheduleOptions { Name = "job", Cron = "0 7 * * *", Prompt = "p" },
            ],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("duplicated", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveSchedulesAsync_bad_time_zone_is_rejected()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveSchedulesAsync(
            [new ScheduleOptions { Name = "job", Cron = "0 6 * * *", Prompt = "p", TimeZone = "Not/A_Zone" }],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("TimeZone", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveCaliperAsync_flags_restart_when_MaxConcurrentJobs_changes()
    {
        var (writer, _, _) = Build();
        await writer.SaveCaliperAsync(new CaliperOptions(), CancellationToken.None);

        var changed = new CaliperOptions();
        changed.Scheduler.MaxConcurrentJobs = 3;
        var result = await writer.SaveCaliperAsync(changed, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task SavePermissionsAsync_wildcard_allowlist_fails_when_execution_backend_is_host()
    {
        var (writer, files, _) = Build();
        var before = files.Content;

        var result = await writer.SavePermissionsAsync(
            new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("wildcard", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, files.Content);
    }

    [Fact]
    public async Task SavePermissionsAsync_wildcard_allowlist_succeeds_when_execution_backend_is_container()
    {
        var (writer, _, _) = Build();
        await writer.SaveExecutionAsync(new ExecutionOptions { Backend = ExecutionBackendKind.Container }, CancellationToken.None);

        var result = await writer.SavePermissionsAsync(
            new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task SavePermissionsAsync_non_wildcard_allowlist_succeeds_under_host_backend()
    {
        var (writer, _, _) = Build();

        var result = await writer.SavePermissionsAsync(
            new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["git status", "dotnet build"] },
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task SavePermissionsAsync_writes_Mode_as_a_string_not_an_int()
    {
        // A8: config.json hand-editability — ConfigWriter must persist enum config values as their
        // string names, not the underlying int, so a hand-edited config.json keeps its shape after
        // a save through the App/console settings UI round-trips it.
        var (writer, files, _) = Build();

        await writer.SavePermissionsAsync(new PermissionsOptions { Mode = PermissionMode.Auto }, CancellationToken.None);

        using var document = JsonDocument.Parse(files.Content);
        var mode = document.RootElement.GetProperty("Permissions").GetProperty("Mode");
        Assert.Equal(JsonValueKind.String, mode.ValueKind);
        Assert.Equal("Auto", mode.GetString());
    }

    [Fact]
    public async Task SaveCaliperAsync_writes_TurnStrategy_and_SkillSelector_as_strings_not_ints()
    {
        var (writer, files, _) = Build();

        await writer.SaveCaliperAsync(
            new CaliperOptions { TurnStrategy = TurnStrategyKind.Native, SkillSelector = SkillSelectorKind.Keyword },
            CancellationToken.None);

        using var document = JsonDocument.Parse(files.Content);
        var caliper = document.RootElement.GetProperty("Caliper");
        Assert.Equal(JsonValueKind.String, caliper.GetProperty("TurnStrategy").ValueKind);
        Assert.Equal("Native", caliper.GetProperty("TurnStrategy").GetString());
        Assert.Equal(JsonValueKind.String, caliper.GetProperty("SkillSelector").ValueKind);
        Assert.Equal("Keyword", caliper.GetProperty("SkillSelector").GetString());
    }

    [Fact]
    public async Task SaveExecutionAsync_writes_Backend_and_Network_as_strings_not_ints()
    {
        var (writer, files, _) = Build();

        await writer.SaveExecutionAsync(
            new ExecutionOptions { Backend = ExecutionBackendKind.Container, Network = ExecutionNetworkKind.Bridge },
            CancellationToken.None);

        using var document = JsonDocument.Parse(files.Content);
        var execution = document.RootElement.GetProperty("Caliper").GetProperty("Execution");
        Assert.Equal(JsonValueKind.String, execution.GetProperty("Backend").ValueKind);
        Assert.Equal("Container", execution.GetProperty("Backend").GetString());
        Assert.Equal(JsonValueKind.String, execution.GetProperty("Network").ValueKind);
        Assert.Equal("Bridge", execution.GetProperty("Network").GetString());
    }

    [Fact]
    public async Task SaveCaliperAsync_loads_a_legacy_int_valued_config_file_and_re_saves_it_as_strings()
    {
        // A8 migration guarantee: an existing config.json written before this change (enum values
        // as ints) must still load cleanly, and the very next save must upgrade it to the
        // string-valued form — no explicit migration step required.
        var files = new FakeConfigFileStore();
        files.Content = """
            {"Caliper":{"TurnStrategy":1,"SkillSelector":1},"Permissions":{"Mode":1},"Providers":{},"Mcp":{"Servers":{}},"Search":{},"Persistence":{}}
            """;
        var runtime = new RuntimeSettings(Options.Create(new CaliperOptions()), Options.Create(new PermissionsOptions()));
        var writer = new ConfigWriter(files, runtime, new CaliperOptionsValidator(), new SearchOptionsValidator());

        var loaded = await writer.LoadCaliperAsync(CancellationToken.None);
        Assert.Equal(TurnStrategyKind.Native, loaded.TurnStrategy);
        Assert.Equal(SkillSelectorKind.Keyword, loaded.SkillSelector);

        var result = await writer.SaveCaliperAsync(loaded, CancellationToken.None);
        Assert.True(result.Success);

        using var document = JsonDocument.Parse(files.Content);
        var caliper = document.RootElement.GetProperty("Caliper");
        Assert.Equal(JsonValueKind.String, caliper.GetProperty("TurnStrategy").ValueKind);
        Assert.Equal(JsonValueKind.String, caliper.GetProperty("SkillSelector").ValueKind);
    }

    [Fact]
    public async Task SaveExecutionAsync_valid_options_round_trips_and_is_live()
    {
        var (writer, files, runtime) = Build();

        var result = await writer.SaveExecutionAsync(
            new ExecutionOptions { Backend = ExecutionBackendKind.Container, Image = "custom:latest", MemoryMb = 8192 },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.RestartRequired);
        using var document = JsonDocument.Parse(files.Content);
        var execution = document.RootElement.GetProperty("Caliper").GetProperty("Execution");
        Assert.Equal("custom:latest", execution.GetProperty("Image").GetString());
        Assert.Equal(ExecutionBackendKind.Container, runtime.Caliper.Execution.Backend);
        Assert.Equal(8192, runtime.Caliper.Execution.MemoryMb);
    }

    [Fact]
    public async Task SaveExecutionAsync_flipping_backend_back_to_host_is_rejected_when_a_saved_schedule_has_a_wildcard_allowlist()
    {
        var (writer, _, _) = Build();
        await writer.SaveExecutionAsync(new ExecutionOptions { Backend = ExecutionBackendKind.Container }, CancellationToken.None);
        await writer.SaveSchedulesAsync(
            [
                new ScheduleOptions
                {
                    Name = "job",
                    Cron = "0 6 * * *",
                    Prompt = "p",
                    Permissions = new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
                },
            ],
            CancellationToken.None);

        var result = await writer.SaveExecutionAsync(new ExecutionOptions { Backend = ExecutionBackendKind.Host }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("wildcard", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveSchedulesAsync_wildcard_overlay_is_rejected_when_execution_backend_is_host()
    {
        var (writer, _, _) = Build();

        var result = await writer.SaveSchedulesAsync(
            [
                new ScheduleOptions
                {
                    Name = "job",
                    Cron = "0 6 * * *",
                    Prompt = "p",
                    Permissions = new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
                },
            ],
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("wildcard", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveSchedulesAsync_wildcard_overlay_succeeds_when_execution_backend_is_container()
    {
        var (writer, _, _) = Build();
        await writer.SaveExecutionAsync(new ExecutionOptions { Backend = ExecutionBackendKind.Container }, CancellationToken.None);

        var result = await writer.SaveSchedulesAsync(
            [
                new ScheduleOptions
                {
                    Name = "job",
                    Cron = "0 6 * * *",
                    Prompt = "p",
                    Permissions = new PermissionsOptions { Mode = PermissionMode.Auto, ShellAutoAllowlist = ["*"] },
                },
            ],
            CancellationToken.None);

        Assert.True(result.Success);
    }

    private sealed class FakeConfigFileStore : IConfigFileStore
    {
        // Not `private set`: SaveCaliperAsync_loads_a_legacy_int_valued_config_file_and_re_saves_it_as_strings
        // seeds this directly to simulate a pre-existing config.json (private set is only visible
        // within FakeConfigFileStore itself, not the enclosing ConfigWriterTests class).
        public string Content { get; set; } = """{"Caliper":{},"Permissions":{},"Providers":{},"Mcp":{"Servers":{}},"Search":{},"Persistence":{}}""";

        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(Content);

        public Task WriteAsync(string json, CancellationToken ct)
        {
            Content = json;
            return Task.CompletedTask;
        }
    }
}
