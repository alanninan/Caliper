// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.Runtime.InteropServices;
using Caliper.App.Preferences;
using Caliper.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.Graphics;

namespace Caliper.App;

public sealed partial class MainWindow : Window
{
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);

    private readonly IAppPreferencesStore _preferences = App.Services.GetRequiredService<IAppPreferencesStore>();

    public ChatViewModel ViewModel { get; } = App.Services.GetRequiredService<ChatViewModel>();

    public bool IsActive { get; private set; } = true;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ApplyTitleBarButtonColors();
        Activated += (_, e) => IsActive = e.WindowActivationState != WindowActivationState.Deactivated;
        Closed += MainWindow_Closed;
        if (Content is FrameworkElement rootElement)
            rootElement.ActualThemeChanged += (_, _) => ApplyTitleBarButtonColors();

        if (!MicaController.IsSupported())
        {
            SystemBackdrop = DesktopAcrylicController.IsSupported()
                ? new DesktopAcrylicBackdrop()
                : null;
        }

        ApplySavedTheme();
        ApplyWindowBounds();
        RootFrame.Navigate(typeof(MainPage));
    }

    private void ApplySavedTheme()
    {
        // The saved theme is otherwise only applied when the user visits Settings → General, so
        // without this a restart falls back to System theme until they do.
        if (Content is FrameworkElement rootElement)
            rootElement.RequestedTheme = _preferences.Load().Theme.ToElementTheme();
    }

    private void ApplyWindowBounds()
    {
        var preferences = _preferences.Load();
        if (preferences is { WindowWidth: { } width, WindowHeight: { } height, WindowX: { } x, WindowY: { } y })
        {
            AppWindow.MoveAndResize(ClampToVisibleDisplay(new RectInt32(x, y, width, height)));
            return;
        }

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(windowHandle) / 96.0;
        var size = ClampSizeToWorkArea(new SizeInt32((int)(1400 * scale), (int)(900 * scale)));
        AppWindow.Resize(size);
    }

    // Saved bounds can reference a monitor that has since been unplugged or a layout that shrank,
    // which would restore the window fully off-screen with no way to reach it. Clamp the saved rect
    // into the work area of whichever display is nearest, and cap its size to that work area.
    private static RectInt32 ClampToVisibleDisplay(RectInt32 saved)
    {
        var display = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest);
        var work = display.WorkArea;

        var width = Math.Min(saved.Width, work.Width);
        var height = Math.Min(saved.Height, work.Height);
        var x = Math.Clamp(saved.X, work.X, Math.Max(work.X, work.X + work.Width - width));
        var y = Math.Clamp(saved.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        return new RectInt32(x, y, width, height);
    }

    private SizeInt32 ClampSizeToWorkArea(SizeInt32 size)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        return new SizeInt32(Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        _preferences.Save(_preferences.Load() with
        {
            WindowX = position.X,
            WindowY = position.Y,
            WindowWidth = size.Width,
            WindowHeight = size.Height,
        });
    }

    private void ApplyTitleBarButtonColors()
    {
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var tint = isDark ? (byte)255 : (byte)0;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(isDark ? (byte)32 : (byte)20, tint, tint, tint);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(isDark ? (byte)48 : (byte)32, tint, tint, tint);
    }
}
