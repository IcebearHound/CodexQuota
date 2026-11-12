using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        var originalLayout = settings.DetailedTaskbar;
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
            Console.WriteLine("PASS prepared native flyout reuse, both taskbar layouts, click latency and sampling resource lifetime");
        }
        finally { settings.DetailedTaskbar = originalLayout; widget.Close(); }
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
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
