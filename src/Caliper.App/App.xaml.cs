// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.Permissions;
using Caliper.App.Preferences;
using Caliper.App.Security;
using Caliper.App.ViewModels;
using Caliper.App.ViewModels.Settings;
using Caliper.Core;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Caliper.Core.Logging;
using Caliper.Core.Permissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace Caliper.App;

public partial class App : Application
{
    private IHost? _host;

    public static Window Window { get; private set; } = null!;
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CaliperHome.EnsureInitialized();
        AppNotificationManager.Default.Register();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        // Core reports degraded states (respond-only fallback, tokenizer fallback, MCP errors) only
        // via ILogger, and every App "A11" resilience boundary logs its swallowed exception the same
        // way. Mirror the Console: Warning+ globally, and persisted to the same shared log file, so
        // none of it is silently lost outside a debugger.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new FileLoggerProvider(
            Path.Combine(CaliperHome.LogsPath, "caliper.log"),
            LogLevel.Warning,
            TimeProvider.System));
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(CaliperHome.ConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CALIPER_");

        var credentialStore = new WindowsCredentialStore();
        var secrets = ResolveStoredSecrets(credentialStore);
        if (secrets.Count > 0)
            builder.Configuration.AddInMemoryCollection(secrets);

        builder.Services.AddCaliperCore(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        builder.Services.AddSingleton<IUiDispatcher, DispatcherQueueAdapter>();
        builder.Services.AddSingleton<IAppPreferencesStore, AppPreferencesStore>();
        builder.Services.AddSingleton<ISessionUsageStore, SessionUsageStore>();
        builder.Services.AddSingleton<ICredentialStore>(credentialStore);
        builder.Services.AddSingleton<ApprovalService>();
        builder.Services.AddSingleton<IPermissionPrompt>(services =>
            services.GetRequiredService<ApprovalService>());
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<IChatSessionController>(services =>
            services.GetRequiredService<ChatViewModel>());
        builder.Services.AddSingleton<SessionsViewModel>();
        builder.Services.AddSingleton<SkillsViewModel>();
        builder.Services.AddSingleton<MemoryViewModel>();
        builder.Services.AddSingleton<GeneralSettingsViewModel>();
        builder.Services.AddSingleton<ModelsProvidersSettingsViewModel>();
        builder.Services.AddSingleton<AgentBehaviorSettingsViewModel>();
        builder.Services.AddSingleton<ContextMemorySettingsViewModel>();
        builder.Services.AddSingleton<ToolsSettingsViewModel>();
        builder.Services.AddSingleton<PermissionsSettingsViewModel>();
        builder.Services.AddSingleton<McpServersSettingsViewModel>();
        builder.Services.AddSingleton<SearchSettingsViewModel>();
        builder.Services.AddSingleton<AdvancedSettingsViewModel>();

        _host = builder.Build();
        Services = _host.Services;
        _ = Services.GetRequiredService<IOptionsMonitor<CaliperOptions>>().CurrentValue;
        Window = new MainWindow();
        Window.Closed += Window_Closed;
        Window.Activate();

        _ = ConnectMcpAsync();
    }

    private static Dictionary<string, string?> ResolveStoredSecrets(WindowsCredentialStore credentialStore)
    {
        var secrets = new Dictionary<string, string?>();
        if (credentialStore.TryRead(CredentialTargets.OpenRouterApiKey, out var openRouterKey))
            secrets["Providers:OpenRouter:ApiKey"] = openRouterKey;
        if (credentialStore.TryRead(CredentialTargets.GeminiApiKey, out var geminiKey))
            secrets["Providers:Gemini:ApiKey"] = geminiKey;
        if (credentialStore.TryRead(CredentialTargets.SearchApiKey, out var searchKey))
            secrets["Search:ApiKey"] = searchKey;

        foreach (var serverName in ReadConfiguredMcpServerNames())
        {
            if (credentialStore.TryRead(CredentialTargets.McpBearerToken(serverName), out var token))
                secrets[$"Mcp:Servers:{serverName}:BearerToken"] = token;
        }

        return secrets;
    }

    private static IReadOnlyList<string> ReadConfiguredMcpServerNames()
    {
        try
        {
            if (!File.Exists(CaliperHome.ConfigPath))
                return [];

            using var document = JsonDocument.Parse(File.ReadAllText(CaliperHome.ConfigPath));
            if (!document.RootElement.TryGetProperty("Mcp", out var mcp) ||
                !mcp.TryGetProperty("Servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return [.. servers.EnumerateObject().Select(static property => property.Name)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task ConnectMcpAsync()
    {
        try
        {
            await Services.GetRequiredService<IMcpHub>().ConnectAllAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A11: top-level startup-resilience boundary — MCP servers are arbitrary user-configured
            // external processes/HTTP endpoints, so the realistic failure set (process launch,
            // network, protocol/JSON errors from third-party server implementations) isn't
            // enumerable; a connection failure must never abort app startup.
            Services.GetRequiredService<ILogger<App>>()
                .LogError(ex, "MCP connection failed during application startup.");
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        if (_host is null)
            return;

        try
        {
            var disposeTask = _host.Services.GetRequiredService<IMcpHub>().DisposeAllAsync();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            if (completed != disposeTask)
            {
                _host.Services.GetRequiredService<ILogger<App>>()
                    .LogWarning("MCP shutdown did not complete within the shutdown timeout.");
            }
        }
        catch (Exception ex)
        {
            // A11: top-level shutdown-resilience boundary — the same unenumerable MCP surface as
            // ConnectMcpAsync above; a shutdown failure must never prevent host disposal/process exit.
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError(ex, "MCP shutdown failed.");
        }

        _host.Dispose();
        _host = null;
    }
}
