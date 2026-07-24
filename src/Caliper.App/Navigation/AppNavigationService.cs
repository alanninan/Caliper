// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
namespace Caliper.App.Navigation;

public enum AppRoute
{
    Chat,
    Skills,
    Memory,
    Schedules,
    Settings,
}

public enum SettingsRoute
{
    General,
    ModelsProviders,
    AgentBehavior,
    ContextMemory,
    Tools,
    Subagents,
    Permissions,
    Execution,
    Mcp,
    Search,
    Advanced,
}

public sealed class AppNavigationService
{
    public event EventHandler<AppRoute>? NavigationRequested;

    public void Navigate(AppRoute route) => NavigationRequested?.Invoke(this, route);
}
