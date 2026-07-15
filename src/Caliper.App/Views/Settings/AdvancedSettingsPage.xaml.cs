// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Diagnostics;
using Caliper.App.ViewModels.Settings;
using Caliper.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Caliper.App.Views.Settings;

public sealed partial class AdvancedSettingsPage : Page
{
    public AdvancedSettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<AdvancedSettingsViewModel>();

    private readonly ILogger<AdvancedSettingsPage> _logger = App.Services.GetRequiredService<ILogger<AdvancedSettingsPage>>();

    public AdvancedSettingsPage() => InitializeComponent();

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

    private void OpenConfig_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(CaliperHome.ConfigPath) { UseShellExecute = true });

    private async void BrowsePersistencePath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = "caliper",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("SQLite database", [".db"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
                ViewModel.PersistencePath = file.Path;
        }
        catch (Exception ex)
        {
            // A11: WinRT/COM file-picker interop (FileSavePicker.PickSaveFileAsync plus window
            // handle initialization) — the failure surface isn't clearly enumerable, and this is a
            // WinUI-dispatched Click handler where an escaping exception would crash the app.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(BrowsePersistencePath_Click));
        }
    }
}
