// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Configuration;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Protocol;
using Caliper.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;
using CaliperChatMessage = Caliper.Core.Models.ChatMessage;
using CaliperChatRole = Caliper.Core.Models.ChatRole;

namespace Caliper.Core.Tests.Agents;

public sealed class NativeToolStrategyTests
{
    [Fact]
    public async Task Native_tool_round_trip_rehydrates_call_and_result_for_next_turn()
    {
        var tool = new EchoTool();
        var registry = BuildRegistry(tool);
        var client = new ScriptedChatClient(
            [
                [
                    FunctionCall("call_1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }),
                    FunctionCall("call_1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }),
                ],
                [new ChatResponseUpdate(AIChatRole.Assistant, "done")],
            ]);
        var runner = BuildRunner(client, registry, tool);

        var events = await RunAll(runner, "session", "echo hello");

        Assert.Equal(1, tool.InvocationCount);
        Assert.Contains(events, e => e is ToolSucceeded { CallId: "call_1", Tool: "echo" });
        Assert.Contains(events, e => e is AssistantMessage { Content: "done" });
        Assert.True(client.Requests.Count >= 2);
        Assert.NotEmpty(client.Options[0]?.Tools ?? []);

        var secondTurn = client.Requests[1];
        Assert.Contains(secondTurn, message =>
            message.Role == AIChatRole.Assistant &&
            message.Contents.OfType<FunctionCallContent>().Any(call =>
                call.CallId == "call_1" &&
                call.Name == "echo" &&
                call.Arguments is not null &&
                call.Arguments.TryGetValue("text", out var text) &&
                text?.ToString() == "hello"));
        Assert.Contains(secondTurn, message =>
            message.Role == AIChatRole.Tool &&
            message.Contents.OfType<FunctionResultContent>().Any(result =>
                result.CallId == "call_1" &&
                result.Result?.ToString() == "echoed: hello"));
    }

    [Fact]
    public async Task Malformed_streamed_arguments_set_MalformedReason_and_log_a_warning()
    {
        // Mirrors what Microsoft.Extensions.AI's adapter hands back on a streamed-argument parse
        // failure (TO_FIX §1): Arguments = null, the parse error stashed in .Exception, no throw.
        var parseError = new FormatException("Unexpected character encountered while parsing value.");
        var malformedCall = new FunctionCallContent("call_1", "echo", new Dictionary<string, object?>())
        {
            Arguments = null,
            Exception = parseError,
        };
        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, [malformedCall])]]);
        var logger = new RecordingLogger<NativeToolStrategy>();
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            new StaticCapabilityProvider(new ModelCapabilities(true, true, true, 32768)),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "test/model" }),
                Options.Create(new PermissionsOptions())),
            logger);

        var updates = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        var call = Assert.Single(completed.Turn.ToolCalls);
        Assert.Equal("Unexpected character encountered while parsing value.", call.MalformedReason);
        Assert.Empty(call.Arguments.EnumerateObject());
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase) &&
            entry.Message.Contains("call_1", StringComparison.Ordinal) &&
            entry.Message.Contains("echo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WellFormed_streamed_arguments_leave_MalformedReason_null()
    {
        var client = new ScriptedChatClient(
            [[FunctionCall("call_1", "echo", new Dictionary<string, object?> { ["text"] = "hi" })]]);
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            new StaticCapabilityProvider(new ModelCapabilities(true, true, true, 32768)),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "test/model" }),
                Options.Create(new PermissionsOptions())),
            NullLogger<NativeToolStrategy>.Instance);

        var updates = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        var call = Assert.Single(completed.Turn.ToolCalls);
        Assert.Null(call.MalformedReason);
        Assert.Equal("hi", call.Arguments.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Replay_tolerates_payload_less_tool_message_from_compaction()
    {
        var store = new InMemorySessionStoreStub();
        var sessionId = await store.CreateAsync(null, CancellationToken.None);
        // A compaction placeholder (or legacy row): a Tool-role result with no stored payload.
        // Rehydrating it must not throw or emit an orphaned tool message on the next turn.
        await store.AppendAsync(
            sessionId,
            new CaliperChatMessage(CaliperChatRole.Tool, MessageKind.ToolResult, "[earlier tool results omitted]"),
            CancellationToken.None);

        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "recovered")]]);
        var options = new CaliperOptions { Model = "test/model", MaxSteps = 2, DuplicateCallLimit = 10, EnabledTools = ["echo"] };
        var runtime = new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions()));
        var capabilities = new StaticCapabilityProvider(new ModelCapabilities(true, true, true, 32768));
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            capabilities,
            runtime,
            NullLogger<NativeToolStrategy>.Instance);
        var runner = new AgentRunner(
            strategy,
            BuildRegistry(new EchoTool()),
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            store,
            new NativeEmptyMemoryStore(),
            new NativeEmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            capabilities,
            new NativeAllowAllGate(),
            runtime,
            NullLogger<AgentRunner>.Instance);

        var events = await RunAll(runner, sessionId, "continue");

        Assert.Contains(events, e => e is AssistantMessage { Content: "recovered" });
        Assert.DoesNotContain(events, e => e is RunFailed);
        Assert.DoesNotContain(client.Requests[0], message => message.Role == AIChatRole.Tool);
    }

    [Fact]
    public async Task Replay_heals_dangling_tool_call_with_a_synthetic_result()
    {
        var store = new InMemorySessionStoreStub();
        var sessionId = await store.CreateAsync(null, CancellationToken.None);
        // A run cancelled or killed mid-tool: a ToolCall persisted with no matching ToolResult.
        var args = JsonDocument.Parse("""{"text":"hi"}""").RootElement.Clone();
        await store.AppendAsync(
            sessionId,
            CaliperChatMessage.FromToolCall(new ToolCall("call_orphan", "echo", args)),
            CancellationToken.None);

        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "recovered")]]);
        var options = new CaliperOptions { Model = "test/model", MaxSteps = 2, DuplicateCallLimit = 10, EnabledTools = ["echo"] };
        var runtime = new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions()));
        var capabilities = new StaticCapabilityProvider(new ModelCapabilities(true, true, true, 32768));
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            capabilities,
            runtime,
            NullLogger<NativeToolStrategy>.Instance);
        var runner = new AgentRunner(
            strategy,
            BuildRegistry(new EchoTool()),
            new EmptySkillStore(),
            new PassthroughContextManager(),
            new SimpleTokenCounter(),
            store,
            new NativeEmptyMemoryStore(),
            new NativeEmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            capabilities,
            new NativeAllowAllGate(),
            runtime,
            NullLogger<AgentRunner>.Instance);

        var events = await RunAll(runner, sessionId, "continue");

        Assert.DoesNotContain(events, e => e is RunFailed);
        Assert.Contains(events, e => e is AssistantMessage { Content: "recovered" });
        // The orphaned assistant tool_calls message must be followed by a synthesized tool result
        // so an OpenAI-compatible endpoint would accept the turn.
        Assert.Contains(client.Requests[0], message =>
            message.Role == AIChatRole.Tool &&
            message.Contents.OfType<FunctionResultContent>().Any(r => r.CallId == "call_orphan"));
    }

    [Fact]
    public async Task Auto_mode_omits_tools_and_reasoning_when_capabilities_do_not_support_them()
    {
        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "plain answer")]]);
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            new StaticCapabilityProvider(new ModelCapabilities(false, false, false, 8192)),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "toolless/model", TurnStrategy = TurnStrategyKind.Auto }),
                Options.Create(new PermissionsOptions())),
            NullLogger<NativeToolStrategy>.Instance);
        var context = new TurnContext(
            "system",
            [CaliperChatMessage.Text(CaliperChatRole.User, "hi")],
            BuildRegistry(new EchoTool()),
            new GenerationParameters());

        _ = await Collect(strategy.NextAsync(context, CancellationToken.None));

        Assert.Empty(client.Options[0]?.Tools ?? []);
        Assert.Null(client.Options[0]?.Reasoning);
    }

    [Fact]
    public async Task Runtime_model_switch_routes_next_turn_to_new_client()
    {
        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "done")]]);
        var provider = new SingleChatClientProvider(client);
        var settings = new RuntimeSettings(
            Options.Create(new CaliperOptions { Model = "model/a", TurnStrategy = TurnStrategyKind.Auto }),
            Options.Create(new PermissionsOptions()));
        var strategy = new NativeToolStrategy(
            provider,
            new StaticCapabilityProvider(new ModelCapabilities(false, false, false, 8192)),
            settings,
            NullLogger<NativeToolStrategy>.Instance);
        settings.SetModel("model/b");

        _ = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        Assert.Contains("model/b", provider.RequestedModels);
    }

    [Fact]
    public async Task AgentRunner_fits_history_with_model_context_window()
    {
        var context = new RecordingContextManager();
        var runner = BuildRunner(
            new SingleTurnStrategy(new TurnCompleted(new ModelTurn("done", [], null, new UsageInfo(null, null, null)))),
            BuildRegistry(new EchoTool()),
            new EchoTool(),
            context,
            capabilities: new ModelCapabilities(true, true, true, 1000),
            options: new CaliperOptions { Model = "small/model", Context = new ContextOptions { ReservedOutputTokens = 100 } });

        _ = await RunAll(runner, "session", "fit");

        Assert.True(context.LastBudget is > 0 and < 1000);
        Assert.True(context.LastBudget <= 900);
    }

    [Fact]
    public async Task Constrained_strategy_streams_envelope_content_and_sets_response_format()
    {
        var client = new ScriptedChatClient(
            [[new ChatResponseUpdate(AIChatRole.Assistant, """{"rationale":"brief","action":"respond","content":"hello"}""")]]);
        var strategy = new ConstrainedEnvelopeStrategy(
            new SingleChatClientProvider(client),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "structured/model" }),
                Options.Create(new PermissionsOptions())),
            NullLogger<ConstrainedEnvelopeStrategy>.Instance);

        var updates = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        Assert.Equal("hello", completed.Turn.Content);
        Assert.Empty(completed.Turn.ToolCalls);
        Assert.NotNull(client.Options[0]?.ResponseFormat);
    }

    [Fact]
    public async Task Constrained_strategy_maps_tool_and_skill_envelopes_to_tool_calls()
    {
        var client = new ScriptedChatClient(
            [
                [new ChatResponseUpdate(AIChatRole.Assistant, """{"rationale":"use echo","action":"call_tool","tool":"echo","arguments":{"text":"hi"}}""")],
                [new ChatResponseUpdate(AIChatRole.Assistant, """{"rationale":"load it","action":"load_skill","skill":"pdf-processing"}""")],
            ]);
        var strategy = new ConstrainedEnvelopeStrategy(
            new SingleChatClientProvider(client),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "structured/model" }),
                Options.Create(new PermissionsOptions())),
            NullLogger<ConstrainedEnvelopeStrategy>.Instance);
        var context = new TurnContext(
            "system",
            [],
            BuildRegistry(new EchoTool()),
            new GenerationParameters(),
            ["pdf-processing"]);

        var toolUpdates = await Collect(strategy.NextAsync(context, CancellationToken.None));
        var skillUpdates = await Collect(strategy.NextAsync(context, CancellationToken.None));

        var toolCall = Assert.Single(Assert.IsType<TurnCompleted>(toolUpdates.Last()).Turn.ToolCalls);
        Assert.Equal("echo", toolCall.Tool);
        Assert.Equal("hi", toolCall.Arguments.GetProperty("text").GetString());

        var skillCall = Assert.Single(Assert.IsType<TurnCompleted>(skillUpdates.Last()).Turn.ToolCalls);
        Assert.Equal("load_skill", skillCall.Tool);
        Assert.Equal("pdf-processing", skillCall.Arguments.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Constrained_strategy_includes_tool_descriptions_in_system_prompt()
    {
        var client = new ScriptedChatClient(
            [[new ChatResponseUpdate(AIChatRole.Assistant, """{"rationale":"brief","action":"respond","content":"ok"}""")]]);
        var strategy = new ConstrainedEnvelopeStrategy(
            new SingleChatClientProvider(client),
            new RuntimeSettings(
                Options.Create(new CaliperOptions { Model = "structured/model" }),
                Options.Create(new PermissionsOptions())),
            NullLogger<ConstrainedEnvelopeStrategy>.Instance);

        _ = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var system = Assert.Single(client.Requests[0], message => message.Role == AIChatRole.System);
        Assert.Contains("## Tools", system.Text, StringComparison.Ordinal);
        Assert.Contains("echo", system.Text, StringComparison.Ordinal);
        Assert.Contains("Echoes text.", system.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selector_auto_prefers_native_when_tools_are_supported()
    {
        var client = new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "native answer")]]);
        var strategy = BuildSelector(
            client,
            new ModelCapabilities(true, true, true, 8192),
            new CaliperOptions { Model = "native/model", TurnStrategy = TurnStrategyKind.Auto });

        var updates = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        Assert.Equal("native answer", completed.Turn.Content);
        Assert.Null(client.Options[0]?.ResponseFormat);
        Assert.NotEmpty(client.Options[0]?.Tools ?? []);
    }

    [Fact]
    public async Task Selector_auto_uses_constrained_when_native_tools_are_unavailable()
    {
        var client = new ScriptedChatClient(
            [[new ChatResponseUpdate(AIChatRole.Assistant, """{"rationale":"brief","action":"respond","content":"structured answer"}""")]]);
        var strategy = BuildSelector(
            client,
            new ModelCapabilities(false, true, true, 8192),
            new CaliperOptions { Model = "structured/model", TurnStrategy = TurnStrategyKind.Auto });

        var updates = await Collect(strategy.NextAsync(
            new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()),
            CancellationToken.None));

        var completed = Assert.IsType<TurnCompleted>(updates.Last());
        Assert.Equal("structured answer", completed.Turn.Content);
        Assert.NotNull(client.Options[0]?.ResponseFormat);
    }

    [Fact]
    public async Task Selector_forced_modes_fail_when_model_lacks_required_capability()
    {
        var native = BuildSelector(
            new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "unused")]]),
            new ModelCapabilities(false, true, true, 8192),
            new CaliperOptions { Model = "toolless/model", TurnStrategy = TurnStrategyKind.Native });
        var constrained = BuildSelector(
            new ScriptedChatClient([[new ChatResponseUpdate(AIChatRole.Assistant, "unused")]]),
            new ModelCapabilities(true, true, false, 8192),
            new CaliperOptions { Model = "unstructured/model", TurnStrategy = TurnStrategyKind.SingleEnvelope });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Collect(native.NextAsync(new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()), CancellationToken.None)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Collect(constrained.NextAsync(new TurnContext("system", [], BuildRegistry(new EchoTool()), new GenerationParameters()), CancellationToken.None)));
    }

    private static ChatResponseUpdate FunctionCall(string callId, string name, IDictionary<string, object?> arguments) =>
        new(AIChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]);

    private static AgentRunner BuildRunner(
        ScriptedChatClient client,
        IToolRegistry registry,
        EchoTool tool,
        IContextManager? context = null,
        ModelCapabilities? capabilities = null,
        CaliperOptions? options = null)
    {
        options ??= new CaliperOptions
        {
            Model = "test/model",
            MaxSteps = 4,
            DuplicateCallLimit = 10,
            EnabledTools = [tool.Name],
        };
        var capabilityProvider = new StaticCapabilityProvider(capabilities ?? new ModelCapabilities(true, true, true, 32768));
        var strategy = new NativeToolStrategy(
            new SingleChatClientProvider(client),
            capabilityProvider,
            new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions())),
            NullLogger<NativeToolStrategy>.Instance);

        return BuildRunner(strategy, registry, tool, context, capabilities, options);
    }

    private static TurnStrategySelector BuildSelector(
        ScriptedChatClient client,
        ModelCapabilities capabilities,
        CaliperOptions options)
    {
        var provider = new SingleChatClientProvider(client);
        var capabilityProvider = new StaticCapabilityProvider(capabilities);
        var runtime = new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions()));
        var native = new NativeToolStrategy(
            provider,
            capabilityProvider,
            runtime,
            NullLogger<NativeToolStrategy>.Instance);
        var constrained = new ConstrainedEnvelopeStrategy(
            provider,
            runtime,
            NullLogger<ConstrainedEnvelopeStrategy>.Instance);
        return new TurnStrategySelector(
            native,
            constrained,
            runtime,
            capabilityProvider,
            NullLogger<TurnStrategySelector>.Instance);
    }

    private static AgentRunner BuildRunner(
        ITurnStrategy strategy,
        IToolRegistry registry,
        EchoTool tool,
        IContextManager? context = null,
        ModelCapabilities? capabilities = null,
        CaliperOptions? options = null)
    {
        options ??= new CaliperOptions
        {
            Model = "test/model",
            MaxSteps = 4,
            DuplicateCallLimit = 10,
            EnabledTools = [tool.Name],
        };
        var store = new InMemorySessionStoreStub();
        return new AgentRunner(
            strategy,
            registry,
            new EmptySkillStore(),
            context ?? new PassthroughContextManager(),
            new SimpleTokenCounter(),
            store,
            new NativeEmptyMemoryStore(),
            new NativeEmptyCaliperMdProvider(),
            new NullHttpClientFactory(),
            new StaticCapabilityProvider(capabilities ?? new ModelCapabilities(true, true, true, 32768)),
            new NativeAllowAllGate(),
            new RuntimeSettings(Options.Create(options), Options.Create(new PermissionsOptions())),
            NullLogger<AgentRunner>.Instance);
    }

    private static ToolRegistry BuildRegistry(EchoTool tool) =>
        new([tool], Options.Create(new CaliperOptions { EnabledTools = [tool.Name] }), NullLogger<ToolRegistry>.Instance);

    private static async Task<List<AgentEvent>> RunAll(AgentRunner runner, string sessionId, string message)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(sessionId, message))
            events.Add(e);
        return events;
    }

    private static async Task<List<TurnUpdate>> Collect(IAsyncEnumerable<TurnUpdate> updates)
    {
        var collected = new List<TurnUpdate>();
        await foreach (var update in updates)
            collected.Add(update);
        return collected;
    }
}

