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

public sealed partial class SkillsPage : Page
{
    public SkillsViewModel ViewModel { get; } = App.Services.GetRequiredService<SkillsViewModel>();

    private readonly ILogger<SkillsPage> _logger = App.Services.GetRequiredService<ILogger<SkillsPage>>();

    public SkillsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.RefreshCommand.Execute(null);

    private async void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
        await ViewModel.SelectSkillAsync(SkillList.SelectedItem as SkillItemViewModel);
        if (ActualWidth < 1008 && ViewModel.SelectedSkill is not null)
        {
            SkillsListPanel.Visibility = Visibility.Collapsed;
            SkillsDetailPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(SkillsDetailPanel, 0);
            SkillsBackButton.Visibility = Visibility.Visible;
        }
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — a WinUI-dispatched SelectionChanged handler;
            // an escaping exception here crashes the app, and skill-file loading's failure surface
            // isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(SkillList_SelectionChanged));
        }
    }

    private void SkillsBack_Click(object sender, RoutedEventArgs e)
    {
        SkillsDetailPanel.Visibility = Visibility.Collapsed;
        SkillsListPanel.Visibility = Visibility.Visible;
        SkillList.Focus(FocusState.Programmatic);
    }
}
