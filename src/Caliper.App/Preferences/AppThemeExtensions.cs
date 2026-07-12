// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using Microsoft.UI.Xaml;

namespace Caliper.App.Preferences;

/// <summary>
/// Single source of truth for mapping the persisted <see cref="AppThemePreference"/> to WinUI's
/// <see cref="ElementTheme"/>, so the startup path and the settings page can't drift apart.
/// </summary>
public static class AppThemeExtensions
{
    public static ElementTheme ToElementTheme(this AppThemePreference preference) =>
        preference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
}
