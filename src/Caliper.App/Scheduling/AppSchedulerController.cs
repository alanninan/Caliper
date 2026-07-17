// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caliper.App.Scheduling;

/// <summary>
/// Roadmap P2 — an opt-in, host-local substitute for the console's <c>--serve</c> flag:
/// <see cref="SchedulerHostedService"/> is a <c>BackgroundService</c> that Core deliberately does
/// not register (see its own remarks), so nothing ticks <c>Caliper:Schedules</c> unless a host
/// explicitly starts one. This controller is that explicit start/stop seam for the WinUI app: a
/// fresh <see cref="SchedulerHostedService"/> instance is created via
/// <see cref="ActivatorUtilities.CreateInstance(IServiceProvider, System.Type, object[])"/> each
/// time <see cref="StartAsync"/> runs (never reused across a stop/start cycle) so this never has
/// to reason about <c>BackgroundService</c> restart semantics — a stopped instance is simply
/// discarded and disposed.
/// </summary>
/// <remarks>
/// Registered as an App singleton so both the Schedules page (the toggle) and <c>App.xaml.cs</c>
/// (auto-start on launch, forced stop on <c>Window_Closed</c>) share the same running instance.
/// Every ticked job still runs unattended (<c>RunSpec.Unattended = true</c>), so its permission
/// prompts reach the App's own <c>RoutingPermissionPrompt</c> — deny + report, never an
/// interactive approval card — exactly like a manual "Run now" or the console's <c>--serve</c>.
/// </remarks>
public sealed class AppSchedulerController(
    IServiceProvider services,
    ILogger<AppSchedulerController> logger)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private SchedulerHostedService? _service;

    public bool IsRunning => _service is not null;

    /// <summary>Started/stopped events (successful or not) so a bound page can refresh its status text.</summary>
    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_service is not null)
            return;

        var service = ActivatorUtilities.CreateInstance<SchedulerHostedService>(services);
        try
        {
            await service.StartAsync(ct);
        }
        catch (Exception ex)
        {
            // A11-style resilience boundary: a failed start (e.g. cancellation racing shutdown)
            // must never crash the app — the toggle just stays effectively off.
            logger.LogError(ex, "In-app scheduler failed to start.");
            service.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _service = service;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        if (_service is not { } service)
            return;

        _service = null;
        try
        {
            using var timeoutSource = new CancellationTokenSource(StopTimeout);
            await service.StopAsync(timeoutSource.Token);
        }
        catch (Exception ex)
        {
            // Mirrors App.xaml.cs's bounded MCP shutdown: a scheduler that won't stop within the
            // timeout must never block window teardown.
            logger.LogWarning(ex, "In-app scheduler did not stop cleanly within the shutdown timeout.");
        }
        finally
        {
            service.Dispose();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
