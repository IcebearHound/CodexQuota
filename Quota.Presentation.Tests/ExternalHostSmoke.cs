using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class ExternalHostSmoke
{
    public static void RunParent()
    {
        var thread = new Thread(() =>
        {
            var app = new Application();
            var window = new Window { Title = "External host fixture", Width = 800, Height = 800, Left = 100, Top = 100, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = Brushes.Black };
            window.SourceInitialized += (_, _) => { Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64()); Console.Out.Flush(); };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }

    public static async Task Run(App app)
    {
        app.Windows.OfType<GuideWindow>().Single().Dismiss();
        var hostType = typeof(MainWindow).Assembly.GetType("Quota.Desktop.DesktopWindowHost", true)!;
        foreach (var show in new[] { true, false })
        {
            var main = (MainWindow)app.MainWindow;
            var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            preferences.DesktopOnly = true; preferences.SimplifiedMode = true;
            typeof(MainWindow).GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
            if (show) main.Show(); else main.Hide();
            using var parent = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--external-parent")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            })!;
            try
            {
                var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var handle = (nint)long.Parse(line!);
                await Task.Delay(100);
                var host = typeof(MainWindow).GetField("desktopHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                Check((bool)hostType.GetMethod("Apply")!.Invoke(host, [main, false])!, "external fixture detaches its main window");
                Check((bool)hostType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [main, handle, Bounds(main)])!, "external parent attachment succeeds");
                parent.Kill(true); await parent.WaitForExitAsync();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                while (!Recovered(app, show, handle) && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
                if (!Recovered(app, show, handle)) Console.WriteLine("EXTERNAL_FAILURE " + JsonSerializer.Serialize(app.Windows.OfType<Window>().Select(window => new { window.Title, window.Visibility, window.IsLoaded, NativeVisible = IsWindowVisible(new WindowInteropHelper(window).Handle), Parent = GetParent(new WindowInteropHelper(window).Handle).ToInt64() })));
                Check(Recovered(app, show, handle), "abrupt parent termination restores the intended main visibility");
                Check(app.Windows.OfType<MainWindow>().Count() == 1, "external recovery leaves one main window");
                Check(app.MainWindow != main, "a destroyed main window is replaced");
                foreach (var field in new[] { "timer", "activityTimer", "taskbarMonitor" })
                        Check(!((DispatcherTimer)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).IsEnabled,
                            "replaced main stops its polling timers");
                Console.WriteLine(show ? "PASS abrupt external host termination restores visible desktop windows" : "PASS abrupt external host termination preserves the tray-hidden main");
            }
            finally { if (!parent.HasExited) { parent.Kill(true); await parent.WaitForExitAsync(); } }
        }
    }

    private static bool Recovered(App app, bool showMain, nint previousParent)
    {
        if (app.MainWindow is not MainWindow { IsLoaded: true } main) return false;
        var handle = new WindowInteropHelper(main).Handle;
        var parent = GetParent(handle);
        if (!IsWindow(handle) || IsWindowVisible(handle) != showMain || parent == 0 || parent == previousParent || !IsWindow(parent)) return false;
        return main.ActualWidth == 252;
    }
    private static PixelRect Bounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
