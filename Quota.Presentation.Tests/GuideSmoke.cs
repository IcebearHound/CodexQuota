using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class GuideSmoke
{
    public static int Run(string[] args)
    {
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex < 0 || args.Length <= dataIndex + 1 || !args.Contains("--guide"))
            throw new ArgumentException("Guide smoke checks require --guide and an isolated --data-dir.");
        var demo = args.Contains("--demo");
        var fakeIndex = Array.IndexOf(args, "--fake-cli");
        if (!demo && (fakeIndex < 0 || args.Length <= fakeIndex + 1 || !File.Exists(args[fakeIndex + 1])))
            throw new ArgumentException("Connection cancellation checks require --fake-cli pointing to Quota.Tests.exe.");
        var dataDirectory = Path.GetFullPath(args[dataIndex + 1]);
        var fixtureMarker = Path.Combine(dataDirectory, ".guide-smoke-fixture");
        if (Directory.Exists(dataDirectory) && Directory.EnumerateFileSystemEntries(dataDirectory).Any() && !File.Exists(fixtureMarker))
            throw new ArgumentException("Choose a fresh data directory for guide smoke checks.");
        var store = new AccountStore(dataDirectory);
        if (args.Contains("--switching"))
        {
            if (demo) throw new ArgumentException("Switching checks require the fake CLI.");
            var currentHome = Path.Combine(dataDirectory, "current-home");
            Directory.CreateDirectory(currentHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", currentHome);
            SwitchingSmoke.WriteAuthentication(currentHome, "old-current");
            File.WriteAllText(Path.Combine(currentHome, ".quota-test-scenario"), "switch-race");
        }
        File.WriteAllText(fixtureMarker, "Quota.Presentation.Tests guide fixture");
        store.SaveSettings(new()
        {
            LayoutVersion = 4, TaskbarVersion = 1, GuideVersion = 0, Language = args.Contains("--screenshots") ? "zh-CN" : "en", Theme = "dark",
            DesktopWidgetEnabled = false, TaskbarWidgetEnabled = false, Topmost = args.Contains("--visibility-smoke"),
            Material = args.Contains("--visibility-smoke") ? "glass" : "solid",
            NotifyLowQuota = false, NotifyExhausted = false, NotifyReset = false, NotifyCardExpiry = false,
            CodexPath = demo ? null : Path.GetFullPath(args[fakeIndex + 1])
        });
        var result = 1;
        var thread = new Thread(() =>
        {
            var app = new App(); app.InitializeComponent();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timeout.Tick += (_, _) => { Console.Error.WriteLine("FAIL guide smoke timed out"); app.Shutdown(1); };
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
            {
                try
                {
                    var guide = app.Windows.OfType<GuideWindow>().Single();
                    if (args.Contains("--switching")) await SwitchingSmoke.Run((MainWindow)app.MainWindow, store);
                    else if (args.Contains("--external-host-recovery")) await ExternalHostSmoke.Run(app);
                    else if (args.Contains("--desktop-recovery")) await NativeWindowSmoke.CheckMainRecovery(app);
                    else if (args.Contains("--appearance-smoke")) await AppearanceSmoke.Run((MainWindow)app.MainWindow, args);
                    else if (args.Contains("--ui-polish")) await UiPolishSmoke.Run((MainWindow)app.MainWindow);
                    else if (args.Contains("--visibility-smoke")) await WindowVisibilitySmoke.Run((MainWindow)app.MainWindow, args);
                    else if (args.Contains("--taskbar-colors")) NativeWindowSmoke.CheckTaskbarPolling((MainWindow)app.MainWindow);
                    else if (args.Contains("--taskbar-performance")) await TaskbarPerformanceSmoke.Run((MainWindow)app.MainWindow);
                    else if (args.Contains("--attention-smoke")) await AttentionNotificationSmoke.Run((MainWindow)app.MainWindow, args);
                    else if (args.Contains("--monitoring-smoke") && demo) await CheckMonitoring(guide, (MainWindow)app.MainWindow, store, args);
                    else if (args.Contains("--screenshots") && demo) await ProductScreenshotSmoke.Run((MainWindow)app.MainWindow, args);
                    else if (demo) await CheckLayouts(guide, args);
                    else await CheckCancel(app, guide, store);
                    result = 0;
                }
                catch (Exception error) { Console.Error.WriteLine("FAIL guide smoke " + error); }
                finally { timeout.Stop(); app.Shutdown(result); }
            });
            timeout.Start(); app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(70))) { Console.Error.WriteLine("FAIL guide UI thread did not finish"); return 1; }
        return result;
    }

    private static async Task CheckMonitoring(GuideWindow guide, MainWindow main, AccountStore store, string[] args)
    {
        CheckBox Toggle(Window window, string name) => Descendants<CheckBox>(window).Single(box => AutomationProperties.GetName(box) == name);
        var tps = (FrameworkElement)main.FindName("TokenRatePanel");
        bool VisiblePace(string text) => Descendants<TextBlock>(main).Any(block => block.IsVisible && block.Text == text);
        guide.Width = 480;
        guide.ShowPage(0); await Settle(guide);
        var token = Toggle(guide, "Local output rate (TPS)");
        var pace = Toggle(guide, "Quota decline rate");
        Check(token.IsChecked == false && pace.IsChecked == false && tps.Visibility == Visibility.Collapsed && !VisiblePace("Down 1 pp/h"), "both metrics are absent by default");
        token.IsChecked = true; await Settle(main);
        Check(tps.IsVisible && TokenRateText(tps) == "12.5 tokens/s" && !VisiblePace("Down 1 pp/h"), "the guide enables TPS independently");
        pace.IsChecked = true; await Settle(main);
        Check(VisiblePace("Down 1 pp/h") && VisiblePace("Down 2.5 pp/h") && VisiblePace("Down —"), "account cards show measured and unavailable rates");
        var saved = store.LoadSettings();
        Check(saved.ShowTokenRate && saved.ShowQuotaPace, "guide choices are persisted immediately");
        var previewIndex = Array.IndexOf(args, "--preview-dir");
        if (previewIndex >= 0 && previewIndex + 1 < args.Length)
        {
            await Settle(guide); await Task.Delay(280);
            Capture(guide, args[previewIndex + 1], "guide-monitoring-en.png");
            Capture(main, args[previewIndex + 1], "dashboard-monitoring-en.png");
        }
        await CheckWidgetMonitoring(main, token, pace, previewIndex >= 0 && previewIndex + 1 < args.Length ? args[previewIndex + 1] : null);

        ((Button)main.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var settings = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "Settings");
        Descendants<RadioButton>(settings).Single(choice => AutomationProperties.GetName(choice) == "Overview").IsChecked = true;
        await Settle(main);
        Check(((FrameworkElement)main.FindName("NormalPanel")).IsVisible && VisiblePace("Down 1 pp/h") && VisiblePace("Down 2.5 pp/h"), "overview keeps the focused and other accounts' rates visible");
        var focus = (ContentControl)main.FindName("FocusAccount");
        var plan = Descendants<TextBlock>(focus).Single(block => block.Text == "Pro100");
        Check(Bounds(plan, focus).Bottom <= focus.ActualHeight - 4, "quota pace does not clip the focused account's plan");
        Descendants<RadioButton>(settings).Single(tab => AutomationProperties.GetName(tab) == "Quota monitoring").IsChecked = true;
        await Settle(settings);
        Check(Toggle(settings, "Local output rate (TPS)").IsChecked == true, "settings reflect the guide's saved TPS choice");
        Toggle(settings, "Quota decline rate").IsChecked = false; await Settle(main);
        Check(!VisiblePace("Down 1 pp/h") && tps.IsVisible && !store.LoadSettings().ShowQuotaPace, "settings disable only quota pace");
        Check(Toggle(guide, "Quota decline rate").IsChecked == false, "the open guide reflects changes from settings");
        settings.Close();
        Descendants<Button>(guide).Single(button => Equals(button.Content, "中文")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(guide); await Settle(main);
        Check(Toggle(guide, "本机输出速率（TPS）").IsChecked == true && Toggle(guide, "额度下降速度").IsChecked == false, "language switching preserves both choices");
        if (previewIndex >= 0 && previewIndex + 1 < args.Length)
        {
            Toggle(guide, "额度下降速度").IsChecked = true; await Settle(main);
            Capture(guide, args[previewIndex + 1], "guide-monitoring.png");
            Capture(main, args[previewIndex + 1], "dashboard-monitoring.png");
        }
        Toggle(guide, "本机输出速率（TPS）").IsChecked = false; await Settle(main);
        Check(tps.Visibility == Visibility.Collapsed && !store.LoadSettings().ShowTokenRate, "TPS can be switched off again");
        Console.WriteLine("PASS optional measurements in guide, settings, dashboard, and persisted preferences");
    }

    private static string TokenRateText(FrameworkElement panel) => Descendants<TextBlock>(panel).Single(block => block.Name == "TokenRateValue").Text;

    private static async Task CheckWidgetMonitoring(MainWindow main, CheckBox token, CheckBox pace, string? output)
    {
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var activity = typeof(MainWindow).GetField("activity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (CodexActivitySnapshot)activity.GetValue(main)!;
        var update = typeof(MainWindow).GetMethod("UpdateCodexStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var compactLayout = typeof(MainWindow).GetMethod("SetSimplifiedLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var compactMode = typeof(MainWindow).GetMethod("SetSimplifiedMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var flyoutLayout = typeof(TaskbarFlyout).GetMethod("SetAccountLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var widget = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(widget, main, groups);
        try
        {
            compactMode.Invoke(main, [true]);
            flyout.Show();
            var compactRate = (FrameworkElement)main.FindName("SimplifiedTokenRate");
            var flyoutRate = (FrameworkElement)flyout.FindName("TokenRatePanel");
            var compactScroll = (ScrollViewer)main.FindName("SimplifiedPanel");
            var flyoutScroll = (ScrollViewer)flyout.FindName("AccountsScroll");
            foreach (var item in new[] { (Layout: "ring", Bar: false), (Layout: "bar", Bar: true) })
            {
                compactLayout.Invoke(main, [item.Layout]);
                flyoutLayout.Invoke(flyout, [item.Bar]);
                await Settle(main); await Settle(flyout);
                foreach (var (ratePanel, scroll) in new[] { (compactRate, compactScroll), (flyoutRate, flyoutScroll) })
                {
                    Check(ratePanel.IsVisible && TokenRateText(ratePanel) == "12.5 tokens/s", $"TPS visible once on the {item.Layout} panel");
                    Check(VisiblePaceCount(scroll) == groups.Count, $"each {item.Layout} account shows its own pace, including unavailable data");
                    Check(Descendants<TextBlock>(scroll).Any(block => block.IsVisible && block.Text == "Down 2.5 pp/h"), "widget pace shows a measured account value");
                    Check(Bounds(ratePanel, (Window)Window.GetWindow(scroll)).Bottom <= Bounds(scroll, (Window)Window.GetWindow(scroll)).Top, "TPS stays above the scrolling accounts");
                }
                if (output is not null)
                {
                    Capture(main, output, "compact-monitoring-" + item.Layout + ".png");
                    Capture(flyout, output, "taskbar-monitoring-" + item.Layout + ".png");
                }
            }
            activity.SetValue(main, original with { OutputTokensPerSecond = 24 });
            update.Invoke(main, null);
            await Settle(main); await Settle(flyout);
            Check(TokenRateText(compactRate) == "24 tokens/s" && TokenRateText(flyoutRate) == "24 tokens/s", "both open widgets follow live TPS updates");
            var compactHeight = main.ActualHeight;
            token.IsChecked = false; await Settle(main); await Settle(flyout);
            Check(!compactRate.IsVisible && !flyoutRate.IsVisible && main.ActualHeight < compactHeight
                && VisiblePaceCount(compactScroll) == groups.Count && VisiblePaceCount(flyoutScroll) == groups.Count,
                "hiding TPS reclaims space and leaves both widgets' account rates visible");
            pace.IsChecked = false; await Settle(main); await Settle(flyout);
            Check(VisiblePaceCount(compactScroll) == 0 && VisiblePaceCount(flyoutScroll) == 0, "quota pace also collapses independently");
            activity.SetValue(main, original with { OutputTokensPerSecond = null });
            update.Invoke(main, null);
            token.IsChecked = true; await Settle(main); await Settle(flyout);
            Check(TokenRateText(compactRate) == "—" && TokenRateText(flyoutRate) == "—", "widgets show missing TPS without substituting zero");
            activity.SetValue(main, original); update.Invoke(main, null);
            pace.IsChecked = true;
            await CheckPinnedRates(main, widget, groups, token, pace, output);
            Console.WriteLine("PASS live TPS and per-account quota pace in both compact and taskbar panel layouts");
        }
        finally
        {
            flyout.Close(); widget.Close();
            compactMode.Invoke(main, [false]); compactLayout.Invoke(main, ["ring"]);
            activity.SetValue(main, original); update.Invoke(main, null);
            token.IsChecked = true; pace.IsChecked = true;
            await Settle(main);
        }
        static int VisiblePaceCount(DependencyObject parent) => Descendants<TextBlock>(parent).Count(block => block.IsVisible && block.Text.StartsWith("Down ", StringComparison.Ordinal));
    }

    private static async Task CheckPinnedRates(MainWindow main, TaskbarWidget widget, ObservableCollection<AccountGroupViewModel> groups, CheckBox token, CheckBox pace, string? output)
    {
        var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var select = typeof(TaskbarWidget).GetMethod("SelectDisplayedAccount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var (originalLayout, originalPlacement) = (preferences.DetailedTaskbar, preferences.TaskbarPlacement);
        widget.Show();
        try
        {
            var tps = (TextBlock)widget.FindName("StripTps");
            var rate = (TextBlock)widget.FindName("StripPace");
            var sidecar = (Border)widget.FindName("StripMetrics");
            preferences.TaskbarPlacement = "left";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            do
            {
                widget.Align();
                if (widget.DockVisible) break;
                await Task.Delay(100);
            }
            while (DateTimeOffset.UtcNow < deadline);
            Check(widget.DockVisible, "taskbar widget docks for the rates smoke check");
            foreach (var (layout, detailed) in new[] { ("ring", false), ("bar", true) })
            {
                preferences.DetailedTaskbar = detailed;
                select.Invoke(widget, [groups[0]]);
                widget.Align(); await Settle(widget);
                Check(sidecar.IsVisible && tps.IsVisible && tps.Text == "TPS 12.5" && rate.IsVisible && rate.Text == "↓ 1 pp/h", $"docked {layout} shows both rates");
                CheckInside(tps, widget, "docked TPS fits the taskbar window");
                CheckInside(rate, widget, "docked quota pace fits the taskbar window");
                select.Invoke(widget, [groups[1]]); await Settle(widget);
                Check(tps.Text == "TPS 12.5" && rate.Text == "↓ 2.5 pp/h", "quota pace follows the selected account, unlike local TPS");
                if (output is not null) Capture(widget, output, "pinned-rate-" + layout + ".png");
            }
            token.IsChecked = false; await Settle(widget);
            Check(!tps.IsVisible && rate.IsVisible, "pinned TPS can be hidden independently");
            pace.IsChecked = false; widget.Align(); await Settle(widget);
            Check(widget.DockVisible && !sidecar.IsVisible, "both switches off restore the original docked widget");
            Console.WriteLine("PASS pinned taskbar TPS and selected-account pace in both native layouts");
        }
        finally
        {
            (preferences.DetailedTaskbar, preferences.TaskbarPlacement) = (originalLayout, originalPlacement);
        }
    }

    private static void Capture(Window window, string folder, string filename)
    {
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, filename)); png.Save(stream);
    }

    private static async Task CheckLayouts(GuideWindow guide, string[] args)
    {
        foreach (var size in new[] { new Size(820, 560), new Size(480, 560), new Size(360, 560), new Size(600, 360), new Size(300, 560) })
        {
            var width = size.Width;
            guide.Width = width; guide.Height = size.Height;
            for (var page = 0; page < 4; page++)
            {
                guide.ShowPage(page); await Settle(guide);
                var surface = (Border)guide.Content;
                Check(surface.CornerRadius == new CornerRadius(8) && surface.Clip is RectangleGeometry { RadiusX: 8, RadiusY: 8 } clip && Math.Abs(clip.Rect.Width - surface.ActualWidth) < 1, "guide corners and content clip stay consistent on every page and size");
                var navigation = Descendants<RadioButton>(guide).Where(button => button.GroupName == "guide-chapters").ToArray();
                Check(navigation.Length == 4 && navigation.All(button => AutomationProperties.GetName(button).Length > 0), "chapter navigation names");
                var close = Descendants<Button>(guide).Single(button => Equals(button.ToolTip, L.T("关闭")));
                Check(AutomationProperties.GetName(close) == L.T("关闭") && close.Padding == new Thickness(0), "guide close icon has a localized accessible name and centered content");
                var navGrid = (UniformGrid)VisualTreeHelper.GetParent(navigation[0]);
                Check(navGrid.Columns == (width < 700 && !(size.Height < 480 && width >= 540) ? 2 : 4), "responsive chapter columns");
                foreach (var button in navigation) CheckInside(button, guide, "chapter bounds");
                var next = Descendants<Button>(guide).Single(button => Equals(button.Content, page == 3 ? "Finish" : "Next"));
                var skip = Descendants<Button>(guide).Single(button => Equals(button.Content, "Set up later"));
                CheckInside(next, guide, "next button bounds"); CheckInside(skip, guide, "skip button bounds");
                Check(!Bounds(next, guide).IntersectsWith(Bounds(skip, guide)), "footer buttons overlap");
                var scroll = Descendants<ScrollViewer>(guide).Single(viewer => viewer.Content is StackPanel);
                Check(scroll.ViewportHeight > 100 && scroll.ExtentWidth <= scroll.ViewportWidth + 1, "content remains accessible");
                if (page == 0 && width < 700)
                {
                    var ring = Descendants<QuotaRing>(guide).Single(); var bar = Descendants<ProgressBar>(guide).First();
                    Check(Bounds(bar, guide).Top > Bounds(ring, guide).Bottom, "quota preview stacks on narrow screens");
                }
                if (page == 2)
                {
                    if (width < 700 && size.Height >= 480) Check(Bounds(scroll, guide).Contains(Bounds(Descendants<StatusLamp>(guide).Single(), guide)), "status preview visible on narrow screens");
                    await CheckStates(guide);
                }
                Console.WriteLine($"PASS guide chapter {page + 1} at {width} × {size.Height} DIP");
            }
        }
        guide.Width = 480; guide.ShowPage(1); await Settle(guide);
        var import = Descendants<Button>(guide).Single(button => AutomationProperties.GetName(button) == "Import from CC Switch");
        import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(guide);
        Check(Descendants<TextBlock>(guide).Any(text => text.Text.Contains("demo mode")), "demo connection feedback");
        Descendants<Button>(guide).Single(button => Equals(button.Content, "中文")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(guide);
        Check(Descendants<TextBlock>(guide).Any(text => text.Text.Contains("演示模式")), "connection feedback switches language");
        Check(Descendants<RadioButton>(guide).Any(button => AutomationProperties.GetName(button) == "连接账号"), "navigation switches language");
        Console.WriteLine("PASS guide connection feedback and navigation language");
        await SimplifiedModeSmoke.Run((MainWindow)Application.Current.MainWindow, args);
        await NativeWindowSmoke.Run((MainWindow)Application.Current.MainWindow, args);
        await CheckLampLifetime();
        guide.ShowPage(2); await Settle(guide);
        Descendants<CheckBox>(guide).Single(box => Equals(box.Content, "自动轮播演示")).IsChecked = true;
        var preview = Descendants<StatusPreviewPanel>(guide).Single();
        var playback = (DispatcherTimer)typeof(StatusPreviewPanel).GetField("Playback", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
        guide.WindowState = WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!playback.IsEnabled, "minimized guide pauses automatic playback");
        guide.WindowState = WindowState.Normal; await Settle(guide);
        Check(playback.IsEnabled == SystemParameters.ClientAreaAnimation, "restored guide resumes allowed playback");
        Console.WriteLine("PASS minimized guide pauses and restores automatic playback");
        var outputIndex = Array.IndexOf(args, "--preview-dir");
        if (outputIndex >= 0 && args.Length > outputIndex + 1)
        {
            Directory.CreateDirectory(args[outputIndex + 1]);
            guide.ShowPage(2); await Settle(guide); await Task.Delay(300);
            var bitmap = new RenderTargetBitmap((int)guide.ActualWidth, (int)guide.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(guide); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(args[outputIndex + 1], "guide-narrow.png")); encoder.Save(stream);
        }
        var activeLamp = Descendants<StatusLamp>(guide).Single();
        guide.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(!activeLamp.HasAnimatedProperties, "closed guide stops lamp animation");
        Console.WriteLine("PASS guide closes without a running lamp animation");
    }

    private static async Task CheckStates(GuideWindow guide)
    {
        var expected = new[] { CodexSignal.Working, CodexSignal.Completed, CodexSignal.NeedsInput, CodexSignal.Low, CodexSignal.Exhausted, CodexSignal.Error, CodexSignal.Paused, CodexSignal.Offline };
        var buttons = Descendants<RadioButton>(guide).Where(button => button.GroupName == "guide-status").ToArray();
        Check(buttons.Length == expected.Length, "all status examples available");
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            buttons[i].IsChecked = true; await Settle(guide);
            var lamp = Descendants<StatusLamp>(guide).Single();
            Check(lamp.Status?.Signal == expected[i], "guide state matches runtime signal");
            Check(ToolTipService.GetIsEnabled(lamp) && lamp.ToolTip is ToolTip { Content: TextBlock tipText } && tipText.Text == lamp.Status!.Tooltip, "every status example exposes its current hover description");
            Check(Descendants<TextBlock>(guide).Any(text => text.Text.EndsWith(" · breathing")) == lamp.Status!.Breathing, "guide pattern describes the actual lamp animation");
            Check(Descendants<CheckBox>(guide).Single(box => Equals(box.Content, "Play states automatically")).IsChecked == false, "manual selection pauses playback");
            Check(Descendants<TextBlock>(guide).Any(text => text.Text.Contains("Sample prompt")) == (i == 2), "input prompt visibility");
        }
    }

    private static async Task CheckCancel(App app, GuideWindow guide, AccountStore store)
    {
        var main = (MainWindow)app.MainWindow;
        var startupField = typeof(MainWindow).GetField("startupRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Task?)startupField.GetValue(main);
        if (original is not null) await original;
        NativeWindowSmoke.CheckNotificationRecovery(main, store);
        AccountFlowSmoke.CheckReloginCommit(main, store);
        await AccountFlowSmoke.CheckLoginDialogs(main);
        await AccountFlowSmoke.CheckImportNotices(main, store);
        AccountFlowSmoke.CheckRemovalDuringRefresh(main, store);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startupField.SetValue(main, pending.Task);
        try
        {
            guide.ShowPage(1); await Settle(guide);
            var connect = typeof(GuideWindow).GetMethod("ConnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var operation = (Task)connect.Invoke(guide, [false])!;
            await Settle(guide);
            Check(!Descendants<Button>(guide).Single(button => Equals(button.Content, "Next")).IsEnabled, "navigation waits for connection readiness");
            Check(Descendants<Button>(guide).Single(button => Equals(button.Content, "Set up later")).IsEnabled, "pending connection can be dismissed");
            guide.Dismiss(); await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!guide.IsVisible && !app.Windows.OfType<GuideWindow>().Any(), "dismissed guide stays closed");
            Check(!app.Windows.OfType<LoginDialog>().Any(), "dismissed preparation does not open login");
            Check(!pending.Task.IsCompleted, "dismissal preserves independent quota sync");
            Check(store.LoadSettings().GuideVersion == 1, "guide dismissal persists");
            Console.WriteLine("PASS pending startup sync can be dismissed without starting login");
        }
        finally { pending.TrySetResult(); startupField.SetValue(main, original); }
    }

    private static async Task CheckLampLifetime()
    {
        var status = new CodexStatusViewModel(); status.Update(new(CodexActivityKind.Working));
        var lamp = new StatusLamp { Status = status };
        var panel = new StackPanel(); panel.Children.Add(lamp);
        var host = new Window { Width = 80, Height = 80, Content = panel, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            host.Show(); await Settle(host);
            Check(lamp.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "visible lamps respect system animation preference");
            panel.Children.Clear(); await Settle(host);
            Check(!lamp.HasAnimatedProperties, "detached lamps stop their animation clock");
            panel.Children.Add(lamp); await Settle(host);
            Check(lamp.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "reattached lamps restore their animation");
            var language = L.Language;
            L.Apply("en"); status.Update(new(CodexActivityKind.Working));
            Check(lamp.ToolTip is ToolTip { Content: TextBlock text } && text.Text == status.Message && AutomationProperties.GetName(lamp) == status.Message, "unchanged signals refresh localized tooltips");
            L.Apply(language); status.Update(new(CodexActivityKind.Working));
            host.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!lamp.HasAnimatedProperties, "closed hosts stop lamp animation");
            Console.WriteLine("PASS lamp animation follows load unload and system preferences");
        }
        finally { host.Close(); }
    }

    private static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static void CheckInside(FrameworkElement element, Window window, string message)
    {
        var bounds = Bounds(element, window);
        Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
