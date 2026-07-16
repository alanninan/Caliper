// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.ComponentModel;
using System.Globalization;
using Caliper.App.Preferences;
using Caliper.App.Security;
using Caliper.App.ViewModels;
using Caliper.Core.Abstractions;
using Caliper.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Caliper.App.Views;

public sealed partial class ChatPage : Page, INotifyPropertyChanged
{
    private const double AutoscrollPinTolerance = 24;

    // U1: lowered alongside MainWindow's 800x560 DIP minimum so both panes can be expanded at
    // once without clipping (180 + 360 workspace MinWidth + 240 + 2*4px splitters = 788 <= 800).
    private const double SessionsPaneMinWidth = 180;
    private const double InspectorPaneMinWidth = 240;
    private const double DefaultSessionsPaneWidth = 260;
    private const double DefaultInspectorPaneWidth = 320;

    public ChatViewModel ViewModel { get; } = App.Services.GetRequiredService<ChatViewModel>();
    public SessionsViewModel Sessions { get; } = App.Services.GetRequiredService<SessionsViewModel>();

    private readonly ILogger<ChatPage> _logger = App.Services.GetRequiredService<ILogger<ChatPage>>();
    private readonly TimeProvider _timeProvider = App.Services.GetRequiredService<TimeProvider>();
    private readonly ICredentialStore _credentials = App.Services.GetRequiredService<ICredentialStore>();
    private readonly IAppPreferencesStore _preferences = App.Services.GetRequiredService<IAppPreferencesStore>();
    // Whether the app was launched with a provider key already bound. Provider clients bind once at
    // startup, so a key added afterward can't actually connect until a restart.
    private readonly bool _startupHadKey = HasAnyProviderKeyConfigured();
    private readonly DispatcherTimer _approvalCountdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _initialized;
    private bool _showAddKeyWelcome;
    private bool _showRestartWelcome;
    // U2: the last known expanded width for each pane — restored on expand, captured before
    // collapse (from ActualWidth, since a pane that starts collapsed reports 0) and on every
    // splitter drag. Seeded from saved prefs (or the historical default) in ApplySavedPaneWidths.
    private double _lastExpandedSessionsWidth = DefaultSessionsPaneWidth;
    private double _lastExpandedInspectorWidth = DefaultInspectorPaneWidth;

    public event PropertyChangedEventHandler? PropertyChanged;

    // A provider key must be added (no key anywhere).
    public bool ShowAddKeyWelcome
    {
        get => _showAddKeyWelcome;
        private set { if (_showAddKeyWelcome != value) { _showAddKeyWelcome = value; RaiseWelcomeChanged(); } }
    }

    // A key now exists but the running app started without one — it needs a restart to connect.
    public bool ShowRestartWelcome
    {
        get => _showRestartWelcome;
        private set { if (_showRestartWelcome != value) { _showRestartWelcome = value; RaiseWelcomeChanged(); } }
    }

    public bool ShowFirstRunWelcome => ShowAddKeyWelcome || ShowRestartWelcome;

    public string WelcomeTitle => ShowRestartWelcome ? "Almost ready" : "Welcome to Caliper";

    public string WelcomeMessage => ShowRestartWelcome
        ? "A provider key is saved but the app started without one. Restart Caliper to connect."
        : "No model provider is configured yet. Add an OpenRouter or Gemini API key in Settings to start chatting.";

