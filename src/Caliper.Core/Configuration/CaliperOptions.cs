// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json.Serialization;

namespace Caliper.Core.Configuration;

public sealed class CaliperOptions
{
    public string Provider { get; set; } = "OpenRouter";
    public string Model { get; set; } = "openrouter/model-slug";
    public string? SummarizerModel { get; set; }
    public int MaxSteps { get; set; } = 25;
    public int DuplicateCallLimit { get; set; } = 2;
    public int ToolTimeoutSeconds { get; set; } = 60;
    public int ToolMaxRetries { get; set; } = 2;
    public int ToolOutputMaxChars { get; set; } = 16000;
    public double Temperature { get; set; }
    public int? Seed { get; set; }
    public ReasoningOptions Reasoning { get; set; } = new();
    public TurnStrategyKind TurnStrategy { get; set; } = TurnStrategyKind.Auto;
    public IList<string> EnabledTools { get; set; } =
    [
        "search", "fetch_url", "read_file", "list_dir", "glob", "grep",
        "write_file", "edit_file", "bash", "powershell", "memory", "load_skill", "task",
    ];
    public string WorkingRoot { get; set; } = ".";
    public string SkillsDirectory { get; set; } = "~/.caliper/skills";
    public SkillSelectorKind SkillSelector { get; set; } = SkillSelectorKind.None;
    public int MaxSurfacedSkills { get; set; } = 12;
    public ContextOptions Context { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
    public SubagentsOptions Subagents { get; set; } = new();
    public SchedulerOptions Scheduler { get; set; } = new();
    public IList<ScheduleOptions> Schedules { get; set; } = [];
    public ExecutionOptions Execution { get; set; } = new();
}

/// <summary>
/// Roadmap §3.2b scheduler-wide knobs (<c>Caliper:Scheduler</c>). <see cref="MaxConcurrentJobs"/>
/// is bound once when <c>SchedulerHostedService</c> starts (its cross-job semaphore is sized
/// then), so changing it requires restarting the <c>--serve</c> process; see
/// <c>ConfigWriter.SaveCaliperAsync</c>'s restart-required bookkeeping.
/// </summary>
public sealed class SchedulerOptions
{
    /// <summary>How many scheduled jobs may run concurrently. Default 1 keeps resource use predictable.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;
}

/// <summary>
/// One scheduled job (an entry in the <c>Caliper:Schedules</c> list, roadmap §3.2b). The whole
/// list is a live seam: the scheduler re-reads it from <c>IRuntimeSettings.Caliper.Schedules</c>
/// on every tick, so add/edit/remove/enable changes apply without a restart — see
/// <c>ConfigWriter.SaveSchedulesAsync</c>.
/// </summary>
public sealed class ScheduleOptions
{
    /// <summary>Unique (case-insensitive) job name; also the session-title tag (<c>[job] name</c>).</summary>
    public string Name { get; set; } = "";

    /// <summary>Standard 5-field cron expression, parsed by Cronos.</summary>
    public string Cron { get; set; } = "";

    /// <summary>"local" (default) or a system time zone id resolvable by <c>TimeZoneInfo.FindSystemTimeZoneById</c>.</summary>
    public string TimeZone { get; set; } = "local";

    /// <summary>The prompt the job run starts from.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Per-job working root (<c>RunSpec.WorkingRoot</c>). Null falls back to <c>Caliper.WorkingRoot</c>.</summary>
    public string? WorkingRoot { get; set; }

    /// <summary>Model slug for job runs. Null falls back to the current default model.</summary>
    public string? Model { get; set; }

