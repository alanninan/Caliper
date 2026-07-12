// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Caliper.App.Controls;

/// <summary>
/// Static x:Bind function converters shared by every page — kept in one place so each page's
/// code-behind doesn't redeclare the same handful of bool/visibility helpers.
/// </summary>
public static class XamlConverters
{
    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static bool InvertBool(bool value) => !value;

    public static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

    public static InfoBarSeverity StatusSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    public static double DimOpacity(bool isDimmed) => isDimmed ? 0.5 : 1.0;

    public static GridLength PaneWidth(bool isCollapsed) => new(isCollapsed ? 0 : 260);

    public static double PaneMinWidth(bool isCollapsed) => isCollapsed ? 0 : 220;
}