    private void RaiseWelcomeChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAddKeyWelcome)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRestartWelcome)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFirstRunWelcome)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WelcomeTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WelcomeMessage)));
    }

    private static bool HasAnyProviderKeyConfigured()
    {
        var providers = App.Services.GetRequiredService<IOptions<ProvidersOptions>>().Value;
        return !string.IsNullOrWhiteSpace(providers.OpenRouter.ApiKey) ||
               !string.IsNullOrWhiteSpace(providers.Gemini.ApiKey);
    }

    // Read the live key state — credential store first (the app's real key home), falling back to
    // the startup config snapshot. Unlike the bound IOptions snapshot, this reflects a key the user
    // just saved without a restart, so the welcome card can update instead of looping the user back.
    private bool HasAnyProviderKeyConfiguredLive() =>
        _startupHadKey ||
        _credentials.TryRead(CredentialTargets.OpenRouterApiKey, out var openRouter) && !string.IsNullOrWhiteSpace(openRouter) ||
        _credentials.TryRead(CredentialTargets.GeminiApiKey, out var gemini) && !string.IsNullOrWhiteSpace(gemini);

    private void RefreshFirstRunState()
    {
        var hasKeyNow = HasAnyProviderKeyConfiguredLive();
        ShowAddKeyWelcome = !hasKeyNow;
        ShowRestartWelcome = hasKeyNow && !_startupHadKey;
    }

    public ChatPage()
    {
        InitializeComponent();
        RefreshFirstRunState();
        // A multi-line TextBox (AcceptsReturn=True) handles the Enter KeyDown itself — inserting the
        // newline and marking the routed event Handled — so a XAML-registered KeyDown handler is
        // never invoked for Enter. Register with handledEventsToo:true so Enter can send while
        // Shift+Enter still inserts a newline.
        PromptTextBox.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(PromptTextBox_KeyDown),
            handledEventsToo: true);
        ViewModel.TranscriptChanged += ViewModel_TranscriptChanged;
        ViewModel.RunActivityChanged += ViewModel_RunActivityChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Sessions.DeleteRequested += Sessions_DeleteRequested;
        _approvalCountdown.Tick += ApprovalCountdown_Tick;

        // U2: pane Width/MinWidth are managed here, not in XAML (see ChatWorkspace's column
        // comment) — apply the saved/remembered widths before first layout, then keep them in
        // sync with collapse toggles and persist on unload as a defensive extra (the primary save
        // points are collapse-time, above, and each splitter drag's ManipulationCompleted, below —
        // see the U2 report for why Unloaded alone isn't relied on).
        ApplySavedPaneWidths();
        Sessions.PropertyChanged += Sessions_PropertyChanged;
        Unloaded += ChatPage_Unloaded;
    }

    public static Visibility IsAdded(DiffLineKind kind) =>
        kind == DiffLineKind.Added ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsRemoved(DiffLineKind kind) =>
        kind == DiffLineKind.Removed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsModified(DiffLineKind kind) =>
        kind == DiffLineKind.Modified ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatLineNumber(int? lineNumber) =>
        lineNumber?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public static InfoBarSeverity ApprovalSeverity(bool isResolved, bool isDenied) =>
        isDenied
            ? InfoBarSeverity.Error
            : isResolved
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;

    public static Visibility EmptyStateVisibility(bool hasMessages, bool showFirstRunWelcome) =>
        !hasMessages && !showFirstRunWelcome ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility WelcomeCardVisibility(bool hasMessages, bool showFirstRunWelcome) =>
        !hasMessages && showFirstRunWelcome ? Visibility.Visible : Visibility.Collapsed;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Runs on every navigation (the page is cached): re-check whether a provider key was added
        // in Settings so the first-run card reflects reality instead of a one-time startup snapshot.
        RefreshFirstRunState();

        if (_initialized)
            return;

        _initialized = true;
        try
        {
            await Sessions.InitializeAsync(CancellationToken.None);
            ViewModel.RefreshContextCommandState();
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — WinUI invokes OnNavigatedTo directly, so an
            // escaping exception crashes the app, and the load path's failure surface (session
            // store I/O, context accounting) isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    private void RestartApp_Click(object sender, RoutedEventArgs e) =>
        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);

    private void InspectTool_Click(object sender, RoutedEventArgs e)
    {
        // An ItemsRepeater with an x:Bind DataTemplate does not set each element's DataContext
        // (unlike ListView), so read the item captured via Tag="{x:Bind}" rather than DataContext.
        if (sender is FrameworkElement { Tag: ToolCallViewModel tool })
            ViewModel.SelectTool(tool);
    }

    private void CloseDetails_Click(object sender, RoutedEventArgs e) => ViewModel.ClearSelectedTool();

    private void FocusPrompt_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = PromptTextBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void StopAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Escape is also how dialogs, flyouts, and AutoSuggestBox suggestions dismiss themselves;
        // only claim it here while a run is actually active.
        if (!ViewModel.IsRunning)
        {
            args.Handled = false;
            return;
        }

        ViewModel.StopCommand.Execute(null);
        args.Handled = true;
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (Sessions.IsUpdatingSelection)
                return;

            if (SessionList.SelectedItem is not SessionItemViewModel session)
            {
                // A group header was clicked - headers aren't selectable; restore the real selection.
                SessionList.SelectedItem = Sessions.SelectedSession;
                return;
            }

            if (string.Equals(ViewModel.CurrentSessionId, session.Id, StringComparison.Ordinal))
                return;

            await Sessions.SelectAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — a WinUI-dispatched SelectionChanged handler;
            // an escaping exception here crashes the app, and session load's failure surface
            // (transcript store I/O, context rebuild) isn't safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(SessionList_SelectionChanged));
        }
    }

    private void SessionList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F2 && SessionList.SelectedItem is SessionItemViewModel session)
        {
            session.BeginRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void SessionRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionItemViewModel session })
            session.BeginRenameCommand.Execute(null);
    }

    private void SessionRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionItemViewModel session })
            return;

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (session.CommitRenameCommand.CanExecute(null))
                session.CommitRenameCommand.Execute(null);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            session.CancelRenameCommand.Execute(null);
        }
    }

    private void SessionRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SessionItemViewModel { IsEditing: true } session } &&
            session.CommitRenameCommand.CanExecute(null))
        {
            session.CommitRenameCommand.Execute(null);
        }
    }

    private async void Sessions_DeleteRequested(object? sender, SessionDeleteRequestedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = "Delete session?",
                Content = $"Delete \"{e.Session.Title}\" and its saved transcript? This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Sessions.ConfirmDeleteAsync(e.Session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A11: top-level UI-resilience boundary — combines ContentDialog's own WinRT interop
            // surface with session-store deletion I/O; an escaping exception here crashes the app,
            // and neither side is safely enumerable.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(Sessions_DeleteRequested));
        }
    }

    private void ViewModel_TranscriptChanged(object? sender, EventArgs e)
    {
        var pinned = IsPinnedToBottom();
        _ = DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (pinned)
                {
                    TranscriptScrollViewer.ChangeView(null, TranscriptScrollViewer.ScrollableHeight, null);
                    JumpToLatestButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    JumpToLatestButton.Visibility = Visibility.Visible;
                }
            });
    }

    private void ViewModel_RunActivityChanged(object? sender, EventArgs e) =>
        ViewModel.RefreshContextCommandState();

    private bool IsPinnedToBottom() =>
        TranscriptScrollViewer.ScrollableHeight - TranscriptScrollViewer.VerticalOffset <= AutoscrollPinTolerance;

    private void TranscriptScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (IsPinnedToBottom())
            JumpToLatestButton.Visibility = Visibility.Collapsed;
    }

    private void JumpToLatest_Click(object sender, RoutedEventArgs e)
    {
        TranscriptScrollViewer.ChangeView(null, TranscriptScrollViewer.ScrollableHeight, null);
        JumpToLatestButton.Visibility = Visibility.Collapsed;
    }

    private void PromptTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (shiftPressed)
            return;

        e.Handled = true;
        SendOrQueue();
    }

    // Shared by plain Enter in the composer, the Ctrl+Enter page-level accelerator, and (via the
    // bound commands directly) the Send button, so the three paths can never drift apart: send
    // when idle, queue when a run is active, no-op on an empty/whitespace prompt (the commands'
    // CanExecute already encodes that).
    private void SendOrQueue()
    {
        if (ViewModel.IsRunning)
        {
            if (ViewModel.QueueMessageCommand.CanExecute(null))
                ViewModel.QueueMessageCommand.Execute(null);
        }
        else if (ViewModel.SendCommand.CanExecute(null))
        {
            ViewModel.SendCommand.Execute(null);
        }
    }

    // B7: promoted from the Send button's KeyboardAccelerator, which lived inside a Visibility=
    // Collapsed button while a run was active — collapsed elements don't process accelerators, so
    // Ctrl+Enter went dead mid-run even though the F1 dialog documents it as always-on.
    private void SendOrQueueAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SendOrQueue();
    }

    // B3: promoted from the New-session button's KeyboardAccelerator, which lived inside the
    // sessions pane Border that gets Visibility=Collapsed when the pane is hidden — collapsed
    // elements don't process accelerators, so Ctrl+N went dead whenever the pane was toggled off.
    private void NewSessionAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Sessions.NewSessionCommand.CanExecute(null))
            Sessions.NewSessionCommand.Execute(null);
    }

    private void ApproveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.PendingApproval is not { } approval || !approval.AllowCommand.CanExecute(null))
            return;

        approval.AllowCommand.Execute(null);
        args.Handled = true;
    }

    private void DenyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.PendingApproval is not { } approval || !approval.DenyCommand.CanExecute(null))
            return;

        approval.DenyCommand.Execute(null);
        args.Handled = true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsInspectorCollapsed))
        {
            ApplyInspectorCollapseChange();
            return;
        }

        if (e.PropertyName != nameof(ChatViewModel.PendingApproval))
            return;

        if (ViewModel.PendingApproval is { } approval)
        {
            _approvalCountdown.Start();
            UpdateApprovalCountdownText(approval);
            NotifyApprovalIfWindowInactive(approval);
        }
        else
        {
            _approvalCountdown.Stop();
            ApprovalCountdownText.Text = string.Empty;
        }
    }

    // U2: applies the saved/remembered pane widths before first layout. Values fall back to the
    // historical defaults for prefs files that predate this feature (or a pane never resized),
    // and are clamped to each pane's MinWidth so a stale saved value narrower than the current
    // MinWidth (e.g. after this change lowered them) can't leave the column under-sized.
    private void ApplySavedPaneWidths()
    {
        var preferences = _preferences.Load();
        _lastExpandedSessionsWidth = Math.Max(preferences.SessionsPaneWidth ?? DefaultSessionsPaneWidth, SessionsPaneMinWidth);
        _lastExpandedInspectorWidth = Math.Max(preferences.InspectorPaneWidth ?? DefaultInspectorPaneWidth, InspectorPaneMinWidth);

        SetColumnWidth(SessionsColumn, Sessions.IsPaneCollapsed, _lastExpandedSessionsWidth, SessionsPaneMinWidth);
        SetColumnWidth(InspectorColumn, ViewModel.IsInspectorCollapsed, _lastExpandedInspectorWidth, InspectorPaneMinWidth);
    }

    private static void SetColumnWidth(ColumnDefinition column, bool isCollapsed, double expandedWidth, double minWidth)
    {
        if (isCollapsed)
        {
            column.Width = new GridLength(0);
            column.MinWidth = 0;
        }
        else
        {
            column.Width = new GridLength(expandedWidth);
            column.MinWidth = minWidth;
        }
    }

    private void Sessions_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionsViewModel.IsPaneCollapsed))
            return;

        // PITFALL: capture ActualWidth before zeroing the column, and only when it's positive —
        // a pane that starts collapsed (e.g. at construction) reports ActualWidth 0, and blindly
        // capturing that would clobber the remembered/saved width with 0.
        if (Sessions.IsPaneCollapsed && SessionsColumn.ActualWidth > 0)
            _lastExpandedSessionsWidth = Math.Max(SessionsColumn.ActualWidth, SessionsPaneMinWidth);

        SetColumnWidth(SessionsColumn, Sessions.IsPaneCollapsed, _lastExpandedSessionsWidth, SessionsPaneMinWidth);
        PersistPaneWidths();
    }

    private void ApplyInspectorCollapseChange()
    {
        if (ViewModel.IsInspectorCollapsed && InspectorColumn.ActualWidth > 0)
            _lastExpandedInspectorWidth = Math.Max(InspectorColumn.ActualWidth, InspectorPaneMinWidth);

        SetColumnWidth(InspectorColumn, ViewModel.IsInspectorCollapsed, _lastExpandedInspectorWidth, InspectorPaneMinWidth);
        PersistPaneWidths();
    }

    // Chosen over Page.Unloaded as the authoritative save point for a completed drag: ChatPage is
    // NavigationCacheMode=Enabled and shares MainPage's root Frame with Settings/Skills/Memory, so
    // Unloaded fires reliably when navigating to one of those (still useful as a defensive extra,
    // below) but is not guaranteed to run through to completion on process/window teardown.
    // SizerBase (the CommunityToolkit GridSplitter's base class) drives its own resize entirely
    // off the standard UIElement ManipulationStarted/ManipulationCompleted routed events — a
    // second handler on the same element fires alongside the control's internal one — so this is
    // the same, well-defined "drag ended" signal the control itself relies on, not a proxy for it.
    private void SessionsGridSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (!Sessions.IsPaneCollapsed && SessionsColumn.ActualWidth > 0)
            _lastExpandedSessionsWidth = Math.Max(SessionsColumn.ActualWidth, SessionsPaneMinWidth);
        PersistPaneWidths();
    }

    private void DetailsGridSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (!ViewModel.IsInspectorCollapsed && InspectorColumn.ActualWidth > 0)
            _lastExpandedInspectorWidth = Math.Max(InspectorColumn.ActualWidth, InspectorPaneMinWidth);
        PersistPaneWidths();
    }

    private void ChatPage_Unloaded(object sender, RoutedEventArgs e) => PersistPaneWidths();

    private void PersistPaneWidths()
    {
        var current = _preferences.Load();
        _preferences.Save(current with
        {
            SessionsPaneWidth = _lastExpandedSessionsWidth,
            InspectorPaneWidth = _lastExpandedInspectorWidth,
        });
    }

    private void ApprovalCountdown_Tick(object? sender, object e)
    {
        if (ViewModel.PendingApproval is not { } approval)
        {
            _approvalCountdown.Stop();
            return;
        }

        UpdateApprovalCountdownText(approval);
    }

    private void UpdateApprovalCountdownText(ApprovalViewModel approval)
    {
        var remaining = approval.DeadlineUtc - _timeProvider.GetUtcNow();
        ApprovalCountdownText.Text = remaining > TimeSpan.Zero
            ? $"Expires in {(int)remaining.TotalMinutes}m {remaining.Seconds}s"
            : "Expiring…";
    }

    private void NotifyApprovalIfWindowInactive(ApprovalViewModel approval)
    {
        if (App.Window is not MainWindow { IsActive: false })
            return;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText($"Approval required: {approval.ToolName}")
                .AddText(approval.Reason)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            // A11: WinRT/COM interop (AppNotificationManager.Show is documented to throw
            // COMException in some unregistered/unpackaged states, but not exhaustively so) — a
            // best-effort toast must never break the approval countdown UI it's attached to.
            _logger.LogError(ex, "Failed to show approval notification toast.");
        }
    }

    // Toggle Visibility (not Opacity): an Opacity=0 button is still focusable and hit-testable, so
    // keyboard users tab onto a button they can't see, Narrator announces it, and it intercepts
    // clicks over the bubble corner. Reveal on hover and on keyboard focus entering the bubble.
    private static void SetCopyButtonVisible(object sender, bool visible)
    {
        if (sender is FrameworkElement bubble && bubble.FindName("CopyButton") is Button copyButton)
            copyButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // Must stay instance methods: WinUI's generated Connect() wires XAML event handlers via an
    // instance reference even though these forward to a static helper.
#pragma warning disable CA1822
    private void MessageBubble_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetCopyButtonVisible(sender, visible: true);

    private void MessageBubble_PointerExited(object sender, PointerRoutedEventArgs e) =>
        SetCopyButtonVisible(sender, visible: false);

    private void MessageBubble_GettingFocus(UIElement sender, GettingFocusEventArgs args) =>
        SetCopyButtonVisible(sender, visible: true);

    private void MessageBubble_LosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        // Keep the button visible if focus is moving to the copy button itself.
        if (args.NewFocusedElement is FrameworkElement { Name: "CopyButton" })
            return;
        SetCopyButtonVisible(sender, visible: false);
    }
