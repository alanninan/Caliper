// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
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
