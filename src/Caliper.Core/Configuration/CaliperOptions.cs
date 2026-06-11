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
        "write_file", "edit_file", "bash", "powershell", "memory", "load_skill",
    ];
    public string WorkingRoot { get; set; } = ".";
    public string SkillsDirectory { get; set; } = "~/.caliper/skills";
    public SkillSelectorKind SkillSelector { get; set; } = SkillSelectorKind.None;
    public int MaxSurfacedSkills { get; set; } = 12;
    public ContextOptions Context { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
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
}

public sealed class OpenRouterOptions
{
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";
    public string AppTitle { get; set; } = "Caliper";
    public string? AppReferer { get; set; }
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
