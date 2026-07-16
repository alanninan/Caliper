// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Agents;
using Caliper.Core;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Logging;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Scheduling;
using Caliper.Console.Commands;
using Caliper.Console.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

var oneShotPrompt = GetOption(args, "--prompt");
var permissionMode = GetOption(args, "--permissions");
var oneShotPrint = HasOption(args, "--print");
var unattended = HasOption(args, "--unattended");
var serve = HasOption(args, "--serve");
var resumeRunId = GetOption(args, "--resume");

// --serve is the headless scheduler host: no REPL, no one-shot. Reject the contradictory
// combinations up front instead of silently ignoring a flag.
if (serve && (!string.IsNullOrWhiteSpace(oneShotPrompt) || unattended))
{
    AnsiConsole.MarkupLine("[red]--serve cannot be combined with --prompt or --unattended.[/]");
    Environment.Exit(1);
}

// --resume is one-shot style itself (roadmap §3.4): rejects --prompt/--serve, composes with --print.
var resumeError = ResumeFlagValidator.Validate(!string.IsNullOrWhiteSpace(resumeRunId), !string.IsNullOrWhiteSpace(oneShotPrompt), serve);
if (resumeError is not null)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(resumeError)}[/]");
    Environment.Exit(1);
}

if (!string.IsNullOrWhiteSpace(permissionMode) &&
    !Enum.TryParse<PermissionMode>(permissionMode, ignoreCase: true, out _))
{
    AnsiConsole.MarkupLine("[red]Invalid --permissions value. Use AskAlways, Auto, or Plan.[/]");
    Environment.Exit(1);
}

var permissionPlan = OneShotPermissionResolver.Resolve(
    unattended, !string.IsNullOrWhiteSpace(oneShotPrompt), permissionMode);
if (!permissionPlan.IsValid)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(permissionPlan.Error!)}[/]");
    Environment.Exit(1);
}

CaliperHome.EnsureInitialized();

var cliOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
if (permissionPlan.ForcedPermissionMode is { } forcedPermissionMode)
    cliOverrides["Permissions:Mode"] = forcedPermissionMode;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory(),
});
builder.Logging.ClearProviders();
// Core reports degraded states (respond-only fallback, tokenizer fallback, MCP errors) only via
// ILogger. Persist Warning+ to a file so they aren't silently lost, without cluttering the REPL.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(CaliperHome.LogsPath, "caliper.log"),
    LogLevel.Warning,
    TimeProvider.System));
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile(CaliperHome.ConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "CALIPER_")
    .AddInMemoryCollection(cliOverrides);

builder.Services.AddCaliperCore(builder.Configuration);
if (permissionPlan.ReadOnlyToolsOnly)
{
    builder.Services.PostConfigure<CaliperOptions>(options =>
    {
        options.EnabledTools = ["read_file", "list_dir", "glob", "grep", "load_skill"];
    });
}
// Prompt wiring per host mode (roadmap §3.2b):
// - Headless (--unattended one-shot, --serve scheduler): UnattendedPermissionPrompt outright —
//   it always denies and logs (Warning) instead of asking a human.
// - Interactive REPL (and attended one-shot): RoutingPermissionPrompt, which sends requests from
//   unattended runs (/schedule run builds RunSpec.Unattended = true) to the deny+report
//   UnattendedPermissionPrompt and everything else to the interactive ConsolePermissionPrompt.
//   PermissionGate itself is unchanged; the App keeps its own ApprovalService untouched.
if (unattended || serve)
{
    builder.Services.AddSingleton<IPermissionPrompt, UnattendedPermissionPrompt>();
}
else
{
    builder.Services.AddSingleton<ConsolePermissionPrompt>();
    builder.Services.AddSingleton<UnattendedPermissionPrompt>();
    builder.Services.AddSingleton<IPermissionPrompt>(sp => new RoutingPermissionPrompt(
        sp.GetRequiredService<ConsolePermissionPrompt>(),
        sp.GetRequiredService<UnattendedPermissionPrompt>()));
}

// The cron scheduler only ticks in the dedicated headless host; interactive sessions manage
// schedules via /schedule list|run without a background loop.
if (serve)
    builder.Services.AddHostedService<SchedulerHostedService>();

var host = builder.Build();

// Eagerly validate options.
var agentOpts = host.Services.GetRequiredService<IOptionsMonitor<CaliperOptions>>().CurrentValue;
var skillStore = host.Services.GetRequiredService<ISkillStore>();