    /// <summary>Step budget for job runs. Null falls back to <c>Caliper.MaxSteps</c>.</summary>
    public int? MaxSteps { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Per-job permission overlay (<c>RunSpec.PermissionsOverlay</c>). Null falls back to the
    /// global <c>Permissions</c> section. <c>Mode</c> must be set explicitly to <c>Auto</c> for
    /// <c>ShellAutoAllowlist</c>/<c>AutoAllowFileRoots</c> to take effect — a job overlay left at
    /// the class default (<c>AskAlways</c>) prompts for every side effect, and under the
    /// unattended prompt every prompt is a deny, so the lists would silently do nothing.
    /// <c>CaliperOptionsValidator.ValidateSchedules</c> (A9) enforces this: a saved overlay that
    /// sets either list under a non-<c>Auto</c> <c>Mode</c> fails validation rather than
    /// deploying an inert configuration. The global shell denylist is always merged in by
    /// <c>PermissionGate</c> regardless.
    /// </summary>
    public PermissionsOptions? Permissions { get; set; }
}

/// <summary>
/// Roadmap §3.1 subagents config (<c>Caliper:Subagents</c>). Read fresh per <c>task</c> tool
/// invocation via <c>IRuntimeSettings.Caliper.Subagents</c> — profiles, limits, and the default
/// profile are all a live seam; see <c>ConfigWriter.SaveSubagentsAsync</c>.
/// </summary>
public sealed class SubagentsOptions
{
    /// <summary>Parent runs are depth 0; a run at depth == MaxDepth cannot spawn another child.</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Children a single run may spawn, counted per run (see <c>SubagentRunState</c>).</summary>
    public int MaxChildrenPerRun { get; set; } = 8;

    /// <summary>Profile name used when the <c>task</c> call omits <c>profile</c>.</summary>
    public string DefaultProfile { get; set; } = "research";

    /// <summary>Wall-clock budget for one child run; also <c>SubagentTool.ToolTimeoutOverride</c>.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    public IDictionary<string, SubagentProfileOptions> Profiles { get; set; } =
        new Dictionary<string, SubagentProfileOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["research"] = new SubagentProfileOptions
            {
                EnabledTools = ["read_file", "list_dir", "glob", "grep", "search", "fetch_url", "load_skill"],
                MaxSteps = 15,
            },
            ["worker"] = new SubagentProfileOptions
            {
                EnabledTools = ["read_file", "list_dir", "glob", "grep", "edit_file", "write_file", "bash", "powershell", "load_skill"],
                MaxSteps = 25,
            },
        };
}

/// <summary>
/// A named, host-defined tool grant a <c>task</c> call may select by name (never compose its own —
/// roadmap §7 Q1). <see cref="Mode"/> only ever tightens the parent's effective permission mode
/// (restrict-only overlay); see <c>SubagentTool</c>'s restrictiveness ordering.
/// </summary>
public sealed class SubagentProfileOptions
{
    public IList<string> EnabledTools { get; set; } = [];
    public int MaxSteps { get; set; } = 15;

    /// <summary>Null means "don't tighten beyond the parent's own effective mode".</summary>
    public PermissionMode? Mode { get; set; }
}

/// <summary>
/// Roadmap §3.3 sandboxed shell execution (<c>Caliper:Execution</c>). Read fresh per shell-tool
/// invocation via <c>IRuntimeSettings.Caliper.Execution</c> — the whole section is a live seam
/// (see <c>ConfigWriter.SaveExecutionAsync</c>), so flipping <see cref="Backend"/> between
/// <see cref="ExecutionBackendKind.Host"/> and <see cref="ExecutionBackendKind.Container"/>, or
/// tuning the container knobs, applies to the very next shell call without a restart. Both
/// backends are always constructed (DI registers both singletons unconditionally); only the live
/// <see cref="Backend"/> read decides which one a given call uses, which is what makes the live
/// seam safe — there is no "swap the wiring" step that could race a concurrent run.
/// </summary>
public sealed class ExecutionOptions
{
    /// <summary>
    /// <see cref="ExecutionBackendKind.Host"/> (default) runs shell commands directly on the host,
    /// exactly as before this feature existed. <see cref="ExecutionBackendKind.Container"/> routes
    /// them through <c>docker run</c> (Linux containers only; see <see cref="Image"/>'s remarks) —
    /// fail-closed: if Docker is unavailable, shell tools return a failed <c>ToolResult</c> rather
    /// than silently falling back to the host.
    /// </summary>
    public ExecutionBackendKind Backend { get; set; } = ExecutionBackendKind.Host;

