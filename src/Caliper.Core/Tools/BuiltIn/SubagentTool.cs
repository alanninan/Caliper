// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caliper.Core.Tools.BuiltIn;

/// <summary>
/// The <c>task</c> tool (roadmap §3.1): delegates a scoped task to a subagent that runs its own
/// bounded loop in an isolated child session and returns a folded summary. Profiles are host-defined
/// (<c>CaliperOptions.Subagents.Profiles</c>) and selected by name only — the model can never
/// compose its own tool grant (roadmap §7 Q1).
/// </summary>
public sealed class SubagentTool(
    IServiceProvider services,
    ISessionStore sessions,
    IRuntimeSettings runtimeSettings,
    ILogger<SubagentTool> logger) : ITool
{
    private const int TitleMaxLength = 60;

    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["prompt"],
          "properties": {
            "prompt": { "type": "string" },
            "profile": { "type": "string" },
            "title": { "type": "string", "maxLength": 60 }
          }
        }
        """).RootElement.Clone();

    public string Name => "task";
    public string Description =>
        "Delegate a scoped task to a subagent that runs in its own isolated session and returns a summary. " +
        "Choose profile by name only (e.g. \"research\" or \"worker\"); you cannot invent a custom tool grant.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.Execute;

    // Timeout carve-out (roadmap §3.1): a whole child run routinely exceeds the generic per-tool
    // ToolTimeoutSeconds. Re-read live so a config change applies to the very next spawn.
    public TimeSpan? ToolTimeoutOverride =>
        TimeSpan.FromSeconds(Math.Max(1, runtimeSettings.Caliper.Subagents.TimeoutSeconds));

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        var caliperOpts = runtimeSettings.Caliper;
        var subagentOpts = caliperOpts.Subagents;

        var prompt = FileToolHelpers.GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return new ToolResult(false, "Missing required argument: prompt.");

        var requestedProfile = FileToolHelpers.GetString(arguments, "profile");
        var profileName = string.IsNullOrWhiteSpace(requestedProfile) ? subagentOpts.DefaultProfile : requestedProfile;
        if (!TryResolveProfile(subagentOpts, profileName, out var profile))
        {
            var validNames = string.Join(", ", subagentOpts.Profiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return new ToolResult(false, $"Unknown subagent profile '{profileName}'. Valid profiles: {validNames}.");
        }

        // Guard 1: depth. Parents are depth 0; a run at depth == MaxDepth cannot spawn another child.
        if (ctx.SubagentDepth >= subagentOpts.MaxDepth)
        {
            return new ToolResult(
                false,
                $"Cannot spawn a subagent: maximum nesting depth ({subagentOpts.MaxDepth}) reached at depth {ctx.SubagentDepth}.");
        }

        // Guard 2: per-run child count. See SubagentRunState's doc comment for why this counter is
        // scoped to "this run" (threaded through ToolContext) rather than a process-lifetime map.
        var spawned = ctx.SubagentState.IncrementAndGetChildCount();
        if (spawned > subagentOpts.MaxChildrenPerRun)
        {
            return new ToolResult(
                false,
                $"Cannot spawn a subagent: this run already spawned the maximum of {subagentOpts.MaxChildrenPerRun} (MaxChildrenPerRun) child agent(s).");
        }

        var title = FileToolHelpers.GetString(arguments, "title");
        var childTitle = $"Subagent: {Truncate(string.IsNullOrWhiteSpace(title) ? prompt : title, TitleMaxLength)}";

        var childSummary = await sessions.CreateWithSummaryAsync(childTitle, ctx.SessionId, ct).ConfigureAwait(false);
        ctx.Emit(new SubagentStarted(ctx.CallId, childSummary.Id, childTitle));

        var childOverlay = BuildChildOverlay(ctx.PermissionsOverlay, runtimeSettings.Permissions, profile.Mode);
        var childSpec = new RunSpec(childSummary.Id, prompt)
        {
            ToolFilter = [.. profile.EnabledTools],
            MaxSteps = profile.MaxSteps,
            SubagentDepth = ctx.SubagentDepth + 1,
            PermissionsOverlay = childOverlay,
            // Children inherit the parent run's working root (roadmap §3.1) — for a scheduled
            // job's subagent that's the job root, not the global one — and its unattended flag,
            // so a job's children keep routing prompts to the deny+report path (roadmap §3.2a:
            // "subagents under unattended inherit the same prompt + overlay").
            WorkingRoot = ctx.WorkingRoot,
            Unattended = ctx.Unattended,
        };

        // Belt-and-suspenders alongside ToolTimeoutOverride: DispatchWithRetry already bounds `ct`
        // to Subagents.TimeoutSeconds via the carve-out, but the child run is given its own
        // explicitly-linked, explicitly-timed token so its behavior doesn't depend on that carve-out
        // being wired correctly elsewhere.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, subagentOpts.TimeoutSeconds)));

        var steps = 0;
        var toolsInvoked = 0;
        ValueTask CountEvents(AgentEvent evt, CancellationToken _)
        {
            switch (evt)
            {
                case TurnStarted turnStarted:
                    steps = turnStarted.Step;
                    break;
                case ToolInvoked:
                    toolsInvoked++;
                    break;
            }
            return ValueTask.CompletedTask;
        }

        ConversationRunResult result;
        try
        {
            // Roadmap §3.1 DI-cycle note: SubagentTool must not constructor-inject
            // IConversationOrchestrator — ToolRegistry's constructor enumerates every ITool
            // singleton, and ConversationOrchestrator/AgentRunner depend on IToolRegistry, so a
            // direct constructor dependency here would close a cycle. IServiceProvider is resolved
            // lazily here instead — the one blessed service-locator use in this codebase. Do not
            // copy this pattern for anything that doesn't have the same cyclic-dependency constraint.
            var orchestrator = services.GetRequiredService<IConversationOrchestrator>();
            result = await orchestrator.RunToCompletionAsync(childSpec, CountEvents, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // RunToCompletionAsync itself absorbs run-level cancellation and failures into
            // ConversationRunResult (it does not throw for a cancelled or timed-out child), so this
            // only fires for something genuinely unexpected (e.g. a DI resolution failure). Guarded
            // on the *parent's* token so an outer cancellation still propagates unhandled and takes
            // the normal AgentRunner cancellation path instead of being reported as a tool failure.
            logger.LogWarning(ex, "Subagent '{Title}' failed to run.", childTitle);
            ctx.Emit(new SubagentCompleted(ctx.CallId, childSummary.Id, null));
            return new ToolResult(false, $"Subagent '{childTitle}' failed to run: {ex.Message}");
        }

        var timedOut = !ct.IsCancellationRequested && timeoutCts.IsCancellationRequested;
        ctx.Emit(new SubagentCompleted(ctx.CallId, childSummary.Id, result.Reason));

        var success = result.Reason == CompletionReason.Completed && result.Error is null;
        var body = new StringBuilder();
        body.AppendLine(string.IsNullOrWhiteSpace(result.AssistantMessage)
            ? "(subagent produced no final message)"
            : result.AssistantMessage);
        body.AppendLine();
        body.Append($"[subagent stats] steps: {steps}, tools invoked: {toolsInvoked}, denials: {result.Denials.Count}");
        if (timedOut)
            body.Append($", timed out after {subagentOpts.TimeoutSeconds}s");
        else if (result.Reason is { } reason && reason != CompletionReason.Completed)
            body.Append($", reason: {reason}");
        if (result.Error is { } error)
            body.Append($", error: {error}");

        return new ToolResult(success, ToolOutput.Truncate(body.ToString(), caliperOpts.ToolOutputMaxChars));
    }

    private static bool TryResolveProfile(
        SubagentsOptions opts,
        string profileName,
        [NotNullWhen(true)] out SubagentProfileOptions? profile)
    {
        if (opts.Profiles.TryGetValue(profileName, out profile))
            return true;

        // Defensive fallback: a Profiles dictionary rehydrated from JSON deserialization may not
        // preserve the OrdinalIgnoreCase comparer the default seed uses, so fall back to an explicit
        // case-insensitive scan rather than depending on the dictionary's own comparer.
        foreach (var (name, candidate) in opts.Profiles)
        {
            if (!string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase))
                continue;

            profile = candidate;
            return true;
        }

        profile = null;
        return false;
    }

    // Roadmap §3.1 restrict-only overlay: child effective mode = Min(parent effective mode, profile
    // mode if set), with restrictiveness ordering Plan < AskAlways < Auto (Plan most restrictive) —
    // a profile can tighten the parent's mode, never loosen it. Every other field is carried over
    // from the parent's own effective options (not the profile), so the global shell denylist/
    // auto-allow roots/RememberApprovals a parent already operates under apply unchanged; the global
    // denylist union itself is still enforced by PermissionGate when the child dispatches a call.
    // Internal (not private) so it's directly unit-testable via InternalsVisibleTo("Caliper.Core.Tests").
    internal static PermissionsOptions BuildChildOverlay(
        PermissionsOptions? parentOverlay,
        PermissionsOptions globalPermissions,
        PermissionMode? profileMode)
    {
        var baseline = parentOverlay ?? globalPermissions;
        var effectiveMode = profileMode is { } mode ? MostRestrictive(baseline.Mode, mode) : baseline.Mode;
        return new PermissionsOptions
        {
            Mode = effectiveMode,
            RememberApprovals = baseline.RememberApprovals,
            ShellAutoAllowlist = [.. baseline.ShellAutoAllowlist],
            ShellDenylist = [.. baseline.ShellDenylist],
            AutoAllowFileRoots = [.. baseline.AutoAllowFileRoots],
        };
    }

    private static PermissionMode MostRestrictive(PermissionMode a, PermissionMode b) =>
        RestrictivenessRank(a) <= RestrictivenessRank(b) ? a : b;

    private static int RestrictivenessRank(PermissionMode mode) => mode switch
    {
        PermissionMode.Plan => 0,
        PermissionMode.AskAlways => 1,
        PermissionMode.Auto => 2,
        _ => 1,
    };

    private static string Truncate(string text, int maxLength)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= maxLength ? oneLine : string.Concat(oneLine.AsSpan(0, maxLength), "…");
    }
}
