// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views;

public sealed partial class RunsPage : Page
{
    public RunsViewModel ViewModel { get; } = App.Services.GetRequiredService<RunsViewModel>();

    private readonly ILogger<RunsPage> _logger =
        App.Services.GetRequiredService<ILogger<RunsPage>>();

    public RunsPage() => InitializeComponent();

    public static Visibility EmptyStateVisibility(bool isLoading, bool hasRuns) =>
        !isLoading && !hasRuns ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ListVisibility(bool isLoading, bool hasRuns) =>
        !isLoading && hasRuns ? Visibility.Visible : Visibility.Collapsed;

    public static string ResumeButtonText(bool isResuming) => isResuming ? "Resuming…" : "Resume";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the load path's failure surface (the SQLite
            // run store) isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    // Read via Tag (not DataContext) so the handler works from the RunRowTemplate's ListView item —
    // mirrors SchedulesPage.RunNow_Click's identical reasoning.
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RunItemViewModel run })
            ViewModel.ResumeCommand.Execute(run);
    }
}