#pragma warning restore CA1822

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string content } || string.IsNullOrEmpty(content))
            return;

        var package = new DataPackage();
        package.SetText(content);
        Clipboard.SetContent(package);
    }

    private void GoToProviderSettings_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));

    private async void ShortcutsButton_Click(object sender, RoutedEventArgs e) => await ShowShortcutsAsync();

    private void ShowShortcutsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ShowShortcutsAsync();
    }

    private async Task ShowShortcutsAsync()
    {
        try
        {
            (string Keys, string Action)[] shortcuts =
            [
                ("Enter", "Send message"),
                ("Shift+Enter", "New line"),
                ("Ctrl+Enter", "Send message (alternate)"),
                ("Escape", "Stop the active run"),
                ("Ctrl+L", "Focus the message box"),
                ("Ctrl+N", "New session"),
                ("F2", "Rename the selected session"),
                ("Ctrl+B", "Toggle the sessions pane"),
                ("Ctrl+Shift+B", "Toggle the inspector pane"),
                ("Ctrl+Shift+K", "Compact context"),
                ("Ctrl+Shift+Delete", "Clear context"),
                ("Ctrl+Shift+Y", "Approve the pending request"),
                ("Ctrl+Shift+N", "Deny the pending request"),
                ("F1", "Show this list"),
            ];

            var list = new StackPanel { Spacing = 8 };
            foreach (var (keys, action) in shortcuts)
            {
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var keysBlock = new TextBlock { Text = keys, FontFamily = (FontFamily)Application.Current.Resources["CodeFontFamily"] };
                var actionBlock = new TextBlock { Text = action, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(actionBlock, 1);
                row.Children.Add(keysBlock);
                row.Children.Add(actionBlock);
                list.Children.Add(row);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
                Title = "Keyboard shortcuts",
                Content = new ScrollViewer { Content = list, MaxHeight = 480 },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ActualTheme,
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // A11: WinRT ContentDialog interop (most commonly InvalidOperationException if another
            // dialog is already open, but the WinRT surface isn't exhaustively documented) — reached
            // from a fire-and-forget accelerator path too, so an uncaught exception here has no
            // other backstop.
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(ShowShortcutsAsync));
        }
    }
}
