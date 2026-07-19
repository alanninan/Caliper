// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Views;
using Caliper.App.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App;

public sealed partial class MainPage : Page
{
    private readonly AppNavigationService _navigation =
        App.Services.GetRequiredService<AppNavigationService>();

    public MainPage()
    {
        InitializeComponent();
        _navigation.NavigationRequested += Navigation_NavigationRequested;
        Unloaded += (_, _) => _navigation.NavigationRequested -= Navigation_NavigationRequested;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        RootNav.SelectedItem = RootNav.MenuItems[0];
        RootContentFrame.Navigate(typeof(ChatPage));
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            RootContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;

        var pageType = tag switch
        {
            "Chat" => typeof(ChatPage),
            "Skills" => typeof(SkillsPage),
            "Memory" => typeof(MemoryPage),
            "Schedules" => typeof(SchedulesPage),
            _ => (System.Type?)null,
        };

        if (pageType is not null)
            RootContentFrame.Navigate(pageType);
    }

    private void Navigation_NavigationRequested(object? sender, AppRoute route)
    {
        if (route == AppRoute.Settings)
        {
            RootNav.SelectedItem = RootNav.SettingsItem;
            return;
        }

        var tag = route.ToString();
        RootNav.SelectedItem = RootNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
    }
}
