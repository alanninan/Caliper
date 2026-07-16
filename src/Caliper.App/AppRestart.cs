// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
namespace Caliper.App;

/// <summary>
/// One-line wrapper around <c>AppInstance.Restart</c>, shared by every settings page's
/// "Restart Caliper" action (U5) plus <c>ChatPage</c>'s first-run welcome card and
/// <c>ModelsProvidersSettingsPage</c> (both predate this helper). Each page's XAML-generated
/// <c>Connect()</c> still requires an instance <c>Click</c> handler, so callers keep a one-line
/// forwarding method with the same <c>CA1822</c> suppression rather than binding to this directly.
/// </summary>
internal static class AppRestart
{
    public static void Restart() => Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
}
