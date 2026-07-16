// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views;

public sealed partial class MemoryPage : Page
{
    public MemoryViewModel ViewModel { get; } = App.Services.GetRequiredService<MemoryViewModel>();

    private readonly ILogger<MemoryPage> _logger = App.Services.GetRequiredService<ILogger<MemoryPage>>();

    public MemoryPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.RefreshMemoryCommand.Execute(null);

    // U10: row VMs are a plain record with no commands, so the row's Edit button routes here via
    // Tag (mirroring ChatPage's ToolTemplate Tag="{x:Bind}" pattern) — copy the entry into the
    // add/edit form and expand the section so the user sees where the values landed.
    private void EditMemory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MemoryItemViewModel item })
            return;

        ViewModel.PrefillFromEntry(item);
        AddMemoryExpander.IsExpanded = true;
    }

    // U10: the row's Forget button opens an inline confirmation Flyout (not an immediate delete);
    // this handles the confirm button inside that Flyout. The confirm button isn't the element
    // that owns the Flyout, so there's no direct FlyoutBase reference to Hide() — walking up the
    // visual tree to the hosting Popup and closing it is the standard way to dismiss a Flyout from
    // its own content.
    private async void ConfirmForget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: MemoryItemViewModel item } element)
                return;

            CloseContainingFlyout(element);
            await ViewModel.ForgetCommand.ExecuteAsync(item);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — a WinUI-dispatched Click handler; an
            // escaping exception here crashes the app, and ForgetCommand already bounds its own
            // store-failure surface, so this only guards the visual-tree walk above.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(ConfirmForget_Click));
        }
    }

    private static void CloseContainingFlyout(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is Popup popup)
            {
                popup.IsOpen = false;
                return;
            }

            current = VisualTreeHelper.GetParent(current);
        }
    }
}