    /// <summary>
    /// The container image `docker run` uses. Only consulted when <see cref="Backend"/> is
    /// <see cref="ExecutionBackendKind.Container"/>. Requires Docker Desktop with the WSL2/Linux
    /// containers backend on Windows — Windows containers are not supported, and the
    /// <c>powershell</c> tool is rejected outright under the container backend (bash only in v1).
    /// </summary>
    public string Image { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    /// <summary>
    /// Container network mode. <see cref="ExecutionNetworkKind.None"/> (default) gives the
    /// container no network access at all — the setting that makes a broad shell allowlist safe to
    /// pair with the container backend (see <c>UnattendedAllowlistGuard</c>: a bare <c>"*"</c>
    /// entry in <c>ShellAutoAllowlist</c> is rejected unless <see cref="Backend"/> is Container).
    /// </summary>
    public ExecutionNetworkKind Network { get; set; } = ExecutionNetworkKind.None;

    /// <summary>CPU limit passed to `docker run --cpus`. Fractional values are allowed.</summary>
    public double Cpus { get; set; } = 2;

    /// <summary>Memory limit in megabytes, passed to `docker run --memory {N}m`.</summary>
    public int MemoryMb { get; set; } = 4096;

    /// <summary>
    /// The `docker run --user` value. Defaults to a non-root uid so a command running inside the
    /// container can't write as root onto the bind-mounted working root.
    /// </summary>
    public string User { get; set; } = "1000";
}

// A8: string enum in config.json for hand-editability. JsonStringEnumConverter<T> is AOT-safe and,
// by default, still accepts an integer value on read — so a pre-existing config.json written with
// the old int-valued form keeps loading (see CaliperOptionsEnumSerializationTests).
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionBackendKind>))]
public enum ExecutionBackendKind
{
    Host,
    Container,
}

// A8: see ExecutionBackendKind's remark — same string-enum rationale.
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionNetworkKind>))]
public enum ExecutionNetworkKind
{
    None,
    Bridge,
}

public sealed class ReasoningOptions
{
    public string Effort { get; set; } = "medium";
    public bool Exclude { get; set; }
}

public sealed class ContextOptions
{
    public bool AutoCompact { get; set; } = true;
    public double CompactAtFraction { get; set; } = 0.8;
    public int KeepRecentTurns { get; set; } = 8;
    public int ReservedOutputTokens { get; set; } = 4096;
}

public sealed class MemoryOptions
{
    public bool Enabled { get; set; } = true;
    public string GlobalDir { get; set; } = "~/.caliper/memory";
    public string ProjectFile { get; set; } = "CALIPER.md";
}

public sealed class ProvidersOptions
{
    public OpenRouterOptions OpenRouter { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public OpenAICodexOptions OpenAICodex { get; set; } = new();
}

public sealed class OpenRouterOptions
{
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";
    public string AppTitle { get; set; } = "Caliper";
    public string? AppReferer { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class GeminiOptions
{
    // Gemini's OpenAI-compatible surface; Caliper talks to it via the same OpenAI client used
    // for OpenRouter, so no dedicated Gemini SDK or wire format is needed.
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";
    public string? ApiKey { get; set; }
}

public sealed class OpenAIOptions
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string? Organization { get; set; }
    public string? Project { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class OpenAICodexOptions
{
    public string Endpoint { get; set; } = "https://chatgpt.com/backend-api/codex";
}

public sealed class PermissionsOptions
{
    public PermissionMode Mode { get; set; } = PermissionMode.AskAlways;
    public bool RememberApprovals { get; set; } = true;
    public IList<string> ShellAutoAllowlist { get; set; } =
        ["git status", "git diff --", "dotnet build", "dotnet test"];
    public IList<string> ShellDenylist { get; set; } =
        ["rm -rf", "sudo ", "mkfs", "dd ", ":(){", "curl", "wget", "Remove-Item -Recurse", "Format-Volume", "reg delete"];
    public IList<string> AutoAllowFileRoots { get; set; } = [];
}

// A8: see ExecutionBackendKind's remark — same string-enum rationale. PermissionMode is used only
// in config DTOs (PermissionsOptions.Mode, SubagentProfileOptions.Mode) — never in a persisted
// session/transcript payload or the model-facing wire protocol — so re-shaping its JSON form here
// is safe.
[JsonConverter(typeof(JsonStringEnumConverter<PermissionMode>))]
public enum PermissionMode
{
    AskAlways,
    Auto,
    Plan,
}

public sealed class McpOptions
{
    public IDictionary<string, McpServerOptions> Servers { get; set; } =
        new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpServerOptions
{
    public string Type { get; set; } = "stdio";
    public string? Url { get; set; }
    public string? Command { get; set; }
    public IList<string> Args { get; set; } = [];
    public string? BearerToken { get; set; }
    public IDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
