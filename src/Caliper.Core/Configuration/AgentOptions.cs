// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Core.Configuration;

public sealed class AgentOptions
{
    public string ModelName { get; set; } = "local-q1";
    public int ContextWindowTokens { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 1024;
    public int SafetyMarginTokens { get; set; } = 512;
    public int MaxSteps { get; set; } = 8;
    public int DuplicateCallLimit { get; set; } = 2;
    public int ToolTimeoutSeconds { get; set; } = 30;
    public int ToolMaxRetries { get; set; } = 2;
    public int ToolOutputMaxChars { get; set; } = 8000;
    public double Temperature { get; set; }
    public int? Seed { get; set; }
    public TurnStrategyKind TurnStrategy { get; set; } = TurnStrategyKind.TwoPhase;
    public bool DisableThinking { get; set; }
    public string SkillsDirectory { get; set; } = "~/.caliper/skills";
    public IList<string> EnabledTools { get; set; } = ["search", "fetch_url"];
    public SkillSelectorKind SkillSelector { get; set; } = SkillSelectorKind.None;
    public int MaxSurfacedSkills { get; set; } = 12;
}
