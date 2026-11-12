using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace Quota.Desktop;

public partial class TaskbarFlyout : Window
{
    private readonly TaskbarWidget strip;
    private readonly MainWindow main;
    private bool wasActivated;
    private bool transitioning, hiding, closed;
    private int transitionVersion;
    private double restingTop, motionOffset = 8;
    internal bool IsTransitioning => transitioning;
    internal bool IsHiding => hiding;
    public CodexStatusViewModel CodexStatus => main.CodexStatus;
    internal Border Body => FlyoutBody;

    public TaskbarFlyout(TaskbarWidget strip, MainWindow main, ObservableCollection<AccountGroupViewModel> accounts)
    {
        this.strip = strip;
        this.main = main;
        InitializeComponent();
        Left = -10000;
        Top = -10000;
        AccountCards.ItemsSource = accounts;
        Activated += (_, _) => wasActivated = true;
        Deactivated += (_, _) => { if (wasActivated && !strip.FlyoutPinned) HideAnimated(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { wasActivated = false; ResetTransition(); } };
        SystemParameters.StaticPropertyChanged += AnimationPreferenceChanged;
        Closed += (_, _) => { closed = true; ResetTransition(); SystemParameters.StaticPropertyChanged -= AnimationPreferenceChanged; };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { strip.CloseAccounts(); args.Handled = true; } };
    }

    internal void ShowAnimated(Action position, double offset = 8)
    {
        if (closed) return;
        var wasVisible = IsVisible;
        var reversing = hiding;
        if (wasVisible && !reversing) { if (!transitioning) position(); return; }
        var opacity = wasVisible ? Opacity : 0;
        var top = Top;
        ResetTransition(preserveCurrent: wasVisible);
        motionOffset = offset;
        // Position the native window while transparent so its first frame is at the anchor.
        if (!wasVisible) { Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1; Show(); }
        position(); restingTop = Top;
        IsHitTestVisible = true;
        if (!SystemParameters.ClientAreaAnimation) { Opacity = 1; return; }
        StartTransition(opacity, 1, reversing ? top : restingTop + motionOffset, restingTop, false);
    }

    internal void HideAnimated()
    {
        if (closed || !IsVisible || hiding) return;
        if (!SystemParameters.ClientAreaAnimation) { HideImmediately(); return; }
        var opacity = Opacity; var top = Top;
        if (!transitioning) restingTop = top;
        ResetTransition(preserveCurrent: true);
        StartTransition(opacity, 0, top, restingTop + motionOffset, true);
    }

    internal void HideImmediately()
    {
        transitionVersion++;
        // Hide the HWND before clearing opacity clocks. Restoring opacity while the
        // compositor still presents the window can expose a black final frame.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) ShowNativeWindow(handle, 0);
        if (!closed && IsVisible) Hide();
        ResetTransition();
    }

    private void StartTransition(double fromOpacity, double toOpacity, double fromTop, double toTop, bool hide)
    {
        transitioning = true; hiding = hide; IsHitTestVisible = !hide;
        var version = ++transitionVersion;
        var duration = TimeSpan.FromMilliseconds(hide ? 130 : 180);
        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = new QuadraticEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut } };
        var slide = new DoubleAnimation(fromTop, toTop, duration) { EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut } };
        fade.Completed += (_, _) =>
        {
            if (closed || version != transitionVersion) return;
            if (hide) HideImmediately();
            else ResetTransition();
        };
        BeginAnimation(TopProperty, slide, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void ResetTransition(bool preserveCurrent = false)
    {
        transitionVersion++;
        var restoreTop = transitioning;
        var top = preserveCurrent || !restoreTop ? Top : restingTop;
        var opacity = preserveCurrent ? Opacity : 1;
        transitioning = false; hiding = false;
        // Set the base values first, so removing a clock preserves the current frame.
        SetValue(TopProperty, top); SetValue(OpacityProperty, opacity);
        BeginAnimation(TopProperty, null); BeginAnimation(OpacityProperty, null);
        IsHitTestVisible = true;
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowNativeWindow(nint window, int command);

    private void AnimationPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || SystemParameters.ClientAreaAnimation || closed) return;
        Dispatcher.BeginInvoke(() => { if (closed) return; if (hiding) HideImmediately(); else ResetTransition(); });
    }

    private void FlyoutEnter(object sender, MouseEventArgs args) => strip.KeepFlyoutOpen();
    private void LeaveArea(object sender, MouseEventArgs args) => strip.CloseFlyoutLater();
    private void SelectAccount(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: AccountGroupViewModel account }) { strip.SelectDisplayedAccount(account); if (!strip.FlyoutPinned) HideAnimated(); }
    }
    private void SwitchAccount(object sender, RoutedEventArgs args) { strip.CloseAccounts(); main.SwitchFromTaskbar(sender, args); }
    private void ShowIssue(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: AccountGroupViewModel account }) { strip.CloseAccounts(); main.ShowTaskbarIssues(account); }
    }
    private async void RefreshAccounts(object sender, RoutedEventArgs args) => await main.RefreshFromWidgetAsync();
    private void ClosePanel(object sender, RoutedEventArgs args) => strip.CloseAccounts();
    private void OpenSessionIssues(object sender, RoutedEventArgs args) { strip.CloseAccounts(); main.ShowSessionIssues(); }
    private void OpenSettings(object sender, RoutedEventArgs args) { strip.CloseAccounts(); main.OpenTaskbarSettings(); }
    private void OpenMain(object sender, RoutedEventArgs args) { strip.CloseAccounts(); main.Show(); main.WindowState = WindowState.Normal; main.Activate(); }
}
