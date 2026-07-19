// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Views.Settings;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views;

public sealed partial class SettingsPage : Page
{
    private static string selectedTag = "General";

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var selected = SettingsNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedTag, StringComparison.Ordinal))
            ?? SettingsNav.MenuItems.OfType<NavigationViewItem>().First();
        SettingsNav.SelectedItem = selected;
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;

        selectedTag = tag;
        var pageType = tag switch
        {
            "General" => typeof(GeneralSettingsPage),
            "ModelsProviders" => typeof(ModelsProvidersSettingsPage),
            "AgentBehavior" => typeof(AgentBehaviorSettingsPage),
            "ContextMemory" => typeof(ContextMemorySettingsPage),
            "Tools" => typeof(ToolsSettingsPage),
            "Subagents" => typeof(SubagentsSettingsPage),
            "Permissions" => typeof(PermissionsSettingsPage),
            "Execution" => typeof(ExecutionSettingsPage),
            "Mcp" => typeof(McpServersSettingsPage),
            "Search" => typeof(SearchSettingsPage),
            "Advanced" => typeof(AdvancedSettingsPage),
            _ => (System.Type?)null,
        };

        if (pageType is not null)
            SettingsFrame.Navigate(pageType);
    }
}
