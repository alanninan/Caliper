// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Caliper.Evals;

internal static class EvalHarnessRunner
{
    // ── Hermetic mode — no Ollama required ─────────────────────────────────

    public static async Task<SuiteResult> RunHermeticAsync(
        string suiteName,
        IReadOnlyList<EvalCase> cases,
        CancellationToken ct = default)
    {
        var results = new List<EvalResult>(cases.Count);

        foreach (var @case in cases)
        {
            var sw = Stopwatch.StartNew();
            var events = new List<AgentEvent>();

            var store = new EvalSessionStore();
            var strategy = new EnvelopeScriptTurnStrategy(@case.ScriptedTurns);
            var toolRegistry = BuildHermeticRegistry(@case);
            var skillStore = new EvalSkillStore();
            var prompt = @case.ScriptedPromptDecisions is null
                ? null
                : new EvalScriptedPermissionPrompt(@case.ScriptedPromptDecisions);
            var runner = BuildRunner(strategy, toolRegistry, skillStore, store, @case, prompt);
            var sessionId = await store.CreateAsync(null, ct).ConfigureAwait(false);
            if (@case.SeedSessionAsync is not null)
                await @case.SeedSessionAsync(store, sessionId, ct).ConfigureAwait(false);

            await foreach (var evt in runner.RunAsync(sessionId, @case.UserMessage, ct).ConfigureAwait(false))
                events.Add(evt);

            sw.Stop();
            results.Add(BuildResult(@case, events, sw.Elapsed, prompt?.Count ?? 0));
        }

        return new SuiteResult(suiteName, ModelName: null, DateTimeOffset.UtcNow, results);
    }

    // ── Model-in-the-loop mode ─────────────────────────────────────────────

    public static async Task<SuiteResult> RunWithModelAsync(
        string suiteName,
        IReadOnlyList<EvalCase> cases,
        string modelName,
        CancellationToken ct = default)
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices((ctx, services) =>
            {
                services.AddCaliperCore(ctx.Configuration);
                // Override from CLI arg after base config is bound.
                services.PostConfigure<CaliperOptions>(opts =>
                {
                    opts.Model = modelName;
                    opts.Temperature = 0.0;
                    opts.Seed        = 42;
                });
            })
            .Build();

        var runner   = host.Services.GetRequiredService<AgentRunner>();
        var sessions = host.Services.GetRequiredService<ISessionStore>();

        var results = new List<EvalResult>(cases.Count);

        foreach (var @case in cases)
        {
            var sw = Stopwatch.StartNew();
            var events = new List<AgentEvent>();
            var sessionId = await sessions.CreateAsync(null, ct).ConfigureAwait(false);

            await foreach (var evt in runner.RunAsync(sessionId, @case.UserMessage, ct).ConfigureAwait(false))
                events.Add(evt);

            sw.Stop();
            results.Add(BuildResult(@case, events, sw.Elapsed, promptCount: 0));
        }

        return new SuiteResult(suiteName, modelName, DateTimeOffset.UtcNow, results);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IToolRegistry BuildHermeticRegistry(EvalCase @case) =>
        @case.ToolRegistryFactory is not null
            ? @case.ToolRegistryFactory()
            : @case.ToolSpecs is { Count: > 0 }
                ? MockToolRegistry.FromSpecs(@case.ToolSpecs)
                : @case.MockToolResponses is { Count: > 0 }
            ? MockToolRegistry.FromMockResponses(@case.MockToolResponses)
            : EmptyEvalToolRegistry.Instance;

