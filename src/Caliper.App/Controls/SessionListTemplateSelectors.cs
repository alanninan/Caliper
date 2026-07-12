// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Caliper.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Caliper.App.Controls;

/// <summary>
/// The session list mixes group-header strings ("Today", "Yesterday", ...) with
/// SessionItemViewModel rows in one flat ItemsSource, so the template needs to branch on the
/// item's runtime type.
/// </summary>
public sealed class SessionListItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SessionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is SessionItemViewModel ? SessionTemplate : HeaderTemplate;
}
