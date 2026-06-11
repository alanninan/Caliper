// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Memory;
using Caliper.Core.Models;
using Caliper.Core.Permissions;
using Caliper.Core.Persistence;
using Caliper.Core.Skills;
using Caliper.Core.Tools;
using Caliper.Core.Tools.BuiltIn;
using Caliper.Core.Tools.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            options.OpenRouter.ApiKey ??= Environment.GetEnvironmentVariable("CALIPER_OPENROUTER_KEY");
        });
        services.PostConfigure<SearchOptions>(options =>
        {
            options.ApiKey ??= Environment.GetEnvironmentVariable("CALIPER_SEARCH_KEY");
        });

        // Validation — triggers eagerly on first resolution
        services.AddSingleton<IValidateOptions<CaliperOptions>, CaliperOptionsValidator>();
        services.AddSingleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>();
        services.AddSingleton<IRuntimeSettings, RuntimeSettings>();

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
        services.AddHttpClient<OpenRouterCapabilityProvider>();
        services.AddSingleton<IModelCapabilityProvider>(sp =>
            sp.GetRequiredService<OpenRouterCapabilityProvider>());
        services.AddSingleton<IModelCatalog>(sp =>
            sp.GetRequiredService<OpenRouterCapabilityProvider>());
        services.AddSingleton<IChatClientProvider, OpenRouterChatClientProvider>();

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
        services.AddSingleton<ITool>(sp => new ShellTool(
            sp.GetRequiredService<IOptions<CaliperOptions>>(),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "bash"));
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

        // Agent runner
        services.AddSingleton<AgentRunner>();

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
