// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views.Settings;

public sealed partial class SubagentsSettingsPage : Page
{
    public SubagentsSettingsViewModel ViewModel { get; } =
        App.Services.GetRequiredService<SubagentsSettingsViewModel>();

    private readonly ILogger<SubagentsSettingsPage> _logger =
        App.Services.GetRequiredService<ILogger<SubagentsSettingsPage>>();

    public SubagentsSettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the load command's failure surface isn't
            // safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    // Must stay an instance method for WinUI's generated event wiring.
#pragma warning disable CA1822
    private void RestartApp_Click(object sender, RoutedEventArgs e) => AppRestart.Restart();
#pragma warning restore CA1822

    private async void RemoveSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.SelectedProfile is not { } profile || !ViewModel.CanRemoveSelected)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = "Remove profile?",
                Content = $"Remove \"{profile.Name}\"? This takes effect once you save, and can't be undone.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ViewModel.RemoveSelectedProfileCommand.Execute(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — ContentDialog's WinRT interop surface isn't
            // safely enumerable (e.g. InvalidOperationException if another dialog is already open).
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(RemoveSelectedProfile_Click));
        }
    }
}
