// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EventReasoningDelta = Caliper.Core.Events.ReasoningDelta;
using TurnReasoningDelta = Caliper.Core.Agents.ReasoningDelta;

namespace Caliper.Core.Agents;

public sealed class AgentRunner(
    ITurnStrategy strategy,
    IToolRegistry toolRegistry,
    ISkillStore skillStore,
    IContextManager context,
    ITokenCounter tokens,
    ISessionStore sessions,
    IMemoryStore memoryStore,
    ICaliperMdProvider caliperMdProvider,
    IHttpClientFactory httpClientFactory,
    IModelCapabilityProvider capabilityProvider,
    IPermissionGate permissionGate,
    IRuntimeSettings runtimeSettings,
    ILogger<AgentRunner> logger,
    ISkillSelector? skillSelector = null) : IAgentRunner
{
    public IAsyncEnumerable<AgentEvent> RunAsync(
        string sessionId,
        string userMessage,
        CancellationToken ct = default) =>
        RunAsync(new RunSpec(sessionId, userMessage), ct);

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        RunSpec spec,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = runtimeSettings.Caliper;
        var effectiveToolRegistry = spec.ToolFilter is null
            ? toolRegistry
            : new FilteredToolRegistry(toolRegistry, spec.ToolFilter);
        await sessions.AppendAsync(spec.SessionId, ChatMessage.Text(ChatRole.User, spec.Prompt), ct)
            .ConfigureAwait(false);

        var allSkills = skillStore.List();
        var selectedSkillNames = skillSelector is not null
            ? await skillSelector.SelectAsync(spec.Prompt, allSkills, opts.MaxSurfacedSkills, ct).ConfigureAwait(false)
            : allSkills.Select(s => s.Name).ToList();
        var skillMenu = selectedSkillNames
            .Select(name => allSkills.FirstOrDefault(s => s.Name == name))
            .OfType<SkillMetadata>()
            .ToList();
        var loadedBodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var recentSigs = new List<string>();
        UsageInfo? lastUsage = null;
        var cumulativePrompt = 0;
        var cumulativeCompletion = 0;
        // Per-run root (roadmap §3.2b): a scheduled job runs in its own working root; interactive
        // runs (spec.WorkingRoot == null) keep using the global one. The same fallback feeds the
        // memory scope here, every ToolContext below, and PermissionRequest.WorkingRoot.
        var workingRoot = ResolveRoot(spec.WorkingRoot ?? opts.WorkingRoot);
        var memoryEnabled = opts.Memory.Enabled;
        var projectScope = memoryEnabled ? MemoryScope.Project(workingRoot) : string.Empty;
        var projectDocument = memoryEnabled
            ? await caliperMdProvider.ReadAsync(workingRoot, ct).ConfigureAwait(false)
            : new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false);
        string? cachedMemoryRender = null;
        var memoryDirty = memoryEnabled;
        int? lastPromptEstimate = null;
        // One instance per RunAsync call (a top-level run or a subagent's own child run each get a
        // fresh one), threaded through every ToolContext this run builds — see SubagentRunState's
        // doc comment for why this, not a dictionary keyed by session/parent id, is the child-count
        // guard's home: it scopes MaxChildrenPerRun to exactly "this run" with no extra bookkeeping
        // to reset or leak across runs.
        var subagentState = new SubagentRunState();

        // load_skill call/result pairs persist in the transcript, so on a resumed session the model
        // believes a skill is loaded — but loadedBodies is per-RunAsync, so its body would be
        // missing from the prompt. Rebuild the set from prior successful loads before the loop.
        await RebuildLoadedSkillsAsync(spec.SessionId, loadedBodies, ct).ConfigureAwait(false);

        for (var step = 1; step <= (spec.MaxSteps ?? opts.MaxSteps); step++)
        {
            opts = runtimeSettings.Caliper;
            var effectiveModel = spec.Model ?? opts.Model;
            yield return new TurnStarted(step);

            var active = ConversationOrchestrator.ActiveHistory(
                await sessions.LoadAsync(spec.SessionId, ct).ConfigureAwait(false));
            var history = active.Messages;
            if (memoryEnabled && (memoryDirty || cachedMemoryRender is null))
            {
                cachedMemoryRender = await memoryStore.RenderForPromptAsync(projectScope, ct).ConfigureAwait(false);
                memoryDirty = false;
            }

            var memoryBlock = memoryEnabled
                ? ConversationOrchestrator.BuildMemoryBlock(cachedMemoryRender ?? string.Empty, projectDocument)
                : string.Empty;
            var system = PromptBuilder.Build(opts, skillMenu, loadedBodies, memoryBlock, spec.Prompt);
            var capabilities = await capabilityProvider.GetAsync(effectiveModel, ct).ConfigureAwait(false);
            var fit = await FitHistoryAsync(history, system, capabilities, opts, effectiveToolRegistry, ct).ConfigureAwait(false);
            fit = fit with { ActiveStartIndex = active.StartIndex };
            var hardLimit = Math.Max(0, capabilities.ContextWindowTokens - opts.Context.ReservedOutputTokens);
            if (opts.Context.AutoCompact &&
                fit.EstimatedPromptTokens is > 0 &&
                fit.EstimatedPromptTokens > hardLimit)
            {
                yield return new RunFailed($"Context window exceeded before send: estimated {fit.EstimatedPromptTokens} prompt tokens, budget {hardLimit}.");
                yield break;
            }

            if (fit.Compacted)
            {
                await sessions.ReplaceWithCompactionAsync(spec.SessionId, fit, ct).ConfigureAwait(false);
                yield return new ContextCompacted(fit.BeforeTokens ?? 0, fit.AfterTokens ?? 0);
            }
            else if (!opts.Context.AutoCompact &&
                fit.EstimatedPromptTokens is { } estimate &&
                estimate > capabilities.ContextWindowTokens * opts.Context.CompactAtFraction)
            {
                logger.LogWarning(
                    "Context estimate {Estimate} is above compact threshold, but AutoCompact is disabled.",
                    estimate);
            }

            lastPromptEstimate = fit.RawEstimatedPromptTokens ?? fit.EstimatedPromptTokens;
            var parameters = new GenerationParameters(
                Temperature: opts.Temperature,
                Seed: opts.Seed,
                MaxOutputTokens: opts.Context.ReservedOutputTokens);
            var ctx = new TurnContext(system, fit.Messages, effectiveToolRegistry, parameters, skillMenu.Select(s => s.Name).ToList(), effectiveModel);

            ModelTurn? turn = null;
            var reasoning = new StringBuilder();
            var content = new StringBuilder();

            var enumerator = strategy.NextAsync(ctx, ct).GetAsyncEnumerator(ct);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool hasNext;
                    TurnUpdate? current = null;
                    Exception? streamError = null;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        if (hasNext) current = enumerator.Current;
                    }
                    catch (OperationCanceledException ex) { streamError = ex; hasNext = false; }
                    catch (Exception ex) { streamError = ex; hasNext = false; }

                    if (streamError is OperationCanceledException && ct.IsCancellationRequested)
                    {
                        yield return new RunCompleted(CompletionReason.Cancelled);
                        yield break;
                    }
                    if (streamError is not null)
                    {
                        // A cancellation not tied to our token (e.g. an inner HTTP timeout) is a
                        // failure, not a user cancel.
                        yield return new RunFailed($"Streaming error: {streamError.Message}");
                        yield break;
                    }
                    if (!hasNext) break;

                    switch (current)
                    {
                        case TurnReasoningDelta r:
                            reasoning.Append(r.Text);
                            yield return new EventReasoningDelta(r.Text);
                            break;
                        case ContentDelta c:
                            content.Append(c.Text);
                            yield return new AssistantMessageDelta(c.Text);
                            break;
                        case TurnCompleted done:
                            turn = done.Turn;
                            break;
                    }
                }
            }

            if (turn is null)
            {
                yield return new RunFailed("Stream ended without a completed turn.");
                yield break;
            }

            lastUsage = turn.Usage;
            cumulativePrompt += lastUsage.PromptTokens ?? 0;
            cumulativeCompletion += lastUsage.CompletionTokens ?? 0;
            yield return new UsageReported(lastUsage.PromptTokens, lastUsage.CompletionTokens, cumulativePrompt, cumulativeCompletion);
            if (lastPromptEstimate is > 0 && lastUsage.PromptTokens is > 0)
                tokens.Calibrate(lastPromptEstimate.Value, lastUsage.PromptTokens.Value);

            if (reasoning.Length > 0)
                yield return new ReasoningCompleted(reasoning.ToString());

            if (turn.ToolCalls.Count == 0)
            {
                var answer = turn.Content ?? content.ToString();
                await sessions.AppendAsync(spec.SessionId, ChatMessage.Text(ChatRole.Assistant, answer), ct)
                    .ConfigureAwait(false);
                yield return new AssistantMessage(answer);
                yield return new RunCompleted(CompletionReason.Completed);
                yield break;
            }

            // The model may narrate a plan before calling tools. Persist that preamble so it
            // survives into the next turn and resumed transcripts, instead of being shown once
            // and dropped (native tool calls carry no Content, so use the streamed text).
            var preamble = turn.Content ?? content.ToString();
            if (!string.IsNullOrWhiteSpace(preamble))
                await sessions.AppendAsync(spec.SessionId, ChatMessage.Text(ChatRole.Assistant, preamble), ct)
                    .ConfigureAwait(false);

            foreach (var call in turn.ToolCalls)
            {
                // Carried-forward item B: the load_skill special-case bypasses the tool registry
                // entirely (LoadSkillTool.InvokeAsync is never actually called — it only exists so
                // the registry surfaces its schema), so a ToolFilter can't restrict it the way it
                // restricts every other tool via effectiveToolRegistry.Find. Gate directly on the
                // filter instead: a run whose ToolFilter excludes load_skill falls through to the
                // general dispatch path below, where it resolves as "Unknown tool" like anything
                // else outside the filter. A null ToolFilter (the common case) keeps today's
                // behavior unchanged.
                var loadSkillAllowed = spec.ToolFilter is null ||
                    spec.ToolFilter.Contains("load_skill", StringComparer.OrdinalIgnoreCase);
                if (call.Tool == "load_skill" && loadSkillAllowed)
                {
                    // Persist the tool-call message first so the stored transcript is a valid
                    // call→result pair; a result without its call is rejected on the next model turn.
                    await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolCall(call), ct).ConfigureAwait(false);

                    var name = call.Arguments.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var failed = new ToolResult(false, "Missing required argument: name");
                        await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolResult(call, failed), ct).ConfigureAwait(false);
                        yield return new ToolFailed(call.CallId, call.Tool, failed.Output);
                        continue;
                    }

                    loadedBodies[name] = await skillStore.LoadBodyAsync(name, ct).ConfigureAwait(false);
                    var loadResult = new ToolResult(true, $"loaded skill {name}");
                    await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolResult(call, loadResult), ct).ConfigureAwait(false);
                    yield return new SkillLoaded(name);
                    continue;
                }

                var sig = $"{call.Tool}:{call.Arguments.GetRawText()}";
                recentSigs.Add(sig);
                // Keep a bounded window and count repeats within it, so an A-B-A-B oscillation is
                // caught (not just back-to-back identical calls) and the list can't grow unbounded.
                const int loopWindow = 24;
                if (recentSigs.Count > loopWindow)
                    recentSigs.RemoveRange(0, recentSigs.Count - loopWindow);
                if (recentSigs.Count(existing => string.Equals(existing, sig, StringComparison.Ordinal)) > opts.DuplicateCallLimit)
                {
                    yield return new RunCompleted(CompletionReason.LoopDetected);
                    yield break;
                }

                var tool = effectiveToolRegistry.Find(call.Tool);
                if (tool is not null)
                {
                    var permissionRequest = new PermissionRequest(
                        call.Tool,
                        tool.EffectiveSideEffect(call.Arguments),
                        call.Arguments,
                        Reason: null,
                        RequestId: call.CallId)
                    {
                        TrustedReadOnly = tool is not IMcpTool,
                        SessionId = spec.SessionId,
                        Overlay = spec.PermissionsOverlay,
                        WorkingRoot = spec.WorkingRoot,
                        Unattended = spec.Unattended,
                    };
                    yield return new PermissionRequested(permissionRequest);
                    var decision = await permissionGate.EvaluateAsync(permissionRequest, ct).ConfigureAwait(false);
                    yield return new PermissionResolved(call.Tool, decision, call.CallId);
                    if (decision == PermissionDecision.Deny)
                    {
                        await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolCall(call), ct).ConfigureAwait(false);
                        await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolResult(call, ToolResult.Denied), ct).ConfigureAwait(false);
                        yield return new ToolFailed(call.CallId, call.Tool, ToolResult.Denied.Output);
                        continue;
                    }
                }

                await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolCall(call), ct).ConfigureAwait(false);
                yield return new ToolInvoked(call.CallId, call.Tool, call.Arguments);

                // File tools may cross WorkingRoot only after the permission gate has approved
                // the call or the path is under a configured auto-allow root.
                var allowOutsideWorkingRoot = Caliper.Core.Permissions.FileAccessPolicy.IsFileTool(call.Tool);
                var toolCtx = new ToolContext(
                    httpClientFactory,
                    logger,
                    opts.SkillsDirectory,
                    ResolveRoot(spec.WorkingRoot ?? opts.WorkingRoot),
                    allowOutsideWorkingRoot,
                    ct,
                    spec.SessionId,
                    call.CallId,
                    spec.SubagentDepth,
                    subagentState,
                    spec.PermissionsOverlay,
                    spec.Unattended);

                ToolResult? result = null;
                var toolCancelled = false;
                try
                {
                    result = await DispatchWithRetry(call, tool, opts, toolCtx, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    toolCancelled = true;
                }

                // Drain any events the tool raised via ToolContext.Emit (e.g. the subagent tool's
                // SubagentStarted/SubagentCompleted — see ToolContext.Emit's doc comment for why
                // this is the mechanism instead of a per-tool special case here) regardless of
                // outcome, so they still surface even if the dispatch was cancelled or failed.
                foreach (var emitted in toolCtx.DrainEmittedEvents())
                    yield return emitted;

                if (toolCancelled)
                {
                    // The tool-call message was already persisted above, so cancelling here would
                    // leave a ToolCall with no matching ToolResult. OpenAI-compatible endpoints
                    // reject an assistant tool_calls message that isn't followed by responses, which
                    // would break every later turn in this session. Append a synthetic result so the
                    // stored transcript stays a valid call→result pair. Use None: ct is cancelled.
                    await sessions.AppendAsync(
                        spec.SessionId,
                        ChatMessage.FromToolResult(call, new ToolResult(false, "Cancelled by user before the tool finished.")),
                        CancellationToken.None).ConfigureAwait(false);
                    yield return new RunCompleted(CompletionReason.Cancelled);
                    yield break;
                }
                if (result is null)
                {
                    yield return new RunFailed("Tool dispatch ended without a result.");
                    yield break;
                }

                if (result.Success)
                    yield return new ToolSucceeded(call.CallId, call.Tool, result.Output, result.FileChange);
                else
                    yield return new ToolFailed(call.CallId, call.Tool, result.Output);

                if (memoryEnabled && result.Success && IsMemoryMutation(call))
                    memoryDirty = true;

                await sessions.AppendAsync(spec.SessionId, ChatMessage.FromToolResult(call, result), ct)
                    .ConfigureAwait(false);
            }
        }

        yield return new RunCompleted(CompletionReason.StepLimit);
    }

    private async Task RebuildLoadedSkillsAsync(
        string sessionId,
        Dictionary<string, string> loadedBodies,
        CancellationToken ct)
    {
        var history = ConversationOrchestrator.ActiveHistory(
            await sessions.LoadAsync(sessionId, ct).ConfigureAwait(false)).Messages;

        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var loadedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in history)
        {
            if (!string.Equals(message.ToolName, "load_skill", StringComparison.Ordinal) ||
                message.Payload is not { } payload ||
                message.ToolCallId is not { } callId)
            {
                continue;
            }

            if (message.Kind == MessageKind.ToolCall &&
                payload.TryGetProperty("Arguments", out var args) &&
                args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("name", out var nameEl) &&
                nameEl.ValueKind == JsonValueKind.String &&
                nameEl.GetString() is { Length: > 0 } name)
            {
                callNames[callId] = name;
            }
            else if (message.Kind == MessageKind.ToolResult &&
                payload.TryGetProperty("Success", out var success) &&
                success.ValueKind == JsonValueKind.True &&
                callNames.TryGetValue(callId, out var loadedName))
            {
                loadedNames.Add(loadedName);
            }
        }

        foreach (var name in loadedNames)
        {
            if (loadedBodies.ContainsKey(name))
                continue;

            try
            {
                loadedBodies[name] = await skillStore.LoadBodyAsync(name, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // A skill loaded in a prior run may have since been renamed or deleted; don't let
                // that break the resumed run — the model can reload it if it still needs it.
                logger.LogWarning("Could not restore previously loaded skill '{Skill}': {Message}", name, ex.Message);
            }
        }
    }

    private async Task<ContextFit> FitHistoryAsync(
        IReadOnlyList<ChatMessage> history,
        string system,
        ModelCapabilities capabilities,
        CaliperOptions opts,
        IToolRegistry tools,
        CancellationToken ct)
    {
        var toolSchemas = tools.Enabled.Select(tool => tool.ParameterSchema).ToList();
        var frame = new PromptFrame(system, history, toolSchemas);
        var budget = new ContextBudget(
            capabilities.ContextWindowTokens,
            opts.Context.ReservedOutputTokens,
            opts.Context.CompactAtFraction);

        return await context.FitAsync(frame, budget, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> DispatchWithRetry(ToolCall call, ITool? tool, CaliperOptions opts, ToolContext toolCtx, CancellationToken outerCt)
    {
        if (tool is null)
            return new ToolResult(false, $"Unknown tool: {call.Tool}");

        var validationError = tool is IMcpTool
            ? null
            : ToolArgumentValidator.Validate(call.Arguments, tool.ParameterSchema);
        if (validationError is not null)
            return new ToolResult(false, $"Invalid arguments for tool '{call.Tool}': {validationError}");

        // Timeout carve-out (roadmap §3.1): a tool whose own work legitimately runs far longer than
        // the generic per-tool budget (the subagent tool's whole child run) overrides it here rather
        // than being killed mid-flight by the generic ToolTimeoutSeconds wrapping every other tool.
        var timeout = tool.ToolTimeoutOverride ?? TimeSpan.FromSeconds(opts.ToolTimeoutSeconds);

        // Only retry side-effect-free tools. Re-running a write/execute tool after a partial
        // failure can duplicate or corrupt work, so those get a single attempt.
        var retryable = tool.SideEffect is SideEffect.ReadOnly or SideEffect.Network;
        var maxAttempts = retryable ? opts.ToolMaxRetries : 0;

        for (var attempt = 0; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                cts.CancelAfter(timeout);
                return await tool.InvokeAsync(call.Arguments, toolCtx, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                    return new ToolResult(false, $"Tool '{call.Tool}' timed out after {timeout.TotalSeconds:0.###}s.");
                await BackoffAsync(attempt, outerCt).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex))
            {
                logger.LogWarning("Tool '{Tool}' attempt {Attempt} failed: {Message}",
                    call.Tool, attempt + 1, ex.Message);
                await BackoffAsync(attempt, outerCt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ToolResult(false, $"Tool '{call.Tool}' failed: {ex.Message}");
            }
        }

        return new ToolResult(false, $"Tool '{call.Tool}' failed after {maxAttempts} retries.");
    }

    private static bool IsRetryable(Exception ex) =>
        // Retrying a rate-limited request with no Retry-After backoff just wastes quota.
        ex is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };

    private static Task BackoffAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), ct);

    private static string ResolveRoot(string root)
    {
        return Path.GetFullPath(LocalPath.ResolveHome(root));
    }

    private static bool IsMemoryMutation(ToolCall call)
    {
        if (!string.Equals(call.Tool, "memory", StringComparison.Ordinal))
            return false;

        if (!call.Arguments.TryGetProperty("action", out var action) ||
            action.ValueKind != JsonValueKind.String)
            return false;

        var actionName = action.GetString();
        return string.Equals(actionName, "remember", StringComparison.Ordinal) ||
            string.Equals(actionName, "forget", StringComparison.Ordinal);
    }

    public const string ContextResetMarker = "[context reset]";
}
