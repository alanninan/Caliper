// Copyright 2026 Alan Ninan Thomas
// Licensed under the Apache License, Version 2.0.
// See the LICENSE file in the project root for full license information.
using System.ComponentModel;
using System.Globalization;
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

    public ChatViewModel ViewModel { get; } = App.Services.GetRequiredService<ChatViewModel>();
    public SessionsViewModel Sessions { get; } = App.Services.GetRequiredService<SessionsViewModel>();

    private readonly ILogger<ChatPage> _logger = App.Services.GetRequiredService<ILogger<ChatPage>>();
    private readonly TimeProvider _timeProvider = App.Services.GetRequiredService<TimeProvider>();
    private readonly ICredentialStore _credentials = App.Services.GetRequiredService<ICredentialStore>();
    // Whether the app was launched with a provider key already bound. Provider clients bind once at
    // startup, so a key added afterward can't actually connect until a restart.
    private readonly bool _startupHadKey = HasAnyProviderKeyConfigured();
    private readonly DispatcherTimer _approvalCountdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _initialized;
    private bool _showAddKeyWelcome;
    private bool _showRestartWelcome;

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
        ViewModel.TranscriptChanged += ViewModel_TranscriptChanged;
        ViewModel.RunActivityChanged += ViewModel_RunActivityChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Sessions.DeleteRequested += Sessions_DeleteRequested;
        _approvalCountdown.Tick += ApprovalCountdown_Tick;
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
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(OnNavigatedTo));
        }
    }

    private void RestartApp_Click(object sender, RoutedEventArgs e) =>
        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);

    private void InspectTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToolCallViewModel tool })
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
            _logger.LogError(ex, "Unhandled exception in {Handler}.", nameof(ShowShortcutsAsync));
        }
    }
}
