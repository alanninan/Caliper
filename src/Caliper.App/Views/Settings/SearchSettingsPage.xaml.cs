// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }
}