sealed class ScriptedChatClient(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> responses) : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _responses = new(responses);

    public List<IReadOnlyList<AIChatMessage>> Requests { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        Options.Add(options);
        var batch = _responses.Count > 0
            ? _responses.Dequeue()
            : [new ChatResponseUpdate(AIChatRole.Assistant, "fallback")];

        await Task.Yield();
        foreach (var update in batch)
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

sealed class SingleChatClientProvider(IChatClient client) : IChatClientProvider
{
    public List<string> RequestedModels { get; } = [];

    public IChatClient GetClient(string modelSlug)
    {
        RequestedModels.Add(modelSlug);
        return client;
    }
}

sealed class EchoTool : ITool
{
    private static readonly JsonElement s_schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["text"],"properties":{"text":{"type":"string"}}}""")
        .RootElement.Clone();

    public int InvocationCount { get; private set; }
    public string Name => "echo";
    public string Description => "Echoes text.";
    public JsonElement ParameterSchema => s_schema;
    public SideEffect SideEffect => SideEffect.ReadOnly;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext ctx, CancellationToken ct)
    {
        InvocationCount++;
        return Task.FromResult(new ToolResult(true, $"echoed: {arguments.GetProperty("text").GetString()}"));
    }
}

sealed class StaticCapabilityProvider(ModelCapabilities capabilities) : IModelCapabilityProvider
{
    public Task<ModelCapabilities> GetAsync(string modelSlug, CancellationToken ct) =>
        Task.FromResult(capabilities);
}

sealed class InMemorySessionStoreStub : ISessionStore
{
    private readonly Dictionary<string, List<CaliperChatMessage>> _data = [];

    public Task<string> CreateAsync(string? title, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _data[id] = [];
        return Task.FromResult(id);
    }

    public Task AppendAsync(string id, CaliperChatMessage msg, CancellationToken ct)
    {
        if (!_data.TryGetValue(id, out var list))
        {
            list = [];
            _data[id] = list;
        }

        list.Add(msg);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CaliperChatMessage>> LoadAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<CaliperChatMessage> messages = _data.TryGetValue(id, out var list) ? [.. list] : [];
        return Task.FromResult(messages);
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        _data.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

    public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct)
    {
        var prefix = _data.TryGetValue(sessionId, out var existing)
            ? existing.Take(Math.Max(0, fit.ActiveStartIndex)).ToList()
            : [];
        prefix.AddRange(fit.Messages);
        _data[sessionId] = prefix;
        return Task.CompletedTask;
    }
}

sealed class EmptySkillStore : ISkillStore
{
    public IReadOnlyList<SkillMetadata> List() => [];
    public Task<string> LoadBodyAsync(string name, CancellationToken ct) => throw new InvalidOperationException();
}

sealed class NativeEmptyMemoryStore : IMemoryStore
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

sealed class NativeEmptyCaliperMdProvider : ICaliperMdProvider
{
    public Task<ProjectMemoryDocument> ReadAsync(string workingRoot, CancellationToken ct) =>
        Task.FromResult(new ProjectMemoryDocument(string.Empty, string.Empty, Truncated: false));
}

sealed class SimpleTokenCounter : ITokenCounter
{
    public int Count(string text) => Math.Max(1, text.Length / 4);
    public int Count(IEnumerable<CaliperChatMessage> messages) => messages.Sum(message => Count(message.Content) + 4);
    public void Calibrate(int estimated, int actual) { }
}

sealed class PassthroughContextManager : IContextManager
{
    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct) =>
        Task.FromResult(new ContextFit(frame.History, false, null, null, 1));
}

sealed class RecordingContextManager : IContextManager
{
    public int? LastBudget { get; private set; }

    public Task<ContextFit> FitAsync(PromptFrame frame, ContextBudget budget, CancellationToken ct)
    {
        LastBudget = budget.ContextWindowTokens - budget.ReservedOutputTokens;
        return Task.FromResult(new ContextFit(frame.History, false, null, null, 1));
    }
}

sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

sealed class SingleTurnStrategy(TurnUpdate update) : ITurnStrategy
{
#pragma warning disable CS1998
    public async IAsyncEnumerable<TurnUpdate> NextAsync(
        TurnContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return update;
    }
#pragma warning restore CS1998
}

sealed class NativeAllowAllGate : IPermissionGate
{
    public Task<PermissionDecision> EvaluateAsync(PermissionRequest request, CancellationToken ct) =>
        Task.FromResult(PermissionDecision.Allow);
}

// Mirrors SchedulerHostedServiceTests' RecordingLogger<T> — this test file has no logger-capture
// helper of its own yet, and the malformed-arguments warning (TO_FIX §1) needs one to assert on.
sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Add((logLevel, formatter(state, exception)));
}
