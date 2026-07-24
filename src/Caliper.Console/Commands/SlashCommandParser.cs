// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Console.Commands;

public enum SlashCommandKind
{
    Unknown,
    New,
    Sessions,
    Resume,
    Skills,
    Tools,
    Mcp,
    Memory,
    Compact,
    Clear,
    Set,
    Model,
    Models,
    Providers,
    Auth,
    Permissions,
    Config,
    Schedule,
    Runs,
    Help,
    Quit,
}

public sealed record SlashCommandParseResult(SlashCommandKind Kind, string Argument, string Name);
public sealed record SlashCommandDescriptor(string Command, string Description);

public static class SlashCommandParser
{
    public static IReadOnlyList<SlashCommandDescriptor> Help { get; } =
    [
        new("/new", "Start a new session."),
        new("/sessions", "List saved sessions."),
        new("/resume <id>", "Resume a saved session by id prefix."),
        new("/skills", "List available skills."),
        new("/tools", "List enabled tools."),
        new("/mcp [reconnect]", "Show MCP server status or reconnect."),
        new("/memory", "Show memory entries and CALIPER.md status."),
        new("/compact", "Force context compaction for the active session."),
        new("/clear", "Clear active context while keeping transcript history."),
        new("/model <slug>", "Switch model for the next run."),
        new("/models [provider]", "List known models for the active or named provider."),
        new("/providers", "Show all model providers and authentication status."),
        new("/auth <command>", "Sign in, sign out, set an API key, or inspect authentication."),
        new("/permissions <mode>", "Switch permission mode: AskAlways, Auto, or Plan."),
        new("/set <key> <value>", "Update supported runtime settings."),
        new("/config save", "Persist the session's runtime/permission settings to config.json."),
        new("/schedule list", "List configured schedules with next occurrence and last result."),
        new("/schedule run <name>", "Trigger a schedule now through the unattended path (prompts deny + report)."),
        new("/runs", "List recent tracked runs (one-shot, unattended, scheduled, subagent) with status."),
        new("/help", "Show this command list."),
        new("/quit", "Exit Caliper."),
    ];

    public static SlashCommandParseResult Parse(string input)
    {
        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length == 0 || parts[0][0] != '/')
            return new SlashCommandParseResult(SlashCommandKind.Unknown, string.Empty, string.Empty);

        var name = parts[0].ToLowerInvariant();
        var argument = parts.Length == 2 ? parts[1].Trim() : string.Empty;
        var kind = name switch
        {
            "/new" => SlashCommandKind.New,
            "/sessions" => SlashCommandKind.Sessions,
            "/resume" => SlashCommandKind.Resume,
            "/skills" => SlashCommandKind.Skills,
            "/tools" => SlashCommandKind.Tools,
            "/mcp" => SlashCommandKind.Mcp,
            "/memory" => SlashCommandKind.Memory,
            "/compact" => SlashCommandKind.Compact,
            "/clear" => SlashCommandKind.Clear,
            "/set" => SlashCommandKind.Set,
            "/model" => SlashCommandKind.Model,
            "/models" => SlashCommandKind.Models,
            "/providers" => SlashCommandKind.Providers,
            "/auth" => SlashCommandKind.Auth,
            "/permissions" => SlashCommandKind.Permissions,
            "/config" => SlashCommandKind.Config,
            "/schedule" => SlashCommandKind.Schedule,
            "/runs" => SlashCommandKind.Runs,
            "/help" => SlashCommandKind.Help,
            "/quit" => SlashCommandKind.Quit,
            _ => SlashCommandKind.Unknown,
        };

        return new SlashCommandParseResult(kind, argument, name);
    }
}
