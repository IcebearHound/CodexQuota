using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Quota.Core;
using Quota.Desktop;

internal static class SimplifiedModeSmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var store = (AccountStore)Field("store").GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var sources = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var mode = Method("SetSimplifiedMode"); var layout = Method("SetSimplifiedLayout");
        var originalMode = settings.SimplifiedMode; var originalLayout = settings.SimplifiedLayout;
        var originalTaskbar = settings.DetailedTaskbar;
        mode.Invoke(main, [false]);
        await Settle(main);
        var width = main.ActualWidth; var height = main.ActualHeight;
        var added = new List<AccountViewModel>();
        var widget = new TaskbarWidget(main, groups, null);
        try
        {
            ((Button)main.FindName("SimplifiedModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(main);
            Check(settings.SimplifiedMode && main.ActualWidth == 252 && main.ActualHeight < height, "main action enters a smaller compact window");
            var full = (Grid)main.FindName("FullLayout");
            var compact = (ScrollViewer)main.FindName("SimplifiedPanel");
            var items = (ItemsControl)main.FindName("SimplifiedAccounts");
            Check(!full.IsVisible && compact.IsVisible && !Descendants<Button>(compact).Any(), "compact surface hides every operation button");
            Check(items.Items.Count == groups.Count, "compact surface shows every account group");
            var lamp = (StatusLamp)main.FindName("SimplifiedRuntimeLamp");
            Check(lamp.IsVisible && ReferenceEquals(lamp.Status, main.CodexStatus), "compact header exposes the shared live status lamp");
            foreach (var kind in new[] { CodexActivityKind.Working, CodexActivityKind.NeedsInput, CodexActivityKind.Error, CodexActivityKind.Paused })
            {
                main.CodexStatus.Update(new(kind, kind == CodexActivityKind.Working ? 1 : 0, kind == CodexActivityKind.NeedsInput ? 1 : 0));
                await Settle(main);
                Check(lamp.Status!.Signal == main.CodexStatus.Signal && ((ToolTip)lamp.ToolTip).Content is TextBlock text && text.Text == main.CodexStatus.Tooltip,
                    "compact lamp shares runtime state, animation and tooltip");
            }
            var issue = new CodexSessionIssue("compact-issue", Guid.NewGuid().ToString("D"), CodexActivityKind.Paused, DateTimeOffset.UtcNow, "demo", Title: "Compact interruption");
            var originalActivity = Field("activity").GetValue(main);
            Field("activity").SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Paused, SessionIssues: [issue]));
            Method("UpdateCodexStatus").Invoke(main, null);
            var lampClick = DoubleClick(lamp); await Settle(main);
            Check(settings.SimplifiedMode, "double-clicking the lamp never toggles the panel layout");
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(lamp)!.GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(main);
            var issues = Application.Current.Windows.OfType<SessionIssuesWindow>().Single();
            Check(issues.IsVisible, "compact lamp opens the live exception list"); issues.Close();
            Field("activity").SetValue(main, originalActivity); Method("UpdateCodexStatus").Invoke(main, null);
            settings.DetailedTaskbar = false;
            foreach (var style in new[] { "ring", "bar" })
            {
                layout.Invoke(main, [style]); await Settle(main);
                Check(settings.SimplifiedLayout == style && !settings.DetailedTaskbar, "main layout keeps taskbar choice independent");
                Check(style == "ring" ? Descendants<QuotaRing>(compact).Count() == groups.Count : Descendants<ProgressBar>(compact).Count() == groups.Count,
                    "each compact account uses the chosen renderer");
                Check(!Descendants<Button>(compact).Any() && main.ActualWidth == 252, "both layouts remain button-free and narrow");
                Check(lamp.IsVisible, "both compact layouts retain their status light");
                var name = Descendants<TextBlock>(compact).First(text => text.Text == groups[0].Name);
                DoubleClick(name); await Settle(main);
                Check(settings.SimplifiedMode, "account text is not a blank-space layout shortcut");
                Method("SavePlacement").Invoke(main, null);
                var saved = store.LoadSettings();
                Check(saved.SimplifiedMode && saved.SimplifiedLayout == style && saved.Width == width && saved.Height == height,
                    "saving compact placement preserves full dimensions and startup mode");
                if (style == "bar")
                {
                    var reset = Descendants<TextBlock>(compact).First(text => text.Name == "InlineReset");
                    var percent = Descendants<TextBlock>(compact).First(text => text.Name == "InlinePercent");
                    Check(Math.Abs(reset.TranslatePoint(new Point(), compact).Y - percent.TranslatePoint(new Point(), compact).Y) < 4 && reset.TranslatePoint(new Point(reset.ActualWidth, 0), compact).X < percent.TranslatePoint(new Point(), compact).X,
                        "reset time shares the percentage line to its left");
                    Check(Descendants<AppIcon>(compact).Count(icon => icon.Kind == "rotate-cw") == groups.Count, "every inline reset time has its icon");
                }
                Capture(main, args, "main-compact-" + style + ".png");
            }
            layout.Invoke(main, ["ring"]); settings.DetailedTaskbar = true;
            Method("ApplySettings").Invoke(main, null); await Settle(main);
            Check(settings.SimplifiedLayout == "ring" && settings.DetailedTaskbar, "taskbar status-bar choice leaves main ring selected");
            var update = typeof(TaskbarWidget).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            update.Invoke(widget, null);
            var body = (Border)widget.Content;
            body.Measure(new Size(180, 48)); body.Arrange(new Rect(0, 0, 180, 48)); body.UpdateLayout();
            Check(((Grid)widget.FindName("FullPanel")).Visibility == Visibility.Visible && ((Grid)widget.FindName("CompactPanel")).Visibility == Visibility.Collapsed,
                "taskbar status-bar mode uses its own compact renderer");
            Check(Descendants<ProgressBar>((Grid)widget.FindName("FullPanel")).Count() == 1 && !Descendants<QuotaRing>((Grid)widget.FindName("FullPanel")).Any(),
                "taskbar status-bar mode renders a bar with essential text");
            Capture(body, args, "taskbar-status-bar.png");
            for (var index = 0; index < 12; index++)
            {
                var source = new AccountViewModel(new() { Id = Guid.NewGuid().ToString("N"), Name = "Compact fixture " + index });
                added.Add(source); sources.Add(source);
            }
            await Settle(main);
            Check(main.ActualHeight <= 450 && compact.ExtentHeight > compact.ViewportHeight && items.Items.Count == groups.Count,
                "many accounts stay accessible within a capped scrolling panel");
            compact.ScrollToEnd(); await Settle(main); Check(compact.VerticalOffset > 0, "compact account list scrolls to its last entry");
            foreach (var source in added) sources.Remove(source);
            added.Clear(); await Settle(main);
            await CheckSnapping(main, settings, store);
            var click = DoubleClick(compact); await Settle(main);
            Check(!settings.SimplifiedMode && click.Handled && main.ActualWidth == width && main.ActualHeight == height,
                $"compact preview input restores full mode on double-click (compact {settings.SimplifiedMode}, handled {click.Handled}, size {main.ActualWidth}x{main.ActualHeight}, expected {width}x{height})");
            Check(!lamp.IsVisible && !lamp.HasAnimatedProperties, "hidden compact status lamp stops its animation");
            var settingsButton = (Button)main.FindName("SettingsButton"); DoubleClick(settingsButton);
            Check(!settings.SimplifiedMode, "double-clicking an operation button never changes layout");
            DoubleClick(full); await Settle(main);
            Check(settings.SimplifiedMode && main.ActualWidth == 252, "double-clicking complete-panel blank space enters compact mode");
            Method("SimplifiedMenuOpening").Invoke(main, [compact, null]);
            var restore = compact.ContextMenu.Items.OfType<MenuItem>().First();
            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle(main);
            Check(!settings.SimplifiedMode && full.IsVisible && !compact.IsVisible && main.ActualWidth == width && main.ActualHeight == height,
                "compact context menu restores full layout and original dimensions");
            Check(store.LoadSettings().SimplifiedLayout == "ring" && store.LoadSettings().DetailedTaskbar,
                "independent layout choices persist together");
            Console.WriteLine("PASS compact status lamps and issue access, blank-only bidirectional double-click, edge/corner snapping, independent layouts and scrolling");
        }
        finally
        {
            foreach (var source in added) sources.Remove(source);
            settings.DetailedTaskbar = originalTaskbar;
            layout.Invoke(main, [originalLayout]); mode.Invoke(main, [originalMode]);
            Method("ApplySettings").Invoke(main, null); Method("SaveSettings").Invoke(main, null);
            widget.Close();
        }
    }
    private static MouseButtonEventArgs DoubleClick(UIElement element)
    {
        // PreviewMouseLeftButtonDown is re-raised directly at each input-route node;
        // raise the tunneling MouseDown event to exercise the real window route.
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(click, 2);
        element.RaiseEvent(click); return click;
    }
    private static async Task CheckSnapping(MainWindow main, WidgetSettings settings, AccountStore store)
    {
        var handle = new WindowInteropHelper(main).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = GetDpiForWindow(handle) / 96d; var near = (int)Math.Round(8 * dpi);
        var guard = typeof(MainWindow).Assembly.GetType("Quota.Desktop.WindowPlacementGuard", true)!;
        var setBounds = guard.GetMethod("SetBounds")!;
        var width = (int)Math.Round(main.ActualWidth * dpi); var height = (int)Math.Round(main.ActualHeight * dpi);
        var original = new PixelRect((int)Math.Round(main.Left * dpi), (int)Math.Round(main.Top * dpi), width, height);
        try
        {
            var midX = area.Left + (area.Width - width) / 2; var midY = area.Top + (area.Height - height) / 2;
            foreach (var x in new[] { area.Left, midX, area.Right - width })
                foreach (var y in new[] { area.Top, midY, area.Bottom - height })
                {
                    var rectangle = new MovingRectangle { Left = x + (x == area.Left ? near : x == midX ? 0 : -near), Top = y + (y == area.Top ? near : y == midY ? 0 : -near) };
                    rectangle.Right = rectangle.Left + width; rectangle.Bottom = rectangle.Top + height;
                    var memory = Marshal.AllocHGlobal(Marshal.SizeOf<MovingRectangle>());
                    try
                    {
                        Marshal.StructureToPtr(rectangle, memory, false);
                        SendMessage(handle, 0x0216, 0, memory);
                        var snapped = Marshal.PtrToStructure<MovingRectangle>(memory);
                        Check(snapped.Left == x && snapped.Top == y && snapped.Right - snapped.Left == width && snapped.Bottom - snapped.Top == height,
                            "native moving rectangle snaps each edge/corner and keeps its size");
                    }
                    finally { Marshal.FreeHGlobal(memory); }
                }
            var nearCorner = new PixelRect(area.Right - width - near, area.Bottom - height - near, width, height);
            setBounds.Invoke(null, [main, nearCorner, 0u]); await Settle(main);
            SendMessage(handle, 0x0232, 0, 0); await Settle(main);
            var saved = store.LoadSettings();
            Check(Math.Abs(main.Left * dpi - (area.Right - width)) < 2 && Math.Abs(main.Top * dpi - (area.Bottom - height)) < 2
                && saved.Left == settings.Left && saved.Top == settings.Top,
                "drag completion applies and persists the physical work-area corner");
            setBounds.Invoke(null, [main, nearCorner, 0u]);
            Method("SnapSimplifiedPanel").Invoke(main, null); await Settle(main);
            Check(Math.Abs(main.Left * dpi - (area.Right - width)) < 2, "custom desktop drag uses the same edge snap path");
        }
        finally { setBounds.Invoke(null, [main, original, 0u]); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct MovingRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Window main) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement surface, string[] args, string filename)
    {
        var output = Array.IndexOf(args, "--preview-dir");
        if (output < 0 || args.Length <= output + 1) return;
        var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 2), (int)Math.Ceiling(surface.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(args[output + 1]);
        using var stream = File.Create(Path.Combine(args[output + 1], filename)); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