AnsiConsole.MarkupLine("[bold cyan]Caliper[/] [dim]— lightweight agentic harness[/]");
AnsiConsole.MarkupLine($"[dim]Model: {agentOpts.Model}  Provider: {agentOpts.Provider}[/]");
AnsiConsole.MarkupLine($"[dim]Skills: {skillStore.List().Count}[/]");
if (!serve)
    AnsiConsole.MarkupLine("[dim]Type your message, or /quit to exit.[/]");
AnsiConsole.WriteLine();

var runner   = host.Services.GetRequiredService<AgentRunner>();
var conversations = host.Services.GetRequiredService<ConversationOrchestrator>();
var sessions = host.Services.GetRequiredService<ISessionStore>();
var tools = host.Services.GetRequiredService<IToolRegistry>();
var mcpHub = host.Services.GetRequiredService<IMcpHub>();
var memoryStore = host.Services.GetRequiredService<IMemoryStore>();
var caliperMdProvider = host.Services.GetRequiredService<ICaliperMdProvider>();
var contextManager = host.Services.GetRequiredService<IContextManager>();
var capabilityProvider = host.Services.GetRequiredService<IModelCapabilityProvider>();
var modelCatalog = host.Services.GetRequiredService<IModelCatalog>();
var runtimeSettings = host.Services.GetRequiredService<IRuntimeSettings>();
var permissionGate = host.Services.GetRequiredService<IPermissionGate>();
var configWriter = host.Services.GetRequiredService<IConfigWriter>();
var scheduleRunner = host.Services.GetRequiredService<ScheduleJobRunner>();
var timeProvider = host.Services.GetRequiredService<TimeProvider>();
var runStore = host.Services.GetRequiredService<IRunStore>();
var footer = new StatusFooter(runtimeSettings, mcpHub);

if (serve)
{
    // Headless scheduler host: connect MCP (jobs may use MCP tools), announce, then hand the
    // process to the Host lifetime — SchedulerHostedService ticks until Ctrl+C / SIGTERM, and
    // its stoppingToken chains the shutdown into every in-flight job run.
    try
    {
        await mcpHub.ConnectAllAsync(CancellationToken.None);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        AnsiConsole.MarkupLine($"[yellow]MCP connect failed: {Markup.Escape(ex.Message)}[/]");
    }

    RenderMcpStatus(mcpHub, showEmpty: false);
    var enabledSchedules = runtimeSettings.Caliper.Schedules.Count(schedule => schedule.Enabled);
    AnsiConsole.MarkupLine($"[green]Serving {enabledSchedules} schedule(s).[/] [dim]Press Ctrl+C to stop.[/]");
    await host.RunAsync();
    await mcpHub.DisposeAllAsync();
    return;
}

// One app-lifetime token plus a fresh per-run token each turn: the first Ctrl+C cancels the
// in-flight run and returns to the prompt; Ctrl+C at the idle prompt exits.
using var appCts = new CancellationTokenSource();
CancellationTokenSource? runCts = null;
var runActive = false;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (runActive && runCts is not null)
        runCts.Cancel();
    else
        appCts.Cancel();
};

try
{
    await mcpHub.ConnectAllAsync(appCts.Token);
}
catch (OperationCanceledException) when (appCts.IsCancellationRequested)
{
    await mcpHub.DisposeAllAsync();
    AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
    return;
}

RenderMcpStatus(mcpHub, showEmpty: false);

if (!string.IsNullOrWhiteSpace(resumeRunId))
{
    var resumeLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Caliper.Console.Resume");
    await RunResumeAsync(conversations, resumeRunId, oneShotPrint, footer, resumeLogger, appCts.Token);
    await mcpHub.DisposeAllAsync();
    return;
}

if (!string.IsNullOrWhiteSpace(oneShotPrompt))
{
    var oneShotSessionId = await sessions.CreateAsync(SessionTitle(oneShotPrompt), appCts.Token);
    var oneShotLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Caliper.Console.OneShot");
    await RunOneShotAsync(conversations, oneShotSessionId, oneShotPrompt, oneShotPrint, unattended, footer, oneShotLogger, appCts.Token);
    await mcpHub.DisposeAllAsync();
    return;
}

string? sessionId = null;

