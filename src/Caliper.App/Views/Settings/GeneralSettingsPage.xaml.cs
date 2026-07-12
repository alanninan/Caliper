// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.Preferences;
using Caliper.App.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Caliper.App.Views.Settings;

public sealed partial class GeneralSettingsPage : Page
{
    public GeneralSettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<GeneralSettingsViewModel>();

    private readonly ILogger<GeneralSettingsPage> _logger = App.Services.GetRequiredService<ILogger<GeneralSettingsPage>>();

    public GeneralSettingsPage() => InitializeComponent();

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

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Window.Content is FrameworkElement windowContent)
            windowContent.RequestedTheme = ViewModel.SelectedTheme.ToElementTheme();
    }

    private async void BrowseWorkingRoot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "Choose working root",
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                ViewModel.WorkingRoot = folder.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(BrowseWorkingRoot_Click));
        }
    }
}
