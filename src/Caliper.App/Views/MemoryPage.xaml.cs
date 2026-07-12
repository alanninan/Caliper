// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Caliper.App.Views;

public sealed partial class MemoryPage : Page
{
    public MemoryViewModel ViewModel { get; } = App.Services.GetRequiredService<MemoryViewModel>();

    public MemoryPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.RefreshMemoryCommand.Execute(null);
}
