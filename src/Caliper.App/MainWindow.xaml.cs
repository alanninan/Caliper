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
    // Pre-Loaded fallback for the caption-button reservation (matches the XAML default); also used
    // whenever the live insets report 0, which they can early in window startup.
    private const double FallbackCaptionInsetDips = 144;

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
        // Loaded sets the initial caption reservation (insets aren't reliable before then);
        // SizeChanged re-applies it on resize/DPI moves. Per the title-bar customization docs,
        // AppWindow.Changed is NOT suitable here — during maximize/minimize it can fire before the
        // title bar element is resized, so the calculation would use stale values.
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarCaptionInsets();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarCaptionInsets();
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
        }
        else
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(windowHandle) / 96.0;
            var size = ClampSizeToWorkArea(new SizeInt32((int)(1400 * scale), (int)(900 * scale)));
            AppWindow.Resize(size);
        }

        // Apply the floating bounds above first, then maximize on top of them — this way the
        // stored restore rect (used the next time the window is un-maximized) is always the
        // clamped floating bounds, never the maximized rect.
        if (preferences.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
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
        // Closing while maximized must not clobber the saved restore rect with the maximized
        // rect (the size of the whole work area) — only persist Position/Size while floating, and
        // always persist IsMaximized so the next launch can restore-then-maximize (ApplyWindowBounds).
        var isMaximized = (AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
        var updated = _preferences.Load() with { IsMaximized = isMaximized };
        if (!isMaximized)
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            updated = updated with
            {
                WindowX = position.X,
                WindowY = position.Y,
                WindowWidth = size.Width,
                WindowHeight = size.Height,
            };
        }

        _preferences.Save(updated);
    }

    // B9: the caption-button reservation used to be a hardcoded Padding="16,0,144,0". The real
    // inset varies with DPI and is mirrored under RTL, so size it from the live
    // AppWindow.TitleBar.RightInset/LeftInset instead. Those values are physical pixels; XAML
    // wants DIPs, so divide by XamlRoot.RasterizationScale.
    private void UpdateTitleBarCaptionInsets()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0)
            scale = 1.0;

        // The insets describe *visual* sides (physical pixels), while Thickness is flow-relative
        // (mirrored under RTL). The caption buttons sit at the visual right in LTR (RightInset)
        // and the visual left in RTL (LeftInset) — the flow-relative end side in both cases, so
        // the reservation always lands in Padding's third component. FlowDirection is an inherited
        // property, so this stays correct if an RTL flow direction is ever applied app-wide.
        var captionInsetPhysical = AppTitleBar.FlowDirection == FlowDirection.RightToLeft
            ? AppWindow.TitleBar.LeftInset
            : AppWindow.TitleBar.RightInset;

        // Insets can still be 0 early in window startup — keep the fallback reservation until a
        // real value arrives (Loaded/SizeChanged re-run this).
        var endInsetDips = captionInsetPhysical > 0
            ? captionInsetPhysical / scale
            : FallbackCaptionInsetDips;

        var padding = new Thickness(16, 0, endInsetDips, 0);
        if (AppTitleBar.Padding != padding)
            AppTitleBar.Padding = padding;
    }

    private void ApplyTitleBarButtonColors()
    {
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var tint = isDark ? (byte)255 : (byte)0;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(isDark ? (byte)32 : (byte)20, tint, tint, tint);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(isDark ? (byte)48 : (byte)32, tint, tint, tint);
    }
}
