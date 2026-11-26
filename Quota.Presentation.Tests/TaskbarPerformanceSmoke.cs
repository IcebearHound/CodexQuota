using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class TaskbarPerformanceSmoke
{
    public static async Task Run(MainWindow main)
    {
        Application.Current.Windows.OfType<GuideWindow>().Single().Dismiss();
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var hostType = typeof(MainWindow).Assembly.GetType("Quota.Desktop.TaskbarWindowHost", true)!;
        var panelField = typeof(TaskbarWidget).GetField("flyout", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var close = typeof(TaskbarWidget).GetMethod("CloseAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var (originalLayout, originalPlacement) = (settings.DetailedTaskbar, settings.TaskbarPlacement);
        settings.TaskbarPlacement = "right";
        var widget = new TaskbarWidget(main, groups, null);
        try
        {
            widget.Show();
            await WaitForDock(widget);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var panel = (TaskbarFlyout?)panelField.GetValue(widget) ?? throw new InvalidOperationException("Idle preparation did not create a flyout.");
            Check(!panel.IsVisible && !IsWindowVisible(new WindowInteropHelper(panel).Handle), "idle preparation leaves its native flyout hidden");
            var host = typeof(TaskbarWidget).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;
            var bounds = (PixelRect)hostType.GetProperty("Bounds")!.GetValue(host)!;
            CheckTaskbarZOrder(widget, bounds);
            var sample = hostType.GetMethod("SampleBackground", BindingFlags.Static | BindingFlags.NonPublic)!;
            var watch = new Stopwatch();
            var samples = new List<double>();
            using var process = Process.GetCurrentProcess();
            var graphicsBefore = GetGuiResources(process.Handle, 0);
            for (var index = 0; index < 30; index++)
            {
                watch.Restart();
                Check(sample.Invoke(null, [bounds]) is RgbColor, "batched screen sampling produces a color");
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            Check(GetGuiResources(process.Handle, 0) <= graphicsBefore + 2, "repeated sampling releases its GDI resources");
            var median = samples.Order().ElementAt(samples.Count / 2);
            Check(median < 50, "batched gutter sampling avoids per-pixel desktop synchronization stalls");
            Console.WriteLine($"MEASURE screen sampling median {median:0.0} ms");
            var gaps = new List<double>();
            var opens = new List<double>();
            var last = Stopwatch.GetTimestamp();
            var pulse = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            pulse.Tick += (_, _) => { var now = Stopwatch.GetTimestamp(); gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now; };
            pulse.Start();
            try
            {
                foreach (var statusBar in new[] { false, true })
                {
                    settings.DetailedTaskbar = statusBar;
                    for (var index = 0; index < 3; index++)
                    {
                        await WaitForDock(widget);
                        watch.Restart(); widget.ShowAccounts(); opens.Add(watch.Elapsed.TotalMilliseconds);
                        Check(ReferenceEquals(panel, panelField.GetValue(widget)) && panel.IsVisible, "clicks reuse the prepared flyout in both taskbar layouts");
                        await Task.Delay(300);
                        close.Invoke(widget, null); await Task.Delay(220);
                        Check(!panel.IsVisible, "each repeated click cycle finishes its hide animation");
                    }
                }
            }
            finally { pulse.Stop(); }
            Check(opens.Max() < 100, "prepared click handlers complete within the responsiveness regression budget");
            Console.WriteLine($"MEASURE open handlers {string.Join(", ", opens.Select(value => value.ToString("0.0")))} ms");
            Console.WriteLine($"MEASURE dispatcher largest gap {gaps.Max():0.0} ms");
            var menu = (ContextMenu)typeof(MainWindow).GetMethod("CreateWidgetMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null)!;
            menu.PlacementTarget = widget; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try
            {
                var captions = menu.Items.OfType<MenuItem>().Select(item => ((Grid)item.Header).Children.OfType<TextBlock>().Single()).ToArray();
                Console.WriteLine("MEASURE taskbar menu font " + captions[0].FontFamily.Source);
                Check(captions.All(caption => caption.FontFamily.Source == "Microsoft YaHei UI, Segoe UI"), "taskbar popup captions use the common Chinese UI font");
                var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                Check(typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.CharacterToGlyphMap.ContainsKey('额'), "the menu UI typeface contains Chinese glyphs");
            }
            finally { menu.IsOpen = false; }
            Console.WriteLine("PASS prepared native flyout reuse, both taskbar layouts, click latency, sampling resource lifetime and popup fonts");
        }
        finally { (settings.DetailedTaskbar, settings.TaskbarPlacement) = (originalLayout, originalPlacement); widget.Close(); }
    }

    private static void CheckTaskbarZOrder(TaskbarWidget widget, PixelRect bounds)
    {
        const uint noActivate = 0x0010, noOwnerZOrder = 0x0200, keepBounds = 0x0001 | 0x0002;
        var handle = new WindowInteropHelper(widget).Handle;
        var taskbar = GetWindow(handle, 4); // GW_OWNER
        var center = new NativePoint { X = (int)bounds.CenterX, Y = (int)bounds.CenterY };
        Check(taskbar != 0 && WindowFromPoint(center) == handle, "widget begins on top of its taskbar owner");

        var overlay = new Window
        {
            Width = 40, Height = 40, Left = -10000, Top = -10000,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Black
        };
        try
        {
            overlay.Show();
            var overlayHandle = new WindowInteropHelper(overlay).Handle;
            Check(SetWindowPos(overlayHandle, -1, center.X - 20, center.Y - 20, 40, 40, noActivate), "fixture covers the widget with another window");
            Check(WindowFromPoint(center) == overlayHandle, "fixture overlay is above the widget");
            widget.Align();
            Check(WindowFromPoint(center) == overlayHandle, "widget does not raise over an unrelated overlay");
        }
        finally { overlay.Close(); }

        var foreground = GetForegroundWindow();
        Check(SetWindowPos(handle, taskbar, 0, 0, 0, 0, keepBounds | noActivate | noOwnerZOrder), "fixture puts the widget behind Explorer");
        Check(IsWindowVisible(handle) && GetAncestor(WindowFromPoint(center), 2) == taskbar, "Explorer covers the widget while it still reports visible");
        widget.Align();
        Check(WindowFromPoint(center) == handle, "alignment restores the covered widget without clicking the taskbar");
        Check(GetForegroundWindow() == foreground, "restoring the widget does not steal foreground focus");
        Console.WriteLine("PASS native taskbar z-order recovery without input or focus changes");
    }

    private static async Task WaitForDock(TaskbarWidget widget)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(12))
        {
            widget.Align();
            if (widget.DockVisible) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Performance fixture requires a visible taskbar widget.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relationship);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint relationship);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
