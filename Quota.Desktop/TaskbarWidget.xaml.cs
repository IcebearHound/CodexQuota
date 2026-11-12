using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;

namespace Quota.Desktop;

public partial class TaskbarWidget : Window
{
    private readonly MainWindow main;
    private readonly ObservableCollection<AccountGroupViewModel> accounts;
    private readonly TaskbarWindowHost host = new();
    private readonly DispatcherTimer openTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer attentionTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private AccountGroupViewModel[] observed = [];
    private string? selectedKey;
    private readonly string? proofDirectory;
    private bool proofSaved;
    private TaskbarFlyout? flyout;
    private bool flyoutPinned;
    internal bool FlyoutPinned => flyoutPinned;
    private bool openPending;
    private bool pendingActivation;
    private bool closed;
    private DispatcherOperation? queuedAlignment;
    private bool attentionPending;
    private RgbColor? lastBackground;
    private string? lastAccent;
    private RgbColor? lastTextColor;
    public bool NativeWindowAlive => host.WindowAlive;
    internal void RefreshPlacement() { host.InvalidateLayout(); Align(); }
    public int? DockError { get; private set; }
    public bool DockVisible => host.Visible;
    public bool DockSuppressed => host.Suppressed;
    public string UnavailableMessage => host.LayoutReady
        ? L.F("任务栏空位不足，额度条已暂停显示。请为任务栏腾出空位，或在外观设置中关闭任务栏额度条。", "There is not enough taskbar space. Free some taskbar space or disable the quota strip in Appearance settings.")
        : L.F("暂时无法读取任务栏布局。请稍候，或在外观设置中重新开关任务栏额度条。", "The taskbar layout is temporarily unavailable. Wait a moment or toggle the taskbar strip in Appearance settings.");
    public object DockState => new { Attached = host.IsAttached, Hosting = "owned-popup", Detailed = main.DetailedTaskbar, Widget = host.Bounds, Taskbar = host.TaskbarBounds, host.OccupiedBounds, ActualWidth, ActualHeight, Visible = host.Visible, host.HitTestVisible, Layered = host.Layered, TaskbarBackground = host.BackgroundColor?.Hex, TextColor = PercentText.Foreground?.ToString(), RingColor = ProgressRing.Foreground?.ToString(), TrackColor = ProgressRing.Track?.ToString(), host.LayoutReady, host.Suppressed, Signal = main.CodexStatus.Signal.ToString(), ReadIssue = main.CodexStatus.ReadIssue.ToString(), main.CodexStatus.Breathing, main.CodexStatus.NeedsInput, FlyoutVisible = flyout?.IsVisible == true, host.FlyoutBounds, host.FlyoutNativeVisible, host.FlyoutHitTestVisible, AccountCount = accounts.Count };
    public void ShowAccounts(bool activate = true)
    {
        if (closed) return;
        if (activate) flyoutPinned = true;
        KeepFlyoutOpen();
        if (!host.Visible) { openPending = true; pendingActivation = activate; return; }
        var panel = PrepareFlyout();
        panel.ShowActivated = activate;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(host.Bounds.Left, host.Bounds.Top));
        var offset = host.TaskbarBounds.CenterY < screen.WorkingArea.Top + screen.WorkingArea.Height / 2d ? -8 : 8;
        panel.ShowAnimated(() => host.PositionFlyout(panel), offset);
        if (activate) panel.Activate();
        if (proofDirectory is not null) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, SaveFlyoutProof);
    }
    private TaskbarFlyout PrepareFlyout()
    {
        if (flyout is not null) return flyout;
        flyout = new TaskbarFlyout(this, main, accounts) { ShowInTaskbar = proofDirectory is not null };
        new WindowInteropHelper(flyout).EnsureHandle();
        var body = flyout.Body;
        body.Measure(new Size(flyout.Width, 480));
        body.Arrange(new Rect(new Point(), body.DesiredSize));
        body.UpdateLayout();
        return flyout;
    }
    private void WarmFlyout()
    {
        if (closed || flyout is not null) return;
        var panel = PrepareFlyout();
        var topmost = panel.Topmost;
        panel.Topmost = false;
        panel.ShowActivated = false;
        panel.Opacity = 0;
        try { panel.Show(); panel.UpdateLayout(); }
        finally { panel.HideImmediately(); panel.Topmost = topmost; }
    }
    public void ShowInputAttention()
    {
        attentionPending = main.CodexStatus.NeedsInput;
        if (!attentionPending || !host.Visible) return;
        attentionPending = false;
        ShowAccounts(false);
        attentionTimer.Stop(); attentionTimer.Start();
    }
    internal void KeepFlyoutOpen() { closeTimer.Stop(); openTimer.Stop(); }
    internal void CloseFlyoutLater() { openTimer.Stop(); if (!flyoutPinned) closeTimer.Start(); }
    internal void CloseAccounts() { flyoutPinned = false; KeepFlyoutOpen(); attentionTimer.Stop(); openPending = false; flyout?.HideAnimated(); }
    internal void SelectDisplayedAccount(AccountGroupViewModel account) { selectedKey = account.Key; UpdateStatus(); }

    public TaskbarWidget(MainWindow main, ObservableCollection<AccountGroupViewModel> accounts, string? proofDirectory)
    {
        InitializeComponent(); this.main = main; this.accounts = accounts; this.proofDirectory = proofDirectory;
        ShowInTaskbar = proofDirectory is not null;
        Bar.MouseRightButtonUp += (_, args) =>
        {
            KeepFlyoutOpen();
            var menu = main.CreateWidgetMenu(); menu.PlacementTarget = Bar;
            menu.Closed += (_, _) => { if (!flyoutPinned) CloseFlyoutLater(); };
            menu.IsOpen = true; args.Handled = true;
        };
        RuntimeLamp.Status = main.CodexStatus;
        FullRuntimeLamp.Status = main.CodexStatus;
        RuntimeLamp.MouseEnter += (_, _) => openTimer.Stop();
        FullRuntimeLamp.MouseEnter += (_, _) => openTimer.Stop();
        RuntimeLamp.MouseLeave += (_, _) => { if (Bar.IsMouseOver) openTimer.Start(); };
        FullRuntimeLamp.MouseLeave += (_, _) => { if (Bar.IsMouseOver) openTimer.Start(); };
        Left = -10000; Top = -10000;
        SourceInitialized += (_, _) => { if (PresentationSource.FromVisual(this) is HwndSource source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; };
        accounts.CollectionChanged += AccountsChanged;
        openTimer.Tick += (_, _) => ShowAccounts(false);
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); if (!flyoutPinned && !Bar.IsMouseOver && flyout is { IsMouseOver: false }) flyout.HideAnimated(); };
        attentionTimer.Tick += (_, _) => { attentionTimer.Stop(); if (!flyoutPinned && !Bar.IsMouseOver && flyout is { IsMouseOver: false, IsActive: false }) flyout.HideAnimated(); };
        Loaded += (_, _) => { ObserveAccounts(); host.WatchForeground(QueueAlignment); Align(); Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, WarmFlyout); if (main.CodexStatus.NeedsInput) ShowInputAttention(); };
        Closed += (_, _) => { closed = true; queuedAlignment?.Abort(); host.Dispose(); flyout?.Close(); openTimer.Stop(); closeTimer.Stop(); attentionTimer.Stop(); accounts.CollectionChanged -= AccountsChanged; foreach (var account in observed) account.PropertyChanged -= AccountChanged; };
    }
    internal void QueueAlignment()
    {
        if (closed || Dispatcher.HasShutdownStarted || queuedAlignment?.Status == DispatcherOperationStatus.Pending) return;
        queuedAlignment = Dispatcher.BeginInvoke(DispatcherPriority.Background, Align);
    }
    public void Align()
    {
        if (closed) return;
        try
        {
            var attached = host.Dock(this, main.DetailedTaskbar, main.TaskbarPosition);
            DockError = null;
            if (!attached) flyout?.HideImmediately();
            if (attached)
            {
                if (flyout is { IsVisible: true, IsTransitioning: false }) host.PositionFlyout(flyout);
                if (openPending) { var activate = pendingActivation; openPending = false; ShowAccounts(activate); }
                if (attentionPending) ShowInputAttention();
                if (proofDirectory is not null && !proofSaved) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, SaveProof);
            }
        }
        catch (Win32Exception error)
        {
            host.Hide(); flyout?.HideImmediately(); DockError = error.NativeErrorCode;
            if (proofDirectory is not null)
                try { File.WriteAllText(Path.Combine(proofDirectory, "taskbar-dock-error.txt"), error.NativeErrorCode.ToString()); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        UpdateStatus();
    }
    private void AccountsChanged(object? sender, NotifyCollectionChangedEventArgs args) => ObserveAccounts();
    private void AccountChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or nameof(AccountGroupViewModel.FocusWindow) or nameof(AccountGroupViewModel.IsCurrent)) UpdateStatus();
    }
    private void ObserveAccounts()
    {
        foreach (var account in observed) account.PropertyChanged -= AccountChanged;
        observed = accounts.ToArray();
        foreach (var account in observed) account.PropertyChanged += AccountChanged;
        UpdateStatus();
    }
    private void UpdateStatus()
    {
        CompactPanel.Visibility = main.DetailedTaskbar ? Visibility.Collapsed : Visibility.Visible;
        FullPanel.Visibility = main.DetailedTaskbar ? Visibility.Visible : Visibility.Collapsed;
        DataContext = accounts.FirstOrDefault(account => account.Key == selectedKey) ?? accounts.FirstOrDefault(account => account.IsCurrent) ?? accounts.FirstOrDefault();
        if (host.BackgroundColor is not { } background) return;
        var sourceColor = (DataContext as AccountGroupViewModel)?.FocusWindow?.Color ?? "#82938D";
        if (lastBackground == background && lastAccent == sourceColor) return;
        var foreground = ContrastColors.TextOn(background, lastTextColor);
        lastTextColor = foreground;
        Foreground = new SolidColorBrush(Color.FromRgb(foreground.Red, foreground.Green, foreground.Blue));
        Resources["TaskbarText"] = Foreground;
        ProgressRing.Track = new SolidColorBrush(Color.FromArgb(65, foreground.Red, foreground.Green, foreground.Blue));
        var source = (Color)ColorConverter.ConvertFromString(sourceColor);
        var accent = ContrastColors.AccentOn(new(source.R, source.G, source.B), background);
        ProgressRing.Foreground = new SolidColorBrush(Color.FromRgb(accent.Red, accent.Green, accent.Blue));
        RuntimeLamp.ContrastBackground = Color.FromRgb(background.Red, background.Green, background.Blue);
        FullRuntimeLamp.ContrastBackground = RuntimeLamp.ContrastBackground;
        lastBackground = background; lastAccent = sourceColor;
    }
    private void BarEnter(object sender, MouseEventArgs args) { closeTimer.Stop(); if (!flyoutPinned && !RuntimeLamp.IsMouseOver && !FullRuntimeLamp.IsMouseOver) openTimer.Start(); }
    private void LeaveArea(object sender, MouseEventArgs args)
    {
        if (openPending && !pendingActivation) openPending = false;
        CloseFlyoutLater();
    }
    private void ToggleFlyout(object sender, MouseButtonEventArgs args)
    {
        KeepFlyoutOpen();
        if (flyoutPinned && flyout is { IsVisible: true }) CloseAccounts(); else ShowAccounts();
        args.Handled = true;
    }
    private void CycleAccount(object sender, MouseWheelEventArgs args)
    {
        if (accounts.Count == 0) return;
        var index = DataContext is AccountGroupViewModel selected ? accounts.IndexOf(selected) : 0;
        selectedKey = accounts[(index + (args.Delta < 0 ? 1 : accounts.Count - 1)) % accounts.Count].Key;
        UpdateStatus(); args.Handled = true;
    }
    private void SaveProof()
    {
        if (proofDirectory is null || proofSaved || !host.IsAttached || ActualWidth <= 0 || ActualHeight <= 0 || DataContext is null) return;
        try
        {
            Directory.CreateDirectory(proofDirectory);
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Bar.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Bar.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(Bar);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(proofDirectory, "taskbar-preview.png"))) encoder.Save(stream);
            File.WriteAllText(Path.Combine(proofDirectory, "taskbar-proof.json"), JsonSerializer.Serialize(new { AttachedToTaskbar = host.IsAttached, Widget = host.Bounds, Taskbar = host.TaskbarBounds, ActualWidth, ActualHeight }, new JsonSerializerOptions { WriteIndented = true }));
            proofSaved = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    private void SaveFlyoutProof()
    {
        if (proofDirectory is null || flyout is not { IsVisible: true } || flyout.Body.ActualHeight <= 0) return;
        try
        {
            var body = flyout.Body;
            var dpi = VisualTreeHelper.GetDpi(body);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(body.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(body.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(body);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(proofDirectory, "taskbar-flyout-preview.png")); encoder.Save(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
