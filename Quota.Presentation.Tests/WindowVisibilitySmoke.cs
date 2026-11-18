using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class WindowVisibilitySmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        Application.Current.Windows.OfType<GuideWindow>().Single().Hide();
        var handle = new WindowInteropHelper(main).Handle;
        await Settle();
        if (args.Contains("--background"))
        {
            Check(!main.IsVisible && !IsWindowVisible(handle), $"background startup keeps the WPF content and native glass window hidden (WPF {main.Visibility}/{main.IsVisible}, native {IsWindowVisible(handle)})");
            Method("ShowFromTray").Invoke(main, null); await Settle();
            Check(main.IsVisible && IsWindowVisible(handle) && ((FrameworkElement)main.FindName("FullLayout")).IsVisible,
                "opening the background-started app restores the main window and its content");
        }

        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        settings.Topmost = true;
        foreach (var material in new[] { "glass", "solid", "glass" })
        {
            settings.Material = material;
            Method("ApplySettings").Invoke(main, null);
            main.Show(); await Settle();
            Check(main.IsVisible && IsWindowVisible(handle), $"showing the main window reveals its content and native surface together ({main.Visibility}, native {IsWindowVisible(handle)})");
            var glass = (bool)typeof(MainWindow).Assembly.GetType("Quota.Desktop.WindowSurface", true)!.GetMethod("IsGlassEnabled", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [main])!;
            Check(glass == (material == "glass"), "restoring a hidden window applies the selected material");
            Check(((FrameworkElement)main.FindName("FullLayout")).IsVisible && ((ItemsControl)main.FindName("AccountList")).HasItems,
                "restored panels contain the account controls as well as their native background");

            Method("MinimizeWindow").Invoke(main, [main, new RoutedEventArgs()]); await Settle();
            Check(!main.IsVisible && !IsWindowVisible(handle), "minimize-to-tray removes the entire native surface");
            Method("ApplySettings").Invoke(main, null); await Settle();
            Check(!main.IsVisible && !IsWindowVisible(handle), "changing settings cannot expose an already hidden panel");

            Method("ShowFromTray").Invoke(main, null); await Settle();
            Check(main.IsVisible && IsWindowVisible(handle), "tray restore reopens a complete main window");
            settings.CloseToTray = true; main.Close(); await Settle();
            Check(!main.IsVisible && !IsWindowVisible(handle), "closing to tray hides the native glass layer");

            // Hide after a material update has queued its deferred native work.
            main.Show(); Method("ApplySettings").Invoke(main, null); main.Hide(); await Settle();
            Check(!main.IsVisible && !IsWindowVisible(handle), "deferred material work cannot resurrect a hidden panel");
        }
        main.Show(); await Settle();
        Console.WriteLine("PASS background startup minimize close-to-tray restore and queued-material visibility");
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(250); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
