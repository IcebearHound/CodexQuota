using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;
using Forms = System.Windows.Forms;

internal static class NativeWindowSmoke
{
    private static readonly Type HostType = typeof(MainWindow).Assembly.GetType("Quota.Desktop.DesktopWindowHost", true)!;
    private static readonly Type GuardType = typeof(MainWindow).Assembly.GetType("Quota.Desktop.WindowPlacementGuard", true)!;

    public static async Task Run(MainWindow main, string[] args)
    {
        await CheckParentCoordinates();
        await CheckDestroyedParent();
        await CheckSettingsInput(main, args);
        CheckPlacementNotice(main);
        await CheckTaskbarPropertyCache();
        await CheckTaskbarAreaSampling();
        await CheckTaskbarProcessExclusion();
        CheckTaskbarPolling(main);
        await CheckScrollbar();
        await InteractionSmoke.Run(main);
        await StatusScopeSmoke.Run(main);
        await InterruptedTaskSmoke.Run(main);
        await PanelInteractionSmoke.Run(main);
        await FlyoutMotionSmoke.Run(main, args);
    }

    private static async Task CheckTaskbarAreaSampling()
    {
        var sample = typeof(MainWindow).Assembly.GetType("Quota.Desktop.TaskbarWindowHost", true)!
            .GetMethod("SampleBackground", BindingFlags.Static | BindingFlags.NonPublic)!;
        var surface = new Border { BorderThickness = new Thickness(3, 2, 3, 2) };
        var fixture = new Window { Width = 260, Height = 140, Left = SystemParameters.WorkArea.Left + 50, Top = SystemParameters.WorkArea.Top + 50,
            Content = surface, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false };
        try
        {
            fixture.Show(); fixture.Activate();
            foreach (var light in new[] { false, true })
            {
                var background = light ? Color.FromRgb(230, 235, 240) : Color.FromRgb(24, 30, 36);
                surface.Background = new SolidColorBrush(background); surface.BorderBrush = light ? Brushes.Black : Brushes.White;
                surface.Child = new Border { Background = light ? Brushes.Black : Brushes.White, Margin = new Thickness(0, 5, 0, 5) };
                await Settle(fixture); await Task.Delay(150);
                var result = (RgbColor?)sample.Invoke(null, [NativeBounds(fixture)]);
                Check(result == new RgbColor(background.R, background.G, background.B), "transparent-gutter sampling ignores widget foreground and side strips");
                Check(ContrastColors.TextOn(result!.Value) == (light ? new RgbColor(0, 0, 0) : new RgbColor(255, 255, 255)), "overall background controls text contrast");
            }
            Console.WriteLine("PASS inset transparent-gutter sampling excludes foreground and capture highlight contamination in both themes");
        }
        finally { fixture.Close(); }
    }
    public static async Task CheckMainRecovery(App app)
    {
        foreach (var scenario in new[] { (Show: true, Wake: false), (Show: false, Wake: false), (Show: false, Wake: true) })
        {
            var show = scenario.Show;
            var main = (MainWindow)app.MainWindow;
            var parent = FixtureWindow(); parent.Show(); await Settle(parent);
            var host = typeof(MainWindow).GetField("desktopHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Check((bool)HostType.GetMethod("Apply")!.Invoke(host, [main, false])!, "recovery fixture starts with a floating main window");
            if (show) main.Show(); else main.Hide();
            var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            preferences.DesktopOnly = true;
            preferences.SimplifiedMode = true;
            preferences.SimplifiedLayout = "bar";
            typeof(MainWindow).GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            var store = (AccountStore)typeof(MainWindow).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            try
            {
                var before = NativeBounds(main);
                var attached = (bool)HostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [main, new WindowInteropHelper(parent).Handle, before])!;
                Check(attached, $"recovery fixture attaches the real main window (show={show}, before={before}, after={NativeBounds(main)}, handle={new WindowInteropHelper(main).Handle}, parent={GetParent(new WindowInteropHelper(main).Handle)}, expected={new WindowInteropHelper(parent).Handle})");
                parent.Close();
                if (scenario.Wake) typeof(App).GetMethod("WakeWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while ((app.MainWindow == main || app.MainWindow?.IsLoaded != true) && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
                var replacement = app.MainWindow as MainWindow;
                Check(replacement is not null && replacement != main && replacement.IsLoaded, "native parent loss rebuilds the main window");
                Check(app.Windows.OfType<MainWindow>().Count() == 1, "recovery leaves one main window");
                Check(replacement!.IsVisible == (show || scenario.Wake), "recovery preserves visibility and pending wake requests");
                var restoredStore = (AccountStore)typeof(MainWindow).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(replacement)!;
                Check(restoredStore.Root == store.Root && restoredStore.LoadSettings().DesktopOnly, "recovery retains the same account data and placement preference");
                if (show)
                {
                    Check(restoredStore.LoadSettings().GuideVersion == 0, "unexpected guide closure does not mark onboarding complete");
                    var restoredGuide = app.Windows.OfType<GuideWindow>().Single(); restoredGuide.Dismiss(); await Settle(replacement);
                    Console.WriteLine("PASS unfinished guide stays incomplete after desktop host loss");
                }
                Check(restoredStore.LoadSettings().SimplifiedMode && replacement.ActualWidth == 252 && replacement.ActualHeight < 450, "recovery preserves main compact mode");
                Console.WriteLine(scenario.Wake ? "PASS wake requests during desktop recovery open the replacement window" : show ? "PASS desktop host loss rebuilds the visible main window" : "PASS desktop host loss rebuilds the tray-hidden main window");
            }
            finally { parent.Close(); }
        }

    }

    private static async Task CheckParentCoordinates()
    {
        var parent = FixtureWindow(); parent.Show(); await Settle(parent);
        SetBounds(parent, new(100, 100, 1600, 1200));
        try
        {
            foreach (var showInTaskbar in new[] { false, true })
            {
                var child = FixtureWindow(); child.ShowInTaskbar = showInTaskbar;
                var host = Activator.CreateInstance(HostType, true)!;
                try
                {
                    child.Show(); await Settle(child); SetBounds(child, new(260, 220, 240, 240));
                    var handle = new WindowInteropHelper(child).Handle;
                    var owner = GetWindowLongPtr(handle, -8); var original = NativeBounds(child);
                    Check((bool)HostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [child, new WindowInteropHelper(parent).Handle, original])!, "fixture attachment succeeds");
                    Check(NativeBounds(child) == original && GetParent(handle) == new WindowInteropHelper(parent).Handle, $"attachment preserves screen coordinates (taskbar={showInTaskbar}, before={original}, after={NativeBounds(child)}, parent={GetParent(handle)}, expectedParent={new WindowInteropHelper(parent).Handle})");
                    await Settle(child);
                    Check(NativeBounds(child) == original && GetParent(handle) == new WindowInteropHelper(parent).Handle, "attachment survives deferred WPF layout");
                    Check(!child.ShowInTaskbar, "attached widget has no taskbar button");
                    SetBounds(child, new(100000, 100000, 240, 240));
                    var expected = ScreenLayout.EnsureVisible(NativeBounds(child), WorkAreas());
                    Check((bool)GuardType.GetMethod("EnsureVisible")!.Invoke(null, [child])!, "parented offscreen window is corrected");
                    Check(NativeBounds(child) == expected, "correction converts screen coordinates to parent coordinates");
                    Check((bool)HostType.GetMethod("Apply")!.Invoke(host, [child, false])!, "native detach succeeds");
                    Check(child.ShowInTaskbar == showInTaskbar && GetWindowLongPtr(handle, -8) == owner, "detach restores taskbar and ownership preferences");
                    Check(NativeBounds(child) == expected, "detach preserves physical position");
                    var style = GetWindowLongPtr(handle, -16);
                    Check(!(bool)HostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [child, (nint)(-1), expected])!, "invalid native parent is rejected");
                    Check(GetWindowLongPtr(handle, -16) == style && GetWindowLongPtr(handle, -8) == owner && NativeBounds(child) == expected, "failed attachment restores native style owner and position");
                    Check((bool)HostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [child, new WindowInteropHelper(parent).Handle, expected])!, "orphan fixture attaches");
                    SetParent(handle, 0);
                    var orphanBounds = NativeBounds(child);
                    Check((bool)HostType.GetMethod("Apply")!.Invoke(host, [child, true])!, "orphaned live windows reconnect to the desktop");
                    Check(GetParent(handle) != 0 && GetParent(handle) != new WindowInteropHelper(parent).Handle && NativeBounds(child) == orphanBounds, "desktop reconnect preserves physical coordinates");
                    Check((bool)HostType.GetMethod("Apply")!.Invoke(host, [child, false])!, "reconnected window can return to floating mode");
                }
                finally { child.Close(); }
            }
            Console.WriteLine("PASS desktop parenting preserves coordinates taskbar flags and native ownership");
            Console.WriteLine("PASS failed native attachment restores the original window");
            Console.WriteLine("PASS orphaned native windows reconnect to the desktop");
        }
        finally { parent.Close(); }
    }

    private static async Task CheckDestroyedParent()
    {
        var parent = FixtureWindow(); var child = FixtureWindow();
        var host = Activator.CreateInstance(HostType, true)!;
        try
        {
            parent.Show(); child.Show(); await Settle(child);
            Check((bool)HostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [child, new WindowInteropHelper(parent).Handle, NativeBounds(child)])!, "parent-loss fixture attaches");
            parent.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(NativeBounds(child).Width == 0, "closing the fixture parent destroys its child handle");
            Check(!(bool)HostType.GetMethod("Place")!.Invoke(host, [child, new PixelRect(0, 0, 1200, 900), 0.5, 0.5])!, "destroyed window rejects placement");
            Check(!(bool)HostType.GetMethod("Apply")!.Invoke(host, [child, false])!, "destroyed window rejects mode changes");
            Console.WriteLine("PASS destroyed native windows reject placement without crashing");
        }
        finally { child.Close(); parent.Close(); }
    }

    private static async Task CheckSettingsInput(MainWindow main, string[] args)
    {
        typeof(MainWindow).GetMethod("SettingsClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [main, new RoutedEventArgs()]);
        var settingsWindow = (Window)typeof(MainWindow).GetField("settingsWindow", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        try
        {
            await Settle(settingsWindow);
            var settingsForTopmost = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var originalTopmost = settingsForTopmost.Topmost;
            var openGuide = Application.Current.Windows.OfType<GuideWindow>().Single();
            foreach (var enabled in new[] { true, false })
            {
                settingsForTopmost.Topmost = enabled;
                typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                Check(settingsWindow.Topmost == main.Topmost && openGuide.Topmost == main.Topmost, "open settings and guide follow main topmost changes");
            }
            settingsForTopmost.Topmost = originalTopmost;
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            Console.WriteLine("PASS settings and guide topmost state follows the main panel live");
            Descendants<RadioButton>(settingsWindow).Single(button => button.GroupName == "settings-pages" && AutomationProperties.GetName(button) == L.T("小组件")).IsChecked = true;
            await Settle(settingsWindow);
            Check(Descendants<TextBlock>(settingsWindow).Any(text => text.Text == L.T("打开 Windows 任务栏设置"))
                && !Descendants<CheckBox>(settingsWindow).Any(box => Equals(box.Content, L.T("显示系统小组件（天气）"))), "system widgets use the supported settings entry");
            Console.WriteLine("PASS system widget controls expose the Windows settings entry and explain the restricted switch");
            Descendants<RadioButton>(settingsWindow).Single(button => button.GroupName == "settings-pages" && AutomationProperties.GetName(button) == L.T("通知")).IsChecked = true;
            await Settle(settingsWindow);
            var input = Descendants<TextBox>(settingsWindow).Single(box => AutomationProperties.GetName(box) == L.T("低额度阈值 (%)"));
            var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var previous = preferences.LowQuotaThreshold;
            foreach (var invalid in new[] { "NaN", "Infinity", "1e309", "", "0", "51", "-1" })
            {
                input.Text = invalid; await Settle(settingsWindow);
                Check(preferences.LowQuotaThreshold == previous, "invalid thresholds preserve the committed value");
                Check(input.BorderBrush.ToString() == Application.Current.FindResource("Error").ToString(), "invalid threshold shows inline feedback");
                Check(Descendants<TextBlock>(settingsWindow).Any(text => text.Text.Contains("1–50") || text.Text.Contains("1 to 50")), "threshold error explains the accepted range");
            }
            input.Text = "7.5"; await Settle(settingsWindow);
            Check(preferences.LowQuotaThreshold == 7.5 && double.IsFinite(preferences.LowQuotaThreshold), "valid threshold applies immediately");
            Console.WriteLine("PASS settings reject NaN and out-of-range thresholds with inline recovery guidance");
            settingsWindow.SizeToContent = SizeToContent.Manual; settingsWindow.Width = 360; settingsWindow.Height = 420;
            await Settle(settingsWindow);
            var tabs = Descendants<RadioButton>(settingsWindow).Where(button => button.GroupName == "settings-pages").ToArray();
            Check(((System.Windows.Controls.Primitives.UniformGrid)VisualTreeHelper.GetParent(tabs[0])).Columns == 2, "narrow settings use two navigation columns");
            var before = tabs[0].TranslatePoint(new Point(), settingsWindow);
            var scroll = Descendants<ScrollViewer>(settingsWindow).Single(viewer => viewer.Content is StackPanel);
            scroll.ScrollToBottom(); await Settle(settingsWindow);
            var after = tabs[0].TranslatePoint(new Point(), settingsWindow);
            Check(before == after && after.Y >= 0 && after.Y + tabs[0].ActualHeight < settingsWindow.ActualHeight, "settings navigation remains visible while content scrolls");
            Console.WriteLine("PASS settings navigation stays pinned and adapts to narrow windows");
            var outputIndex = Array.IndexOf(args, "--preview-dir");
            if (outputIndex >= 0 && args.Length > outputIndex + 1)
            {
                System.IO.Directory.CreateDirectory(args[outputIndex + 1]); scroll.ScrollToTop(); await Settle(settingsWindow);
                var bitmap = new RenderTargetBitmap((int)settingsWindow.ActualWidth, (int)settingsWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(settingsWindow); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(System.IO.Path.Combine(args[outputIndex + 1], "settings-narrow.png")); encoder.Save(stream);
            }
            Descendants<RadioButton>(settingsWindow).Single(button => button.GroupName == "settings-pages" && AutomationProperties.GetName(button) == L.T("通用")).IsChecked = true; await Settle(settingsWindow);
            Descendants<RadioButton>(settingsWindow).Single(button => AutomationProperties.GetName(button) == "English").IsChecked = true; await Settle(settingsWindow);
            var guide = Application.Current.Windows.OfType<GuideWindow>().Single();
            Check(settingsWindow.Title == "Settings" && guide.Title.EndsWith("· Guide"), "language changes update both settings and guide");
            Check(Descendants<TextBlock>(guide).Any(text => text.Text.Contains("demo mode")), "guide feedback updates across window language changes");
            Descendants<RadioButton>(settingsWindow).Single(button => AutomationProperties.GetName(button) == "简体中文").IsChecked = true; await Settle(settingsWindow);
            Check(guide.Title.Contains("使用导览"), "language can switch back across open windows");
            Console.WriteLine("PASS language changes synchronize settings and the open guide");
            await CheckSettingsPages(settingsWindow, args);
        }
        finally { settingsWindow.Close(); }
    }
    private static async Task CheckSettingsPages(Window window, string[] args)
    {
        var theme = typeof(MainWindow).Assembly.GetType("Quota.Desktop.ThemeService", true)!.GetMethod("Apply")!;
        var previewIndex = Array.IndexOf(args, "--preview-dir");
        var output = previewIndex >= 0 && args.Length > previewIndex + 1 ? args[previewIndex + 1] : null;
        foreach (var width in new[] { 860d, 360d })
        foreach (var language in new[] { "zh-CN", "en" })
        foreach (var preference in new[] { "dark", "light" })
        {
            L.Apply(language); theme.Invoke(null, [preference]); window.Width = width; window.Height = width < 640 ? 560 : 760;
            await Settle(window);
            foreach (var page in new[] { "外观", "小组件", "账号", "通知", "状态检测", "额度监测", "通用" })
            {
                Descendants<RadioButton>(window).Single(button => button.GroupName == "settings-pages" && AutomationProperties.GetName(button) == L.T(page)).IsChecked = true;
                await Settle(window);
                var scroll = Descendants<ScrollViewer>(window).Single(viewer => viewer.Content is StackPanel);
                Check(scroll.ViewportHeight >= 100 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, "every settings page remains vertically accessible");
                if (page == "额度监测")
                {
                    Check(Descendants<QuotaPreviewPanel>(window).Count() == 1, "settings exposes the shared quota demo");
                    var toggle = Descendants<CheckBox>(window).Single(); toggle.IsChecked = true; await Settle(window);
                    Check(Descendants<TextBlock>(window).Any(text => text.Text == "28%"), "quota demo is interactive in settings");
                }
                if (page == "状态检测")
                {
                    var preview = Descendants<StatusPreviewPanel>(window).Single();
                    var choice = Descendants<RadioButton>(preview).Last(); choice.IsChecked = true; await Settle(window);
                    Check(Descendants<StatusLamp>(preview).Single().Status?.Signal == CodexSignal.Offline, "settings status demo responds independently");
                    var playback = (DispatcherTimer)typeof(StatusPreviewPanel).GetField("Playback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    window.WindowState = WindowState.Minimized; await Settle(window);
                    Check(!playback.IsEnabled, "settings demo pauses when minimized");
                    window.WindowState = WindowState.Normal; await Settle(window);
                    Descendants<RadioButton>(window).First(button => button.GroupName == "settings-pages").IsChecked = true; await Settle(window);
                    Check(!playback.IsEnabled, "leaving settings status stops its timer");
                    Descendants<RadioButton>(window).Single(button => button.GroupName == "settings-pages" && AutomationProperties.GetName(button) == L.T(page)).IsChecked = true; await Settle(window);
                }
                if (output is not null && width == 860 && language == "zh-CN")
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(output, $"settings-{page}-{preference}.png")); encoder.Save(stream);
                }
            }
        }
        L.Apply("zh-CN"); theme.Invoke(null, ["dark"]);
        Console.WriteLine("PASS seven settings pages in both languages, both themes and both widths with standalone demos");
    }

    private static void CheckPlacementNotice(MainWindow main)
    {
        var update = typeof(MainWindow).GetMethod("UpdateRecoveryNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dismiss = typeof(MainWindow).GetMethod("DismissNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var show = typeof(MainWindow).GetMethod("ShowNotice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var notice = (TextBlock)main.FindName("Notice"); var border = (Border)main.FindName("NoticeBorder");
        var previous = (string)update.Invoke(main, [false, "Fixture placement issue", null])!;
        Check(border.Visibility == Visibility.Visible, "new placement errors are visible");
        dismiss.Invoke(main, [main, new RoutedEventArgs()]);
        update.Invoke(main, [false, "Fixture placement issue", previous]);
        Check(border.Visibility == Visibility.Collapsed, "dismissed placement errors stay dismissed while retrying");
        show.Invoke(main, ["Fixture quota issue", false]);
        update.Invoke(main, [true, "", previous]);
        Check(border.Visibility == Visibility.Visible && notice.Text == "Fixture quota issue", "placement recovery preserves unrelated account guidance");
        show.Invoke(main, [previous, false]); update.Invoke(main, [true, "", previous]);
        Check(border.Visibility == Visibility.Collapsed, "placement recovery clears its own notice");
        Console.WriteLine("PASS placement notices clear on recovery and preserve dismissals and unrelated guidance");
    }
    public static void CheckNotificationRecovery(MainWindow main, AccountStore store)
    {
        var accounts = (IEnumerable<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var source = accounts.Single(account => account.Record.IsCurrent);
        var check = typeof(MainWindow).GetMethod("CheckNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = new UsageSnapshot
        {
            Email = "notification-fixture@example.com", AccountId = "notification-fixture", FetchedAt = DateTimeOffset.UtcNow,
            Buckets = [new() { Name = "Codex", Windows = [new() { UsedPercent = 20, DurationMinutes = 300 }] }]
        };
        source.SetSnapshot(snapshot); check.Invoke(main, null);
        var notice = (TextBlock)main.FindName("Notice"); var border = (Border)main.FindName("NoticeBorder");
        using (var held = new System.IO.FileStream(System.IO.Path.Combine(store.Root, "notification-state.json"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
        {
            snapshot.Buckets[0].Windows[0].UsedPercent = 30; source.SetSnapshot(snapshot); check.Invoke(main, null);
            Check(border.Visibility == Visibility.Visible && notice.Text == L.T("通知状态未能保存，请检查本地目录权限。"), "notification storage failures show recovery guidance");
        }
        check.Invoke(main, null);
        Check(border.Visibility == Visibility.Collapsed, "successful notification retry clears its storage warning");
        Console.WriteLine("PASS notification storage recovery clears its inline warning");
        snapshot.Buckets[0].Windows[0].Kind = "secondary";
        source.SetSnapshot(snapshot); check.Invoke(main, null);
        var secondary = store.LoadAlertState()!.Windows.Values.Single(window => window.Period == "secondary");
        Check(secondary.WindowKey.EndsWith("/1", StringComparison.Ordinal), "missing primary quotas retain the secondary notification slot");
        Console.WriteLine("PASS missing primary quotas preserve the secondary notification identity");
    }
    private static async Task CheckTaskbarPropertyCache()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        Check(taskbar != 0 && GetWindowRect(taskbar, out _), "taskbar cache fixture finds the shell");
        GetWindowRect(taskbar, out var native);
        var bounds = new PixelRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        var type = typeof(MainWindow).Assembly.GetType("Quota.Desktop.TaskbarWindowHost", true)!;
        using var host = (IDisposable)Activator.CreateInstance(type, true)!;
        type.GetMethod("ProbeLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [taskbar, bounds]);
        var snapshot = type.GetField("snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        object? measured;
        while ((measured = snapshot.GetValue(host)) is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        measured = snapshot.GetValue(host);
        Check(measured is not null, "cached taskbar properties produce a layout snapshot");
        var occupied = (PixelRect[])measured!.GetType().GetProperty("Occupied")!.GetValue(measured)!;
        Check(occupied.Length > 0 && occupied.All(rectangle => rectangle.Width > 0 && rectangle.Height > 0), "cached bounds remain usable");
        Console.WriteLine("PASS batched taskbar properties produce a usable native layout");
    }

    private static void CheckTaskbarPolling(MainWindow main)
    {
        var accounts = new System.Collections.ObjectModel.ObservableCollection<AccountGroupViewModel>();
        var widget = new TaskbarWidget(main, accounts, null);
        try
        {
            var host = typeof(TaskbarWidget).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;
            var background = host.GetType().GetField("<BackgroundColor>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var update = typeof(TaskbarWidget).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            background.SetValue(host, new RgbColor(240, 240, 240)); update.Invoke(widget, null);
            var foreground = widget.Foreground;
            update.Invoke(widget, null);
            Check(ReferenceEquals(foreground, widget.Foreground), "unchanged taskbar colors preserve the existing brush");
            background.SetValue(host, new RgbColor(20, 20, 20)); update.Invoke(widget, null);
            Check(!ReferenceEquals(foreground, widget.Foreground), "changed taskbar background refreshes text contrast");
            widget.Measure(new Size(240, 60)); widget.Arrange(new Rect(0, 0, 240, 60)); widget.UpdateLayout();
            var textFields = new[] { "PercentText", "ResetText", "FullPercentText", "FullNameText", "FullResetText" }.Select(name => (TextBlock)widget.FindName(name)).ToArray();
            var theme = typeof(MainWindow).Assembly.GetType("Quota.Desktop.ThemeService", true)!.GetMethod("Apply")!;
            foreach (var lightBackground in new[] { false, true })
            {
                var backdrop = lightBackground ? new RgbColor(240, 240, 240) : new RgbColor(25, 35, 45);
                background.SetValue(host, backdrop); update.Invoke(widget, null);
                foreach (var preference in new[] { "dark", "light", "system" })
                {
                    theme.Invoke(null, [preference]); widget.UpdateLayout();
                    foreach (var text in textFields)
                    {
                        var color = ((SolidColorBrush)text.Foreground).Color;
                        Check(ContrastColors.Contrast(new(color.R, color.G, color.B), backdrop) >= 4.5, "all taskbar text stays readable across app theme changes");
                    }
                }
            }
            theme.Invoke(null, ["dark"]);
            Console.WriteLine("PASS compact and full taskbar text retains contrast across independent app themes");
            var lamp = (StatusLamp)widget.FindName("RuntimeLamp");
            lamp.Opacity = 0.3; var first = JsonSerializer.Serialize(widget.DockState);
            lamp.Opacity = 0.9;
            Check(first == JsonSerializer.Serialize(widget.DockState), "breathing phase does not rewrite taskbar diagnostics");
            Console.WriteLine("PASS taskbar polling reuses stable colors and ignores animation phase in diagnostics");
            var ready = host.GetType().GetField("<LayoutReady>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ready.SetValue(host, true); widget.Align();
            Check(!(bool)ready.GetValue(host)! && !widget.DockVisible, "failed native docking cannot retain a stale layout-ready state");
            Console.WriteLine("PASS failed taskbar docking clears stale layout readiness");
        }
        finally { widget.Close(); }
    }

    private static async Task CheckTaskbarProcessExclusion()
    {
        var type = typeof(MainWindow).Assembly.GetType("Quota.Desktop.TaskbarWindowHost", true)!;
        var window = FixtureWindow();
        window.Content = new GroupBox { Header = "Owned component branch", Content = new Button { Content = "Layout probe" } };
        try
        {
            window.Show(); await Settle(window);
            var handle = new WindowInteropHelper(window).Handle;
            var fixtureValid = await Task.Run(() =>
            {
                var controls = AutomationElement.FromHandle(handle).FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                return controls.Count > 0 && controls.Cast<AutomationElement>().All(element => element.Current.ProcessId == Environment.ProcessId);
            });
            Check(fixtureValid, "fixture exposes its own process controls");
            using var host = (IDisposable)Activator.CreateInstance(type, true)!;
            type.GetMethod("ProbeLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [handle, NativeBounds(window)]);
            var probing = type.GetField("probing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((int)probing.GetValue(host)! != 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
            Check((int)probing.GetValue(host)! == 0 && type.GetField("snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host) is null,
                "own process controls cannot become occupied taskbar layout bounds");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS taskbar layout discovery excludes the component's own process controls");
    }

    private static async Task CheckScrollbar()
    {
        var window = FixtureWindow();
        var viewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Height = 1000, Background = Brushes.Gray }
        };
        window.Content = viewer;
        try
        {
            window.Show(); await Settle(window);
            var scrollbar = Descendants<ScrollBar>(viewer).Single(bar => bar.Orientation == Orientation.Vertical);
            var thumb = Descendants<Thumb>(scrollbar).Single();
            var grip = Descendants<Border>(thumb).Single();
            Check(scrollbar.IsVisible && scrollbar.ActualWidth <= 7.1 && thumb.ActualHeight > 0 && grip.ActualWidth >= 3, "narrow scrollbar preserves its intended width and a visible grip");
            viewer.ScrollToEnd(); await Settle(window);
            Check(viewer.VerticalOffset > 0 && grip.ActualWidth >= 2, "scrollbar remains visible after scrolling");
            Console.WriteLine("PASS narrow scrollbar retains a visible grip before and after scrolling");
        }
        finally { window.Close(); }
    }

    private static Window FixtureWindow() => new()
    {
        Width = 240, Height = 240, Left = 100, Top = 100, WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brushes.Black
    };
    private static PixelRect[] WorkAreas() => Forms.Screen.AllScreens.Select(screen => new PixelRect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();
    private static void SetBounds(Window window, PixelRect bounds)
        => Check((bool)GuardType.GetMethod("SetBounds")!.Invoke(null, [window, bounds, 0u])!, "fixture window moves");
    private static PixelRect NativeBounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) return default;
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? name);
}