    // Builds the result: evaluates the case's tool/argument expectations once, both to
    // gate pass/fail and to record correct-tool / valid-argument outcomes for the metrics
    // (SPEC §14.4). A null outcome means the case sets no expectation of that kind.
    private static EvalResult BuildResult(EvalCase @case, IReadOnlyList<AgentEvent> events, TimeSpan elapsed, int promptCount)
    {
        var invoked = events.OfType<ToolInvoked>().ToList();

        bool? correctTool = @case.ExpectedTools is { } expected
            ? expected.SequenceEqual(invoked.Select(t => t.Tool), StringComparer.Ordinal)
            : null;

        bool? validArgs = @case.ValidateArgs is { } validate
            ? invoked.All(t => validate(t.Arguments))
            : null;
        bool? permissionCorrect = @case.PermissionCorrect is { } permissionCheck
            ? permissionCheck(events, promptCount)
            : null;
        bool? compactionSafe = @case.CompactionSafe is { } compactionCheck
            ? compactionCheck(events)
            : null;

        var outcome = @case.Assert(events);
        if (outcome.Pass && correctTool == false)
            outcome = EvalOutcome.Fail(
                $"Expected tools [{string.Join(", ", @case.ExpectedTools!)}], got [{string.Join(", ", invoked.Select(t => t.Tool))}]");
        else if (outcome.Pass && validArgs == false)
            outcome = EvalOutcome.Fail(
                $"Invalid arguments for invoked tool(s): [{string.Join(", ", invoked.Select(t => t.Tool))}]");
        else if (outcome.Pass && permissionCorrect == false)
            outcome = EvalOutcome.Fail("Permission-correctness check failed.");
        else if (outcome.Pass && compactionSafe == false)
            outcome = EvalOutcome.Fail("Compaction-safety check failed.");

        return new EvalResult(@case.Id, outcome, events, elapsed, correctTool, validArgs, permissionCorrect, compactionSafe, promptCount);
    }

    private static AgentRunner BuildRunner(
        ITurnStrategy strategy,
        IToolRegistry toolRegistry,
        ISkillStore skillStore,
        ISessionStore sessionStore,
        EvalCase? @case = null,
        EvalScriptedPermissionPrompt? prompt = null)
    {
        var tokens = new EvalTokenCounter();
        var context = @case?.ContextFactory?.Invoke(tokens) ?? new EvalContextManager(tokens);
        var caliperOptions = @case?.RuntimeOptions ?? new CaliperOptions
        {
            Model              = "eval-fake",
            MaxSteps           = 8,
            DuplicateCallLimit = 5,
            Seed               = 42,
            Temperature        = 0.0,
        };
        var opts = Options.Create(caliperOptions);

        var services = new ServiceCollection().AddHttpClient();
        if (prompt is not null)
            services.AddSingleton<IPermissionPrompt>(prompt);
        var provider = services.BuildServiceProvider();
        var httpFac = provider.GetRequiredService<IHttpClientFactory>();
        var permissions = Options.Create(new PermissionsOptions
        {
            Mode = @case?.PermissionMode ?? PermissionMode.AskAlways,
        });
        var runtimeSettings = new RuntimeSettings(opts, permissions);
        IPermissionGate gate = @case?.PermissionMode is null
            ? new EvalPermissionGate()
            : new PermissionGate(runtimeSettings, provider);

        return new AgentRunner(
            strategy,
            toolRegistry,
            skillStore,
            context,
            tokens,
            sessionStore,
            new EvalMemoryStore(),
            new EvalCaliperMdProvider(),
            httpFac,
            new EvalCapabilityProvider(@case?.Capabilities),
            gate,
            runtimeSettings,
            NullLogger<AgentRunner>.Instance,
            skillSelector: null);
    }
}

internal sealed class EvalScriptedPermissionPrompt(IReadOnlyList<PermissionDecision> decisions) : IPermissionPrompt
{
    private readonly Queue<PermissionDecision> _decisions = new(decisions);
    public int Count { get; private set; }

    public Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Count++;
        return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : PermissionDecision.Deny);
    }
}

// Internal (not file-scoped): reused by SubagentSuite to assemble the small recursive DI graph a
// hermetic "task"-tool scenario needs (its own child AgentRunner/ConversationOrchestrator) — see
// SubagentSuite.cs's doc comment for why that scenario needs these building blocks directly rather
// than going through BuildHermeticRegistry/BuildRunner's normal single-runner path.
internal sealed class EvalContextManager(ITokenCounter tokens) : IContextManager
{
    private readonly DropOldestContextManager _dropOldest = new(tokens);

    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct)
    {
        var hardLimit = Math.Max(0, budget.ContextWindowTokens - budget.ReservedOutputTokens);
        var fitted = _dropOldest.FitMessages(frame.History, hardLimit, ct);
        return Task.FromResult(new ContextFit(
            fitted,
            Compacted: !fitted.SequenceEqual(frame.History),
            BeforeTokens: null,
            AfterTokens: null,
            EstimatedPromptTokens: 1,
            RawEstimatedPromptTokens: 1));
    }
}

