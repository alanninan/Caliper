// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Caliper.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App;

public sealed partial class MainPage : Page
{
    public RunsViewModel RunsViewModel { get; } = App.Services.GetRequiredService<RunsViewModel>();

    private readonly ILogger<MainPage> _logger = App.Services.GetRequiredService<ILogger<MainPage>>();

    public MainPage() => InitializeComponent();

    public static string InterruptedBannerMessage(int interruptedCount) =>
        $"{interruptedCount} run(s) were interrupted — view Runs.";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        RootNav.SelectedItem = RootNav.MenuItems[0];
        RootContentFrame.Navigate(typeof(ChatPage));

        try
        {
            // Roadmap P3 item 3: surfaces the startup sweep's interrupted runs immediately, without
            // waiting for the user to ever open the Runs page.
            await RunsViewModel.CheckStartupInterruptionsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the run store's failure surface (SQLite I/O)
            // isn't safely enumerable; this check must never block or crash app launch.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
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
            "Runs" => typeof(RunsPage),
            _ => (System.Type?)null,
        };

        if (pageType is not null)
            RootContentFrame.Navigate(pageType);
    }

    // The InfoBar's action button both navigates to Runs and dismisses the banner (mirrors clicking
    // the InfoBar's own close button, handled below) — either path means the user has seen it.
    private void ViewRuns_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in RootNav.MenuItems)
        {
            if (item is NavigationViewItem { Tag: "Runs" } runsItem)
            {
                RootNav.SelectedItem = runsItem;
                break;
            }
        }

        RunsViewModel.DismissStartupBannerCommand.Execute(null);
    }

    private void InterruptedRunsBanner_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        RunsViewModel.DismissStartupBannerCommand.Execute(null);
}
