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

public sealed partial class ModelsProvidersSettingsPage : Page
{
    public ModelsProvidersSettingsViewModel ViewModel { get; } =
        App.Services.GetRequiredService<ModelsProvidersSettingsViewModel>();

    private readonly ILogger<ModelsProvidersSettingsPage> _logger =
        App.Services.GetRequiredService<ILogger<ModelsProvidersSettingsPage>>();

    public ModelsProvidersSettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            if (ViewModel.Models.Count == 0)
                await ViewModel.LoadModelsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the load path's failure surface (config I/O
            // plus a live provider model-catalog fetch) isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    private void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetProvider(ViewModel.CurrentProvider);

    // Must stay an instance method for WinUI's generated event wiring.
#pragma warning disable CA1822
    private void RestartApp_Click(object sender, RoutedEventArgs e) =>
        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
#pragma warning restore CA1822

    private void ModelPicker_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ViewModel.FilterModels(sender.Text);
    }

    // Must stay an instance method: WinUI's generated Connect() wires XAML event handlers via an
    // instance reference, even though this particular handler doesn't touch instance state.
#pragma warning disable CA1822
    private void ModelPicker_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is ModelItemViewModel model)
            sender.Text = model.Id;
    }
#pragma warning restore CA1822

    private void ModelPicker_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var model = (args.ChosenSuggestion as ModelItemViewModel)?.Id ?? args.QueryText.Trim();
        if (!string.IsNullOrWhiteSpace(model))
            ViewModel.SetModel(model);
    }
}
