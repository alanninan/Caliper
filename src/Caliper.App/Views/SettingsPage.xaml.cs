// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Views.Settings;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
        SettingsFrame.Navigate(typeof(GeneralSettingsPage));
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;

        var pageType = tag switch
        {
            "General" => typeof(GeneralSettingsPage),
            "ModelsProviders" => typeof(ModelsProvidersSettingsPage),
            "AgentBehavior" => typeof(AgentBehaviorSettingsPage),
            "ContextMemory" => typeof(ContextMemorySettingsPage),
            "Tools" => typeof(ToolsSettingsPage),
            "Permissions" => typeof(PermissionsSettingsPage),
            "Mcp" => typeof(McpServersSettingsPage),
            "Search" => typeof(SearchSettingsPage),
            "Advanced" => typeof(AdvancedSettingsPage),
            _ => (System.Type?)null,
        };

        if (pageType is not null)
            SettingsFrame.Navigate(pageType);
    }
}
