// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Text.Json;
using Caliper.App.Permissions;
using Caliper.App.Preferences;
using Caliper.App.Scheduling;
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
        UnhandledException += App_UnhandledException;

#if DEBUG
        // Crash hardening (TO_FIX.md item 2): the official docs example pattern for diagnosing
        // "Layout cycle detected" stowed exceptions. High tracing names the looping elements in
        // Measure/Arrange traces (visible under a native debugger) and enriches the stowed-exception
        // data captured in crash dumps, so the next repro identifies the culprit element instead of
        // the bare HRESULT this app has crashed with before. The break level only trips under an
        // attached native debugger, so it's inert otherwise.
        DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;
        DebugSettings.LayoutCycleDebugBreakLevel = LayoutCycleDebugBreakLevel.Low;
#endif
    }

    // Crash hardening (TO_FIX.md item 2): a breadcrumb, not a recovery mechanism. Docs: this class of
    // failure (e.g. a layout cycle) is treated as non-recoverable by the runtime — termination happens
    // even if e.Handled is set true — and XAML may already be in an inconsistent state, so routinely
    // handling is explicitly discouraged. Never set e.Handled here. e.Exception's type/message/stack
    // aren't guaranteed to match the original error, but e.Message "in most cases" carries the
    // original message — for a layout cycle that names the failing element, which is the whole point
    // of logging this before the process dies.
    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // The host may not be built yet (a crash during OnLaunched, before Services is assigned).
        if (Services is null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unhandled exception before host startup: {e.Message}\n{e.Exception}");
            return;
        }

        try
        {
            // CA1873: guard with IsEnabled since e.Exception/e.Message are WinRT property
            // accesses the analyzer treats as potentially expensive to evaluate.
            var logger = Services.GetRequiredService<ILogger<App>>();
            if (logger.IsEnabled(LogLevel.Critical))
            {
                logger.LogCritical(
                    e.Exception,
                    "Unhandled exception reaching the application boundary: {Message}",
                    e.Message);
            }
        }
        catch
        {
            // A crashing DI container/logging pipeline must not mask the original crash — this
            // handler's only job is best-effort diagnostics on the way down.
        }
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
        // P1b: schedule job runs (both the headless --serve tick and this App's own "Run now")
        // build a RunSpec with Unattended = true, so prompts must be routed to the deny+report
        // UnattendedPermissionPrompt instead of ApprovalService's interactive card — exactly the
        // split the console REPL uses (Program.cs's RoutingPermissionPrompt wiring).
        builder.Services.AddSingleton<UnattendedPermissionPrompt>();
        builder.Services.AddSingleton<IPermissionPrompt>(services => new RoutingPermissionPrompt(
            services.GetRequiredService<ApprovalService>(),
            services.GetRequiredService<UnattendedPermissionPrompt>()));
        builder.Services.AddSingleton<AppSchedulerController>();
        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddSingleton<IChatSessionController>(services =>
            services.GetRequiredService<ChatViewModel>());
        builder.Services.AddSingleton<SessionsViewModel>();
        builder.Services.AddSingleton<SkillsViewModel>();
        builder.Services.AddSingleton<MemoryViewModel>();
        builder.Services.AddSingleton<SchedulesViewModel>();
        builder.Services.AddSingleton<RunsViewModel>();
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
        _ = StartSchedulerIfEnabledAsync();
    }

    // P2: opt-in in-app scheduler — auto-starts on launch only when the user previously turned the
    // Schedules page's toggle on (persisted in app-ui.json, not config.json: this is host-local
    // behavior, not an engine setting). Fire-and-forget like ConnectMcpAsync: startup must never
    // block on it, and AppSchedulerController.StartAsync already swallows/logs its own failures.
    private static async Task StartSchedulerIfEnabledAsync()
    {
        if (!Services.GetRequiredService<IAppPreferencesStore>().Load().RunSchedulerInApp)
            return;

        await Services.GetRequiredService<AppSchedulerController>().StartAsync(CancellationToken.None);
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
            // P2: stop the in-app scheduler before host disposal — AppSchedulerController.StopAsync
            // already bounds itself to a ~3s timeout internally (mirroring the MCP shutdown bound
            // just below), so no separate Task.WhenAny guard is needed here.
            await _host.Services.GetRequiredService<AppSchedulerController>().StopAsync();
        }
        catch (Exception ex)
        {
            // A11: top-level shutdown-resilience boundary — same reasoning as the MCP shutdown
            // catch below; a scheduler stop failure must never prevent host disposal/process exit.
            _host.Services.GetRequiredService<ILogger<App>>()
                .LogError(ex, "In-app scheduler shutdown failed.");
        }

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
