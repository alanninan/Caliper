// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views.Settings;

public sealed partial class ContextMemorySettingsPage : Page
{
    public ContextMemorySettingsViewModel ViewModel { get; } =
        App.Services.GetRequiredService<ContextMemorySettingsViewModel>();

    private readonly ILogger<ContextMemorySettingsPage> _logger =
        App.Services.GetRequiredService<ILogger<ContextMemorySettingsPage>>();

    public ContextMemorySettingsPage() => InitializeComponent();

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
}
