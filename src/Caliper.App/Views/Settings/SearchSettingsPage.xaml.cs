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

public sealed partial class SearchSettingsPage : Page
{
    public SearchSettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<SearchSettingsViewModel>();

    private readonly ILogger<SearchSettingsPage> _logger = App.Services.GetRequiredService<ILogger<SearchSettingsPage>>();

    public SearchSettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            if (ViewModel.IsDirty)
                return;
            await ViewModel.LoadCommand.ExecuteAsync(null);
            SearchBackendPicker.SelectedIndex = ViewModel.IsTavilySelected ? 1 : 0;
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

    private void SearchBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchBackendPicker.SelectedItem is FrameworkElement { Tag: string backend })
            ViewModel.Backend = backend;
    }
}
