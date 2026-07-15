// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Net.Sockets;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Execution;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Persistence;
using Caliper.Core.Scheduling;
using Caliper.Core.Skills;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Caliper.Core.Tools.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Caliper.Core.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers all Caliper.Core services. Call from the host's DI setup.</summary>
    public static IServiceCollection AddCaliperCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options
        services.Configure<CaliperOptions>(configuration.GetSection("Caliper"));
        services.Configure<ProvidersOptions>(configuration.GetSection("Providers"));
        services.Configure<PermissionsOptions>(configuration.GetSection("Permissions"));
        services.Configure<McpOptions>(configuration.GetSection("Mcp"));
        // Preserved local path options; not used by the default native strategy.
        services.Configure<AgentOptions>(configuration.GetSection("Agent"));
        services.Configure<OllamaOptions>(configuration.GetSection("Ollama"));
        services.Configure<TokenizerOptions>(configuration.GetSection("Tokenizer"));
        services.Configure<SearchOptions>(configuration.GetSection("Search"));
        services.Configure<PersistenceOptions>(configuration.GetSection("Persistence"));
        services.PostConfigure<ProvidersOptions>(options =>
        {
            // Fall back to the env var when the config key is null OR an empty/whitespace
            // placeholder (the seeded config.json ships ApiKey as ""), so a set env var wins.
            if (string.IsNullOrWhiteSpace(options.OpenRouter.ApiKey))
                options.OpenRouter.ApiKey = Environment.GetEnvironmentVariable("CALIPER_OPENROUTER_KEY");
            if (string.IsNullOrWhiteSpace(options.Gemini.ApiKey))
                options.Gemini.ApiKey = Environment.GetEnvironmentVariable("CALIPER_GEMINI_KEY");
        });
        services.PostConfigure<SearchOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.ApiKey))
                options.ApiKey = Environment.GetEnvironmentVariable("CALIPER_SEARCH_KEY");
        });

        // Validation — triggers eagerly on first resolution
        services.AddSingleton<IValidateOptions<CaliperOptions>, CaliperOptionsValidator>();
        services.AddSingleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>();
        // Cross-section: the global Permissions.ShellAutoAllowlist is subject to the same
        // wildcard-requires-container rule as a per-schedule overlay (roadmap §3.3) — see
        // PermissionsOptionsValidator's doc comment for why this needs its own IValidateOptions<T>
        // rather than living inside CaliperOptionsValidator.
        services.AddSingleton<IValidateOptions<PermissionsOptions>, PermissionsOptionsValidator>();
        services.AddSingleton<IRuntimeSettings, RuntimeSettings>();
        services.AddSingleton<IConfigFileStore, ConfigFileStore>();
        services.AddSingleton<IConfigWriter, ConfigWriter>();

        // HTTP clients
        services.AddHttpClient();
        services.AddHttpClient("ollama", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.Endpoint);
            client.Timeout     = TimeSpan.FromSeconds(opts.RequestTimeoutSeconds);
        });
        services.AddHttpClient("tavily", client =>
        {
            client.BaseAddress = new Uri("https://api.tavily.com/");
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("fetch_url")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = ConnectToSafeAddressAsync,
            });

        // Preserved local model client
        services.AddSingleton<IModelClient, OllamaModelClient>();
        // Named client resolved per request via IHttpClientFactory so the singleton provider
        // doesn't pin a handler with stale DNS for the process lifetime.
        services.AddHttpClient(OpenRouterCapabilityProvider.HttpClientName);
        services.AddSingleton<OpenRouterCapabilityProvider>();
        services.AddSingleton<OpenRouterChatClientProvider>();
        services.AddSingleton<GeminiCapabilityProvider>();
        services.AddSingleton<GeminiChatClientProvider>();
        // ModelProviderRouter dispatches to the OpenRouter or Gemini concretes above based on
        // CaliperOptions.Provider, re-checked per call so a runtime provider switch takes effect
        // immediately rather than requiring a restart.
        services.AddSingleton<ModelProviderRouter>();
        services.AddSingleton<IChatClientProvider>(sp =>
            sp.GetRequiredService<ModelProviderRouter>());
        services.AddSingleton<IModelCapabilityProvider>(sp =>
            sp.GetRequiredService<ModelProviderRouter>());
        services.AddSingleton<IModelCatalog>(sp =>
            sp.GetRequiredService<ModelProviderRouter>());

        // Turn strategy.
        services.AddSingleton<NativeToolStrategy>();
        services.AddSingleton<ConstrainedEnvelopeStrategy>();
        services.AddSingleton<TurnStrategySelector>();
        services.AddSingleton<ITurnStrategy>(sp => sp.GetRequiredService<TurnStrategySelector>());
        services.AddSingleton<IPermissionGate, PermissionGate>();
        services.AddSingleton<IMcpHub, McpHub>();
        services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
        services.AddSingleton<ICaliperMdProvider, CaliperMdProvider>();

        // Tools
        services.AddSingleton<StubSearchBackend>();
        services.AddSingleton<TavilySearchBackend>();
        services.AddSingleton<ISearchBackend>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
            return string.Equals(opts.Backend, "Tavily", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<TavilySearchBackend>()
                : sp.GetRequiredService<StubSearchBackend>();
        });
        services.AddSingleton<ITool, SearchTool>();
        services.AddSingleton<ITool, FetchUrlTool>();
        services.AddSingleton<ITool, LoadSkillTool>();
        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool, ListDirTool>();
        services.AddSingleton<ITool, GlobTool>();
        services.AddSingleton<ITool, GrepTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, EditFileTool>();
        services.AddSingleton<ITool, MemoryTool>();
        // Roadmap §3.3: both execution backends are always constructed regardless of the configured
        // Execution.Backend — ShellTool below picks between them per call via the live
        // runtimeSettings.Caliper.Execution.Backend seam, so there is no "swap the DI wiring" step
        // that could race a concurrent run when Backend changes.
        services.AddSingleton<HostExecutionBackend>();
        services.AddSingleton<ContainerExecutionBackend>();
        // Register both shells and let EnabledTools decide; this keeps cross-platform configs
        // working and avoids a startup warning about the OS's non-default shell name.
        services.AddSingleton<ITool>(sp => new ShellTool(
            sp.GetRequiredService<IOptions<CaliperOptions>>(),
            "powershell",
            sp.GetRequiredService<HostExecutionBackend>(),
            sp.GetRequiredService<ContainerExecutionBackend>(),
            sp.GetRequiredService<IRuntimeSettings>()));
        services.AddSingleton<ITool>(sp => new ShellTool(
            sp.GetRequiredService<IOptions<CaliperOptions>>(),
            "bash",
            sp.GetRequiredService<HostExecutionBackend>(),
            sp.GetRequiredService<ContainerExecutionBackend>(),
            sp.GetRequiredService<IRuntimeSettings>()));
        // SubagentTool resolves IConversationOrchestrator lazily from IServiceProvider inside
        // InvokeAsync rather than a constructor dependency — see its DI-cycle comment — so it can be
        // registered here like any other ITool without closing the ToolRegistry -> orchestrator cycle.
        services.AddSingleton<ITool, SubagentTool>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        // Skills
        services.AddSingleton<ISkillStore, SkillStore>();
        services.AddSingleton<KeywordSkillSelector>();
        if (Enum.TryParse<SkillSelectorKind>(configuration["Caliper:SkillSelector"], ignoreCase: true, out var selectorKind)
            && selectorKind == SkillSelectorKind.Keyword)
        {
            services.AddSingleton<ISkillSelector>(sp =>
                sp.GetRequiredService<KeywordSkillSelector>());
        }

        // Context management
        services.AddSingleton<ITokenCounter, TokenCounter>();
        services.AddSingleton<ISummarizer, ChatSummarizer>();
        services.AddSingleton<IContextManager, AutoCompactingContextManager>();

        // Session store
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        // Durable execution (roadmap §3.4): tracks orchestrator-driven runs so a crash/kill can be
        // resumed. Shares the same SqlitePath as the session store but its own schema-gated store
        // instance; see SqliteRunStore's startup-sweep doc comment for when stale rows flip to
        // Interrupted.
        services.AddSingleton<IRunStore, SqliteRunStore>();

        // Agent runner
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<IAgentRunner>(services =>
            services.GetRequiredService<AgentRunner>());
        services.AddSingleton<ConversationOrchestrator>();
        services.AddSingleton<IConversationOrchestrator>(services =>
            services.GetRequiredService<ConversationOrchestrator>());

        // Scheduling (roadmap §3.2b). TryAdd: the App already registers TimeProvider.System
        // itself and hosts/tests may supply their own (e.g. FakeTimeProvider) — first one wins.
        // SchedulerHostedService is deliberately NOT registered here; only headless entry points
        // (the console's --serve flag) add it, so interactive hosts never tick cron jobs.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ScheduleJobRunner>();

        return services;
    }

    private static async ValueTask<Stream> ConnectToSafeAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (addresses.Any(UrlSafetyGuard.IsBlockedAddress))
            throw new InvalidOperationException($"Blocked private, loopback, or link-local address for host '{host}'.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
