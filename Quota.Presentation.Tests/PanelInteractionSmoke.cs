using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Quota.Desktop;
using Quota.Core;

internal static class PanelInteractionSmoke
{
    public static async Task Run(MainWindow main)
    {
        var menuFactory = typeof(MainWindow).GetMethod("CreateWidgetMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var store = (AccountStore)typeof(MainWindow).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var previousTaskbar = preferences.DetailedTaskbar;
        var previousMainLayout = preferences.SimplifiedLayout;
        foreach (var language in new[] { "zh-CN", "en" })
        {
            L.Apply(language);
            var menu = (ContextMenu)menuFactory.Invoke(main, null)!;
            var entries = menu.Items.OfType<MenuItem>().ToArray();
            Check(entries.Length == 7 && entries.All(entry => entry.Header is Grid), "taskbar and tray menu entries share icons and labels");
            Check(entries.Any(entry => AutomationProperties.GetName(entry) == L.F("异常会话", "Session issues")), "both menus expose the localized issue list");
            menu.PlacementTarget = main; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(menu.Background.ToString() == Application.Current.FindResource("Surface").ToString(), "menu uses the application surface");
            menu.IsOpen = false;
            foreach (var statusBar in new[] { false, true })
            {
                var label = statusBar ? L.F("任务栏 · 状态条模式", "Taskbar · Status bar") : L.F("任务栏 · 圆环模式", "Taskbar · Ring");
                var choice = entries.Single(item => AutomationProperties.GetName(item) == label);
                Check(choice.IsCheckable, "taskbar layout entries expose their selected state");
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(preferences.DetailedTaskbar == statusBar && store.LoadSettings().DetailedTaskbar == statusBar && preferences.SimplifiedLayout == previousMainLayout,
                    "direct taskbar layout choices persist independently of the main compact layout");
                var refreshed = (ContextMenu)menuFactory.Invoke(main, null)!;
                Check(refreshed.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetName(item) == label).IsChecked,
                    "fresh menus mark the active taskbar layout");
            }
        }
        typeof(MainWindow).GetMethod("SetTaskbarLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [previousTaskbar]);
        L.Apply("zh-CN");
        typeof(MainWindow).GetMethod("ShowTrayMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var trayMenu = (ContextMenu)typeof(MainWindow).GetField("trayMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        Check(trayMenu.IsOpen && trayMenu.Items.OfType<MenuItem>().Any(item => AutomationProperties.GetName(item) == "异常会话"), "notification-area menu opens with the same issue action");
        trayMenu.IsOpen = false; await Task.Delay(250); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(typeof(MainWindow).GetField("trayMenuAnchor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) is null, "notification-area menu releases its focus anchor");
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var widget = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(widget, main, groups);
        try
        {
            flyout.Left = SystemParameters.WorkArea.Left + 30; flyout.Top = SystemParameters.WorkArea.Top + 30; flyout.Show();
            typeof(TaskbarWidget).GetField("flyout", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(widget, flyout);
            var toggle = typeof(TaskbarWidget).GetMethod("ToggleFlyout", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toggle.Invoke(widget, [widget, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }]);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check((bool)typeof(TaskbarWidget).GetProperty("FlyoutPinned", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(widget)!, "click converts a hover panel into a pinned panel");
            typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flyout, [EventArgs.Empty]);
            typeof(TaskbarWidget).GetMethod("CloseFlyoutLater", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, null);
            var timer = (DispatcherTimer)typeof(TaskbarWidget).GetField("closeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;
            Check(flyout.IsVisible && !timer.IsEnabled, "pinned panel survives deactivation and mouse leave");
            toggle.Invoke(widget, [widget, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }]);
            await Task.Delay(220);
            Check(!flyout.IsVisible && !(bool)typeof(TaskbarWidget).GetProperty("FlyoutPinned", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(widget)!, "second click closes the pinned panel");
            flyout.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CheckSurface(main); CheckSurface(flyout);
            var dialog = new SessionIssuesWindow(main, _ => { }, (_, _) => { }, () => { });
            try
            {
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); CheckSurface(dialog);
                dialog.Width = 340; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); dialog.UpdateLayout(); CheckSurface(dialog);
            }
            finally { dialog.Close(); }
        }
        finally { flyout.Close(); widget.Close(); }
        Console.WriteLine("PASS themed icon menus localized issue entry click-pinned panels and matching native corners");
    }

    private static void CheckSurface(Window window)
    {
        window.UpdateLayout();
        Check(WindowChrome.GetWindowChrome(window).CornerRadius == new CornerRadius(0), "native compositor owns rounding");
        var border = (Border)window.Content;
        Check(border.Clip is RectangleGeometry { RadiusX: 8, RadiusY: 8 } clip && Math.Abs(clip.Rect.Width - border.ActualWidth) < 1, "content clip follows the outer border after resizing");
        if (DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 33, out var preference, sizeof(int)) >= 0)
            Check(preference == 2, "DWM uses the same rounded window preference");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
