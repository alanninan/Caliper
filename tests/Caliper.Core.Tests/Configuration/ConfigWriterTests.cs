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

    private sealed class FakeConfigFileStore : IConfigFileStore
    {
        public string Content { get; private set; } = """{"Caliper":{},"Permissions":{},"Providers":{},"Mcp":{"Servers":{}},"Search":{},"Persistence":{}}""";

        public Task<string> ReadAsync(CancellationToken ct) => Task.FromResult(Content);

        public Task WriteAsync(string json, CancellationToken ct)
        {
            Content = json;
            return Task.CompletedTask;
        }
    }
}