internal sealed class EvalCapabilityProvider(ModelCapabilities? capabilities = null) : IModelCapabilityProvider
{
    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(capabilities ?? new ModelCapabilities(true, true, true, 32768));
}

internal sealed class EvalPermissionGate : IPermissionGate
{
    public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionDecision.Allow);
}

internal sealed class EvalMemoryStore : IMemoryStore
{
    public Task<string> RenderForPromptAsync(string scope, CancellationToken ct) =>
        Task.FromResult(string.Empty);

    public Task RememberAsync(string scope, string key, string value, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MemoryEntry>> RecallAsync(string scope, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public Task ForgetAsync(string scope, string key, CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class EvalCaliperMdProvider : ICaliperMdProvider
{
    public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) =>
        Task.FromResult(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false));
}

internal sealed class EnvelopeScriptTurnStrategy(IEnumerable<string> scriptedTurns) : ITurnStrategy
{
    private readonly Queue<string> _turns = new(scriptedTurns);

    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var text = _turns.TryDequeue(out var turn) ? turn : """{"action":"respond","rationale":"fallback","content":"fallback"}""";
        yield return new TurnCompleted(ToModelTurn(text));
    }

    private static ModelTurn ToModelTurn(string text)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(text));
        var root = document.RootElement;
        var action = root.GetProperty("action").GetString();
        return action switch
        {
            "respond" => new ModelTurn(
                root.GetProperty("content").GetString() ?? string.Empty,
                [],
                root.TryGetProperty("rationale", out var rationale) ? rationale.GetString() : null,
                new UsageInfo(1, 1, 2)),
            "load_skill" => new ModelTurn(
                null,
                [new ToolCall(Guid.NewGuid().ToString("N"), "load_skill", JsonSerializer.SerializeToElement(new { name = root.GetProperty("skill").GetString() }))],
                root.TryGetProperty("rationale", out var rationale) ? rationale.GetString() : null,
                new UsageInfo(1, 1, 2)),
            "call_tool" => new ModelTurn(
                null,
                [new ToolCall(Guid.NewGuid().ToString("N"), root.GetProperty("tool").GetString() ?? "", root.GetProperty("arguments").Clone())],
                root.TryGetProperty("rationale", out var rationale) ? rationale.GetString() : null,
                new UsageInfo(1, 1, 2)),
            _ => new ModelTurn($"Unsupported scripted action: {action}", [], null, new UsageInfo(1, 1, 2)),
        };
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
            return text;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }

        return text[start..];
    }
}

internal sealed class EvalTokenCounter : ITokenCounter
{
    public int Count(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    public int Count(IEnumerable<Caliper.Core.Models.ChatMessage> messages) =>
        messages.Sum(message => Count(message.Content) + 8);

    public void Calibrate(int estimated, int actual)
    {
    }
}

internal sealed class EvalSkillStore : ISkillStore
{
    private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal)
    {
        ["pdf-processing"] = "# PDF Processing\nExtract PDF text, fill forms, and merge files.",
        ["spreadsheet-cleanup"] = "# Spreadsheet Cleanup\nNormalize columns and clean spreadsheet data.",
    };

    public IReadOnlyList<Caliper.Core.Models.SkillMetadata> List() =>
    [
        new("pdf-processing", "Extract PDF text, fill forms, merge files. Use when handling PDFs."),
        new("spreadsheet-cleanup", "Clean spreadsheet data, normalize columns, and prepare CSV reports."),
    ];

    public Task<string> LoadBodyAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_bodies.TryGetValue(name, out var body))
            throw new InvalidOperationException($"Unknown skill: {name}");

        return Task.FromResult(body);
    }
}
