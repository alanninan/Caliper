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

public sealed partial class PermissionsSettingsPage : Page
{
    public PermissionsSettingsViewModel ViewModel { get; } =
        App.Services.GetRequiredService<PermissionsSettingsViewModel>();

    private readonly ILogger<PermissionsSettingsPage> _logger =
        App.Services.GetRequiredService<ILogger<PermissionsSettingsPage>>();

    public PermissionsSettingsPage() => InitializeComponent();

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

    private void AddAllowlistEntry_Click(object sender, RoutedEventArgs e) =>
        AddEntry(ViewModel.ShellAutoAllowlist, NewAllowlistEntry);

    private void AddDenylistEntry_Click(object sender, RoutedEventArgs e) =>
        AddEntry(ViewModel.ShellDenylist, NewDenylistEntry);

    private void AddFileRootEntry_Click(object sender, RoutedEventArgs e) =>
        AddEntry(ViewModel.AutoAllowFileRoots, NewFileRootEntry);

    private void RemoveAllowlistEntry_Click(object sender, RoutedEventArgs e) => RemoveEntry(ViewModel.ShellAutoAllowlist, sender);
    private void RemoveDenylistEntry_Click(object sender, RoutedEventArgs e) => RemoveEntry(ViewModel.ShellDenylist, sender);
    private void RemoveFileRootEntry_Click(object sender, RoutedEventArgs e) => RemoveEntry(ViewModel.AutoAllowFileRoots, sender);

    private static void AddEntry(System.Collections.ObjectModel.ObservableCollection<string> target, TextBox source)
    {
        var value = source.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        target.Add(value);
        source.Text = string.Empty;
    }

    private static void RemoveEntry(System.Collections.ObjectModel.ObservableCollection<string> target, object sender)
    {
        if (sender is FrameworkElement { Tag: string value })
            target.Remove(value);
    }
}