while (!appCts.Token.IsCancellationRequested)
{
    string? input;
    try
    {
        AnsiConsole.Markup("[bold green]>[/] ");
        input = Console.ReadLine();
    }
    catch (Exception)
    {
        break;
    }

    if (input is null) break;
    input = input.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.StartsWith('/'))
    {
        sessionId = await HandleSlashCommandAsync(input, sessionId, sessions, skillStore, tools, mcpHub, memoryStore, caliperMdProvider, contextManager, capabilityProvider, modelCatalog, runtimeSettings, permissionGate, configWriter, conversations, scheduleRunner, timeProvider, runStore, footer, appCts.Token);
        continue;
    }

    if (string.IsNullOrEmpty(sessionId))
        sessionId = await sessions.CreateAsync(SessionTitle(input), appCts.Token);

    var renderer = new EventRenderer(footer);
    runCts = CancellationTokenSource.CreateLinkedTokenSource(appCts.Token);
    runActive = true;
    try
    {
        await foreach (var evt in runner.RunAsync(sessionId, input, runCts.Token))
            renderer.Render(evt);
    }
    catch (OperationCanceledException)
    {
        if (appCts.IsCancellationRequested)
            break;
        AnsiConsole.MarkupLine("[yellow]Cancelled; back to prompt.[/]");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Unexpected error: {Markup.Escape(ex.Message)}[/]");
    }
    finally
    {
        runActive = false;
        runCts.Dispose();
        runCts = null;
    }
}

await mcpHub.DisposeAllAsync();
AnsiConsole.MarkupLine("[dim]Goodbye.[/]");

