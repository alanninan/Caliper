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

public sealed partial class SchedulesPage : Page
{
    public SchedulesViewModel ViewModel { get; } = App.Services.GetRequiredService<SchedulesViewModel>();

    private readonly ILogger<SchedulesPage> _logger =
        App.Services.GetRequiredService<ILogger<SchedulesPage>>();

    public SchedulesPage() => InitializeComponent();

    public static Visibility EmptyStateVisibility(bool isLoading, bool hasJobs) =>
        !isLoading && !hasJobs ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility MasterDetailVisibility(bool isLoading, bool hasJobs) =>
        !isLoading && hasJobs ? Visibility.Visible : Visibility.Collapsed;

    public static string RunButtonText(bool isRunning) => isRunning ? "Running…" : "Run now";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the load path's failure surface (config I/O,
            // scheduler start/stop) isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    // Read via Tag (not DataContext) so the same handler works from both the ScheduleRowTemplate
    // (a ListView item, which does set DataContext) and the ScheduleDetailTemplate (a ContentControl
    // template) without depending on which one propagates DataContext — mirrors ChatPage's
    // InspectTool_Click, which reads Tag for the same reason inside an ItemsRepeater template.
    private void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ScheduleItemViewModel job })
            ViewModel.RunNowCommand.Execute(job);
    }

    private async void RemoveSelectedJob_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.SelectedJob is not { } job)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = "Remove schedule?",
                Content = $"Remove \"{job.Name}\"? This takes effect once you save, and can't be undone.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.RemoveSelectedJobCommand.Execute(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — ContentDialog's WinRT interop surface isn't
            // safely enumerable (e.g. InvalidOperationException if another dialog is already open).
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(RemoveSelectedJob_Click));
        }
    }
}
