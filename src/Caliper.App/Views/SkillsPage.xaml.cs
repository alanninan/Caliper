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

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.LoadSkills();

    private async void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            await ViewModel.SelectSkillAsync(SkillList.SelectedItem as SkillItemViewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(SkillList_SelectionChanged));
        }
    }
}
