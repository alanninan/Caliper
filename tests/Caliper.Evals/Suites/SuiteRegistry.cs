// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.Evals.Suites;

internal static class SuiteRegistry
{
    private static readonly Dictionary<string, Func<IReadOnlyList<EvalCase>>> s_suites =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tool-calling"] = ToolCallingSuite.Cases,
            ["skills"] = SkillsSuite.Cases,
            ["context"] = ContextSuite.Cases,
            ["permissions"] = PermissionSuite.Cases,
            ["mcp"] = McpSuite.Cases,
            ["edit-file"] = EditFileSuite.Cases,
            ["compaction"] = CompactionSuite.Cases,
            ["subagents"] = SubagentSuite.Cases,
        };

    internal static IReadOnlyDictionary<string, Func<IReadOnlyList<EvalCase>>> All => s_suites;

    internal static bool TryGet(string name, out IReadOnlyList<EvalCase> cases)
    {
        if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
        {
            cases = s_suites.SelectMany(suite => suite.Value()).ToList();
            return true;
        }

        if (s_suites.TryGetValue(name, out var factory))
        {
            cases = factory();
            return true;
        }
        cases = [];
        return false;
    }
}