static async Task<string> HandleSlashCommandAsync(
    string input,
    string? currentSessionId,
    ISessionStore sessions,
    ISkillStore skillStore,
    IToolRegistry tools,
    IMcpHub mcpHub,
    IMemoryStore memoryStore,
    ICaliperMdProvider caliperMdProvider,
    IContextManager contextManager,
    IModelCapabilityProvider capabilityProvider,
    IModelCatalog modelCatalog,
    IRuntimeSettings runtimeSettings,
    IPermissionGate permissionGate,
    IConfigWriter configWriter,
    ConversationOrchestrator conversations,
    ScheduleJobRunner scheduleRunner,
    TimeProvider timeProvider,
    IRunStore runStore,
    StatusFooter footer,
    CancellationToken ct)
{
    var parsed = SlashCommandParser.Parse(input);
    var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    switch (parsed.Kind)
    {
        case SlashCommandKind.New:
        {
            // A new conversation must not inherit "allow for session" approvals from the last one.
            permissionGate.ResetSessionApprovals();
            var sessionId = await sessions.CreateAsync(null, ct);
            AnsiConsole.MarkupLine($"[green]Started new session:[/] {Markup.Escape(sessionId[..8])}");
            return sessionId;
        }

        case SlashCommandKind.Sessions:
        {
            var sessionList = await sessions.ListAsync(ct);
            if (sessionList.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No sessions yet.[/]");
                return currentSessionId ?? "";
            }

            var table = new Table()
                .RoundedBorder()
                .AddColumn("Id")
                .AddColumn("Title")
                .AddColumn("Created");
            foreach (var session in sessionList)
            {
                table.AddRow(
                    Markup.Escape(session.Id[..Math.Min(12, session.Id.Length)]),
                    Markup.Escape(session.Title ?? ""),
                    Markup.Escape(session.CreatedAt.ToLocalTime().ToString("g")));
            }

            AnsiConsole.Write(table);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Resume:
        {
            if (string.IsNullOrWhiteSpace(parsed.Argument))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /resume <session-id>[/]");
                return currentSessionId ?? "";
            }

            var requested = parsed.Argument;
            var matches = (await sessions.ListAsync(ct))
                .Where(s => s.Id.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count != 1)
            {
                var message = matches.Count == 0
                    ? "No matching session."
                    : "Session id prefix is ambiguous.";
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
                return currentSessionId ?? "";
            }

            var selected = matches[0];
            var history = await sessions.LoadAsync(selected.Id, ct);
            AnsiConsole.MarkupLine($"[green]Resumed session:[/] {Markup.Escape(selected.Id[..8])}");
            foreach (var message in history.Where(m => m.Kind == Caliper.Core.Models.MessageKind.Text))
            {
                var role = message.Role.ToString().ToLowerInvariant();
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(role)}:[/] {Markup.Escape(message.Content)}");
            }

            return selected.Id;
        }

        case SlashCommandKind.Skills:
        {
            var skills = skillStore.List();
            if (skills.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No skills found.[/]");
                return currentSessionId ?? "";
            }

            var table = new Table()
                .RoundedBorder()
                .AddColumn("Skill")
                .AddColumn("Description");
            foreach (var skill in skills)
                table.AddRow(Markup.Escape(skill.Name), Markup.Escape(skill.Description));
            AnsiConsole.Write(table);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Tools:
        {
            if (tools.Enabled.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No tools are enabled.[/]");
                return currentSessionId ?? "";
            }

            var table = new Table()
                .RoundedBorder()
                .AddColumn("Tool")
                .AddColumn("Description");
            foreach (var tool in tools.Enabled)
                table.AddRow(Markup.Escape(tool.Name), Markup.Escape(tool.Description));
            AnsiConsole.Write(table);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Mcp:
        {
            if (parsed.Argument.Equals("reconnect", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await mcpHub.ConnectAllAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("[dim]MCP reconnect cancelled.[/]");
                    return currentSessionId ?? "";
                }

                AnsiConsole.MarkupLine("[green]Reconnected MCP servers.[/]");
            }

            RenderMcpStatus(mcpHub, showEmpty: true);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Memory:
        {
            await RenderMemoryAsync(memoryStore, caliperMdProvider, runtimeSettings.Caliper, ct);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Compact:
        {
            if (string.IsNullOrWhiteSpace(currentSessionId))
            {
                AnsiConsole.MarkupLine("[yellow]No active session to compact.[/]");
                return currentSessionId ?? "";
            }

            var fit = await conversations.ForceCompactAsync(currentSessionId, ct);
            if (fit.Compacted)
                AnsiConsole.MarkupLine($"[green]Context compacted:[/] {fit.BeforeTokens ?? 0} → {fit.AfterTokens ?? 0} tokens");
            else
                AnsiConsole.MarkupLine("[dim]Context did not need compaction.[/]");
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Clear:
        {
            if (string.IsNullOrWhiteSpace(currentSessionId))
            {
                AnsiConsole.MarkupLine("[yellow]No active session to clear.[/]");
                return currentSessionId ?? "";
            }

            await sessions.AppendAsync(
                currentSessionId,
                new Caliper.Core.Models.ChatMessage(
                    Caliper.Core.Models.ChatRole.System,
                    Caliper.Core.Models.MessageKind.Summary,
                    AgentRunner.ContextResetMarker),
                ct);
            AnsiConsole.MarkupLine("[green]Context cleared; session transcript kept.[/]");
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Set:
        {
            var settingParts = parsed.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (settingParts.Length < 2)
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /set <key> <value>[/]");
                return currentSessionId ?? "";
            }

            if (runtimeSettings.TrySet(settingParts[0], settingParts[1], out var message))
            {
                AnsiConsole.MarkupLine($"[green]Updated:[/] {Markup.Escape(message)}");
                AnsiConsole.MarkupLine("[dim](applies to this session; run /config save to persist)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
            }
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Model:
        {
            if (string.IsNullOrWhiteSpace(parsed.Argument))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /model <slug>[/]");
                return currentSessionId ?? "";
            }

            if (await SwitchModelAsync(parsed.Argument, modelCatalog, capabilityProvider, runtimeSettings, ct))
                AnsiConsole.MarkupLine("[dim](applies to this session; run /config save to persist)[/]");
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Models:
        {
            await RenderModelsAsync(modelCatalog, ct);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Permissions:
        {
            if (!Enum.TryParse<PermissionMode>(parsed.Argument, ignoreCase: true, out var mode))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /permissions <AskAlways|Auto|Plan>[/]");
                return currentSessionId ?? "";
            }

            runtimeSettings.SetPermissionMode(mode);
            AnsiConsole.MarkupLine($"[green]Permission mode:[/] {mode}");
            AnsiConsole.MarkupLine("[dim](applies to this session; run /config save to persist)[/]");
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Config:
        {
            if (!string.Equals(parsed.Argument.Trim(), "save", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /config save[/]");
                return currentSessionId ?? "";
            }

            // Snapshot the live runtime/permission settings (which /set, /model, /permissions have
            // mutated) and persist them through the typed, validated config writer.
            var caliperResult = await configWriter.SaveCaliperAsync(runtimeSettings.Caliper, ct);
            if (!caliperResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Save failed:[/] {Markup.Escape(caliperResult.Error ?? "unknown error")}");
                return currentSessionId ?? "";
            }

            var permissionsResult = await configWriter.SavePermissionsAsync(runtimeSettings.Permissions, ct);
            if (!permissionsResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Save failed:[/] {Markup.Escape(permissionsResult.Error ?? "unknown error")}");
                return currentSessionId ?? "";
            }

            AnsiConsole.MarkupLine("[green]Settings saved to ~/.caliper/config.json.[/]");
            if (caliperResult.RestartRequired)
                AnsiConsole.MarkupLine("[dim](some changes take effect on next launch)[/]");
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Schedule:
        {
            var scheduleArgs = parsed.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var subcommand = scheduleArgs.Length > 0 ? scheduleArgs[0].ToLowerInvariant() : "";
            switch (subcommand)
            {
                case "list":
                    RenderScheduleList(runtimeSettings, scheduleRunner, timeProvider);
                    break;
                case "run" when scheduleArgs.Length == 2 && !string.IsNullOrWhiteSpace(scheduleArgs[1]):
                    await RunScheduleAsync(scheduleArgs[1].Trim(), runtimeSettings, scheduleRunner, footer, ct);
                    break;
                default:
                    AnsiConsole.MarkupLine("[yellow]Usage: /schedule list | /schedule run <name>[/]");
                    break;
            }

            return currentSessionId ?? "";
        }

        case SlashCommandKind.Runs:
        {
            await RenderRunsListAsync(runStore, ct);
            return currentSessionId ?? "";
        }

        case SlashCommandKind.Help:
        {
            RenderHelp();
            return currentSessionId ?? "";
        }

        default:
            AnsiConsole.MarkupLine($"[red]Unknown command: {Markup.Escape(parsed.Name.Length > 0 ? parsed.Name : parts[0])}[/]");
            return currentSessionId ?? "";
    }
}

static async Task RenderMemoryAsync(
    IMemoryStore memoryStore,
    ICaliperMdProvider caliperMdProvider,
    CaliperOptions agentOpts,
    CancellationToken ct)
{
    if (!agentOpts.Memory.Enabled)
    {
        AnsiConsole.MarkupLine("[dim]Memory is disabled.[/]");
        return;
    }

    var workingRoot = ResolveRoot(agentOpts.WorkingRoot);
    var projectScope = MemoryScope.Project(workingRoot);
    var entries = await memoryStore.RecallAsync(projectScope, query: null, ct);
    var projectDocument = await caliperMdProvider.ReadAsync(workingRoot, ct);

    if (entries.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]No memory entries found.[/]");
    }
    else
    {
        var table = new Table()
            .RoundedBorder()
            .AddColumn("Scope")
            .AddColumn("Key")
            .AddColumn("Value")
            .AddColumn("Updated");

        foreach (var entry in entries)
        {
            table.AddRow(
                Markup.Escape(entry.Scope == MemoryScope.Global ? "global" : "project"),
                Markup.Escape(entry.Key),
                Markup.Escape(entry.Value),
                Markup.Escape(entry.UpdatedAt.ToLocalTime().ToString("g")));
        }

        AnsiConsole.Write(table);
    }

    if (!string.IsNullOrWhiteSpace(projectDocument.Content))
        AnsiConsole.MarkupLine($"[dim]CALIPER.md: {Markup.Escape(projectDocument.Path)}{(projectDocument.Truncated ? " (truncated)" : "")}[/]");
    else
        AnsiConsole.MarkupLine("[dim]CALIPER.md: not found.[/]");
}

static void RenderMcpStatus(IMcpHub mcpHub, bool showEmpty)
{
    if (mcpHub.Status.Count == 0)
    {
        if (showEmpty)
            AnsiConsole.MarkupLine("[dim]No MCP servers configured.[/]");
        return;
    }

    var table = new Table()
        .RoundedBorder()
        .AddColumn("Server")
        .AddColumn("Status")
        .AddColumn("Tools")
        .AddColumn("Error");

    foreach (var status in mcpHub.Status)
    {
        table.AddRow(
            Markup.Escape(status.Name),
            status.Connected ? "[green]connected[/]" : "[red]failed[/]",
            status.ToolCount.ToString(),
            Markup.Escape(status.Error ?? ""));
    }

    AnsiConsole.Write(table);
}

static async Task<bool> SwitchModelAsync(
    string slug,
    IModelCatalog modelCatalog,
    IModelCapabilityProvider capabilityProvider,
    IRuntimeSettings runtimeSettings,
    CancellationToken ct)
{
    IReadOnlyList<ModelCatalogEntry> models = [];
    try
    {
        models = await modelCatalog.ListAsync(ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        AnsiConsole.MarkupLine($"[yellow]Could not validate model catalog: {Markup.Escape(ex.Message)}[/]");
    }

    var match = models.FirstOrDefault(model => string.Equals(model.Id, slug, StringComparison.OrdinalIgnoreCase));
    if (models.Count > 0 && match is null)
    {
        AnsiConsole.MarkupLine($"[yellow]Model not found in catalog:[/] {Markup.Escape(slug)}");
        return false;
    }

    var capabilities = match?.Capabilities ?? await capabilityProvider.GetAsync(slug, ct);
    runtimeSettings.SetModel(slug);
    AnsiConsole.MarkupLine($"[green]Model:[/] {Markup.Escape(slug)}");
    if (!capabilities.SupportsTools)
        AnsiConsole.MarkupLine("[yellow]Selected model is not known to support native tools; Auto mode will respond without tools.[/]");
    return true;
}

static async Task RenderModelsAsync(IModelCatalog modelCatalog, CancellationToken ct)
{
    IReadOnlyList<ModelCatalogEntry> models;
    try
    {
        models = await modelCatalog.ListAsync(ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        AnsiConsole.MarkupLine($"[red]Could not load models:[/] {Markup.Escape(ex.Message)}");
        return;
    }

    if (models.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]No model catalog is available for the active provider.[/]");
        return;
    }

    var table = new Table()
        .RoundedBorder()
        .AddColumn("Model")
        .AddColumn("Context")
        .AddColumn("Tools")
        .AddColumn("Reasoning");

    foreach (var model in models.Take(80))
    {
        table.AddRow(
            Markup.Escape(model.Id),
            model.Capabilities.ContextWindowTokens.ToString(),
            model.Capabilities.SupportsTools ? "yes" : "no",
            model.Capabilities.SupportsReasoning ? "yes" : "no");
    }

    AnsiConsole.Write(table);
    if (models.Count > 80)
        AnsiConsole.MarkupLine($"[dim]Showing 80 of {models.Count} models.[/]");
}

static void RenderScheduleList(
    IRuntimeSettings runtimeSettings,
    ScheduleJobRunner scheduleRunner,
    TimeProvider timeProvider)
{
    var schedules = runtimeSettings.Caliper.Schedules;
    if (schedules.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]No schedules configured. Add entries under Caliper:Schedules in ~/.caliper/config.json.[/]");
        return;
    }

    // The scheduler service itself does not run in REPL mode, so the next occurrence is computed
    // on demand from live config, and "last result" only reflects manual runs from this session.
    var now = timeProvider.GetUtcNow();
    var table = new Table()
        .RoundedBorder()
        .AddColumn("Name")
        .AddColumn("Enabled")
        .AddColumn("Cron")
        .AddColumn("Next occurrence")
        .AddColumn("Last result");

    foreach (var job in schedules)
    {
        string next;
        if (!job.Enabled)
        {
            next = "—";
        }
        else
        {
            var occurrence = ScheduleCron.GetNextOccurrence(job, now, out var error);
            next = occurrence is { } wakeAt
                ? wakeAt.ToLocalTime().ToString("g")
                : error is null ? "never" : $"invalid — {error}";
        }

        var last = scheduleRunner.GetLastResult(job.Name);
        var lastText = last is null
            ? "—"
            : $"{last.Reason?.ToString() ?? (last.Error is null ? "?" : "Error")}, {last.DenialCount} denial(s) at {last.CompletedAt.ToLocalTime():g}";

        table.AddRow(
            Markup.Escape(job.Name),
            job.Enabled ? "yes" : "no",
            Markup.Escape(job.Cron),
            Markup.Escape(next),
            Markup.Escape(lastText));
    }

    AnsiConsole.Write(table);
}

static async Task RunScheduleAsync(
    string name,
    IRuntimeSettings runtimeSettings,
    ScheduleJobRunner scheduleRunner,
    StatusFooter footer,
    CancellationToken ct)
{
    var job = runtimeSettings.Caliper.Schedules
        .FirstOrDefault(schedule => string.Equals(schedule.Name, name, StringComparison.OrdinalIgnoreCase));
    if (job is null)
    {
        var known = string.Join(", ", runtimeSettings.Caliper.Schedules.Select(schedule => schedule.Name));
        AnsiConsole.MarkupLine($"[red]Unknown schedule: {Markup.Escape(name)}[/]");
        if (known.Length > 0)
            AnsiConsole.MarkupLine($"[dim]Known schedules: {Markup.Escape(known)}[/]");
        return;
    }

    if (!job.Enabled)
        AnsiConsole.MarkupLine("[dim]Schedule is disabled for the cron scheduler; running it manually anyway.[/]");

    // Identical unattended path to a --serve tick: ScheduleJobRunner builds the RunSpec with
    // Unattended = true, so the RoutingPermissionPrompt denies+records every prompt while the
    // run's events still render live below.
    AnsiConsole.MarkupLine($"[green]Running schedule:[/] {Markup.Escape(job.Name)} [dim](unattended: permission prompts deny + report)[/]");
    var renderer = new EventRenderer(footer);
    var outcome = await scheduleRunner.RunJobAsync(
        job,
        concurrencyGate: null,
        (evt, _) =>
        {
            renderer.Render(evt);
            return ValueTask.CompletedTask;
        },
        ct);

    if (outcome.Skipped)
    {
        AnsiConsole.MarkupLine("[yellow]Skipped: a previous occurrence of this job is still running.[/]");
        return;
    }

    if (outcome.DenialCount > 0)
        AnsiConsole.MarkupLine($"[yellow]{outcome.DenialCount} action(s) denied (unattended policy).[/]");
    if (outcome.Error is { } jobError)
        AnsiConsole.MarkupLine($"[red]Job error: {Markup.Escape(jobError)}[/]");
    else
        AnsiConsole.MarkupLine($"[dim]Job finished: {outcome.Reason?.ToString() ?? "?"}[/]");
}

static async Task RenderRunsListAsync(IRunStore runStore, CancellationToken ct)
{
    var runs = await runStore.ListRecentAsync(20, ct);
    if (runs.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]No tracked runs yet. One-shot, --unattended, scheduled, and subagent runs are tracked; interactive REPL turns are not.[/]");
        return;
    }

    var table = new Table()
        .RoundedBorder()
        .AddColumn("Run")
        .AddColumn("Session")
        .AddColumn("Job")
        .AddColumn("Status")
        .AddColumn("Step")
        .AddColumn("Updated");

    foreach (var run in runs)
    {
        table.AddRow(
            Markup.Escape(run.RunId[..Math.Min(8, run.RunId.Length)]),
            Markup.Escape(run.SessionId[..Math.Min(8, run.SessionId.Length)]),
            Markup.Escape(run.JobName ?? "—"),
            Markup.Escape(run.Resumed ? $"{run.Status} (resumed)" : run.Status.ToString()),
            $"{run.Step}/{run.MaxSteps}",
            Markup.Escape(run.UpdatedAt.ToLocalTime().ToString("g")));
    }

    AnsiConsole.Write(table);
}

static void RenderHelp()
{
    var table = new Table()
        .RoundedBorder()
        .AddColumn("Command")
        .AddColumn("Description");

    foreach (var item in SlashCommandParser.Help)
        table.AddRow(Markup.Escape(item.Command), Markup.Escape(item.Description));

    AnsiConsole.Write(table);
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            // Guard against a missing value swallowing the next flag (e.g. "--prompt --print").
            var value = args[i + 1];
            return value.StartsWith("--", StringComparison.Ordinal) ? null : value;
        }
    }

    return null;
}

static bool HasOption(string[] args, string name) =>
    args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

static string SessionTitle(string firstMessage) =>
    Caliper.Core.SessionTitle.FromPrompt(firstMessage);

static string ResolveRoot(string root)
{
    return Path.GetFullPath(LocalPath.ResolveHome(root));
}

static async Task RunOneShotAsync(
    ConversationOrchestrator conversations,
    string sessionId,
    string prompt,
    bool printOnly,
    bool unattended,
    StatusFooter footer,
    ILogger oneShotLogger,
    CancellationToken ct)
{
    var renderer = new EventRenderer(footer, printOnly: true);
    var result = await conversations.RunToCompletionAsync(
        sessionId,
        prompt,
        (evt, _) =>
        {
            if (printOnly)
                renderer.Render(evt);
            return ValueTask.CompletedTask;
        },
        ct);

    if (result.Error is not null)
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(result.Error)}[/]");
    else if (!printOnly && result.AssistantMessage is not null)
        Console.WriteLine(result.AssistantMessage);

    // Unattended contract: deny + report, never silent-drop. Each denial was already logged
    // (Warning) by UnattendedPermissionPrompt as it happened; this is the run-level summary —
    // both to the terminal (so a human watching stderr sees it immediately) and, via the
    // aggregate Warning below, to the same file log.
    if (unattended && result.Denials.Count > 0)
    {
        oneShotLogger.LogWarning("Unattended run denied {Count} action(s).", result.Denials.Count);

        Console.Error.WriteLine($"{result.Denials.Count} action(s) denied (unattended):");
        foreach (var denial in result.Denials)
        {
            var reasonSuffix = string.IsNullOrWhiteSpace(denial.Reason) ? "" : $" — {denial.Reason}";
            Console.Error.WriteLine($"  {denial.Tool}{reasonSuffix}");
        }
    }

    // ExitCode (not Environment.Exit) so the caller's cleanup (MCP disposal) still runs.
    Environment.ExitCode = OneShotExitCode.From(result.Error, result.Denials.Count, reportDenialsInExitCode: unattended);
}

static async Task RunResumeAsync(
    ConversationOrchestrator conversations,
    string runId,
    bool printOnly,
    StatusFooter footer,
    ILogger resumeLogger,
    CancellationToken ct)
{
    var renderer = new EventRenderer(footer, printOnly: true);
    var result = await conversations.ResumeAsync(
        runId,
        (evt, _) =>
        {
            if (printOnly)
                renderer.Render(evt);
            return ValueTask.CompletedTask;
        },
        ct);

    if (result.Error is not null)
    {
        AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(result.Error)}[/]");
        resumeLogger.LogWarning("Resume failed for run '{RunId}': {Error}", runId, result.Error);
    }
    else if (!printOnly && result.AssistantMessage is not null)
    {
        Console.WriteLine(result.AssistantMessage);
    }

    // Same deny + report contract as a fresh unattended run: a resumed job/unattended run keeps
    // its original Unattended flag (RunSpec.Unattended, carried on the runs row), so its denials
    // are reported here exactly like RunOneShotAsync's.
    if (result.Denials.Count > 0)
    {
        resumeLogger.LogWarning("Resumed run denied {Count} action(s).", result.Denials.Count);

        Console.Error.WriteLine($"{result.Denials.Count} action(s) denied:");
        foreach (var denial in result.Denials)
        {
            var reasonSuffix = string.IsNullOrWhiteSpace(denial.Reason) ? "" : $" — {denial.Reason}";
            Console.Error.WriteLine($"  {denial.Tool}{reasonSuffix}");
        }
    }

    // A resume is always non-interactive-style (one-shot entry point), so denials count toward
    // the exit code regardless of the original run's Unattended flag — nobody watched them happen.
    Environment.ExitCode = OneShotExitCode.From(result.Error, result.Denials.Count, reportDenialsInExitCode: true);
}

sealed class ConsolePermissionPrompt : IPermissionPrompt
{
    public async Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || Console.IsInputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            return PermissionDecision.Deny;

        var choices = new List<string> { "Allow once", "Deny" };
        var allowSession = request.Reason is null ||
            !request.Reason.StartsWith(PermissionGate.DenylistReasonPrefix, StringComparison.Ordinal);
        if (allowSession)
            choices.Insert(1, "Allow for session");

        var body = $"[bold]{Markup.Escape(request.Tool)}[/] [dim]({request.Effect})[/]";
        if (request.Arguments.TryGetProperty("command", out var command) && command.ValueKind == System.Text.Json.JsonValueKind.String)
            body += $"\n[dim]command:[/] {Markup.Escape(command.GetString() ?? "")}";
        if (!string.IsNullOrWhiteSpace(request.Reason))
            body += $"\n[dim]{Markup.Escape(request.Reason)}[/]";

        AnsiConsole.Write(new Panel(new Markup(body))
            .Header("permission required")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow));

        string selected;
        try
        {
            // Cancellable overload so Ctrl+C during the prompt cancels instead of being swallowed.
            selected = await new SelectionPrompt<string>()
                .Title("Allow this action?")
                .AddChoices(choices)
                .ShowAsync(AnsiConsole.Console, ct);
        }
        catch (OperationCanceledException)
        {
            return PermissionDecision.Deny;
        }
        catch (InvalidOperationException)
        {
            return PermissionDecision.Deny;
        }

        return selected switch
        {
            "Allow once" => PermissionDecision.Allow,
            "Allow for session" => PermissionDecision.AllowForSession,
            _ => PermissionDecision.Deny,
        };
    }
}
