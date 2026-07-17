// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Scheduling;
using Caliper.Core.Abstractions;
using Caliper.Core.Agents;
using Caliper.Core.Context;
using Caliper.Core.Events;
using Caliper.Core.Models;
using Caliper.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Caliper.App.Tests;

/// <summary>
/// AppSchedulerController's Start/Stop transitions, exercised against a minimal
/// <see cref="ServiceProvider"/> (roadmap P2) rather than the full app DI container —
/// <c>ActivatorUtilities.CreateInstance&lt;SchedulerHostedService&gt;</c> only needs its four
/// constructor dependencies registered. With no schedules configured (<see cref="TestRuntimeSettings"/>'s
/// default <c>CaliperOptions.Schedules</c> is empty), the service's tick loop immediately parks on
/// an infinite sleep, so start/stop complete quickly without any real waiting.
/// </summary>
public sealed class AppSchedulerControllerTests
{
    private static AppSchedulerController Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRuntimeSettings>(new TestRuntimeSettings());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new ScheduleJobRunner(
            new NoopConversationOrchestrator(),
            new EmptySessionStore(),
            TimeProvider.System,
            NullLogger<ScheduleJobRunner>.Instance));
        services.AddSingleton<ILogger<SchedulerHostedService>>(NullLogger<SchedulerHostedService>.Instance);
        var provider = services.BuildServiceProvider();
        return new AppSchedulerController(provider, NullLogger<AppSchedulerController>.Instance);
    }

    [Fact]
    public async Task StartAsync_transitions_IsRunning_to_true()
    {
        var controller = Create();

        await controller.StartAsync(CancellationToken.None);

        Assert.True(controller.IsRunning);
    }

    [Fact]
    public async Task StartAsync_called_twice_is_idempotent()
    {
        var controller = Create();
        await controller.StartAsync(CancellationToken.None);

        await controller.StartAsync(CancellationToken.None);

        Assert.True(controller.IsRunning);
    }

    [Fact]
    public async Task StopAsync_transitions_IsRunning_back_to_false()
    {
        var controller = Create();
        await controller.StartAsync(CancellationToken.None);

        await controller.StopAsync();

        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task StopAsync_before_any_start_is_a_noop()
    {
        var controller = Create();

        await controller.StopAsync();

        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task Restart_after_stop_starts_a_fresh_instance()
    {
        var controller = Create();
        await controller.StartAsync(CancellationToken.None);
        await controller.StopAsync();

        await controller.StartAsync(CancellationToken.None);

        Assert.True(controller.IsRunning);
    }

    [Fact]
    public async Task StateChanged_fires_once_for_start_and_once_for_stop()
    {
        var controller = Create();
        var raiseCount = 0;
        controller.StateChanged += (_, _) => raiseCount++;

        await controller.StartAsync(CancellationToken.None);
        await controller.StopAsync();

        Assert.Equal(2, raiseCount);
    }

    private sealed class NoopConversationOrchestrator : IConversationOrchestrator
    {
        private static readonly ConversationRunResult Result = new(null, null, CompletionReason.Completed, []);

        public Task<ConversationRunResult> RunToCompletionAsync(
            string sessionId, string prompt, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<ConversationRunResult> RunToCompletionAsync(
            RunSpec spec, Func<AgentEvent, CancellationToken, ValueTask>? onEvent, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<ContextFit> ForceCompactAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(new ContextFit([], Compacted: false, BeforeTokens: null, AfterTokens: null, EstimatedPromptTokens: null));
    }

    private sealed class EmptySessionStore : ISessionStore
    {
        public Task<string> CreateAsync(string? title, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>([]);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task DeleteAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string title, CancellationToken ct) => Task.CompletedTask;

        public Task ReplaceWithCompactionAsync(string sessionId, ContextFit fit, CancellationToken ct) => Task.CompletedTask;
    }
}
