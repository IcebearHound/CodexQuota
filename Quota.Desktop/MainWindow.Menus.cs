using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Quota.Desktop;

public partial class MainWindow
{
    private ContextMenu? trayMenu;
    private Window? trayMenuAnchor;

    internal ContextMenu CreateWidgetMenu()
    {
        var menu = new ContextMenu();
        void Item(string label, string icon, Action action, bool? selected = null)
        {
            var header = SettingsControls.Label(label, icon);
            if (selected.HasValue)
            {
                header.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
                var check = new AppIcon { Kind = "check", Width = 14, Height = 14, Visibility = selected.Value ? Visibility.Visible : Visibility.Hidden };
                check.SetResourceReference(AppIcon.ForegroundProperty, "Accent");
                Grid.SetColumn(check, 2); header.Children.Add(check);
            }
            var item = new MenuItem { Header = header, IsCheckable = selected.HasValue, IsChecked = selected == true, Padding = new Thickness(12, 10, 12, 10), MinWidth = 210 };
            AutomationProperties.SetName(item, label);
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Item(L.T("打开额度监控"), "monitor", ShowFromTray);
        Item(L.F("异常会话", "Session issues"), "activity", ShowSessionIssues);
        Item(L.T("刷新额度"), "refresh-cw", async () => await RefreshAllAsync());
        Item(L.F("任务栏 · 圆环模式", "Taskbar · Ring"), "rotate-cw", () => SetTaskbarLayout(false), !settings.DetailedTaskbar);
        Item(L.F("任务栏 · 状态条模式", "Taskbar · Status bar"), "activity", () => SetTaskbarLayout(true), settings.DetailedTaskbar);
        Item(L.T("设置"), "settings-2", OpenTaskbarSettings);
        var separator = new Separator { Margin = new Thickness(8, 4, 8, 4) };
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(Border.BackgroundProperty, new System.Windows.DynamicResourceExtension("BorderBrush"));
        separator.Template = new ControlTemplate(typeof(Separator)) { VisualTree = line };
        separator.SetResourceReference(Control.BackgroundProperty, "BorderBrush"); separator.Height = 1;
        menu.Items.Add(separator);
        Item(L.T("退出"), "x", Exit);
        return menu;
    }

    private void ShowTrayMenu()
    {
        if (exiting) return;
        trayMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var anchor = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        trayMenuAnchor = anchor;
        anchor.Show();
        var handle = new WindowInteropHelper(anchor).Handle;
        var point = System.Windows.Forms.Cursor.Position;
        SetMenuWindowPos(handle, -1, point.X, point.Y, 1, 1, 0x0010);
        SetForegroundWindow(handle);
        var menu = CreateWidgetMenu(); trayMenu = menu;
        menu.PlacementTarget = anchor; menu.Placement = PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            anchor.Close();
            if (ReferenceEquals(trayMenu, menu)) { trayMenu = null; trayMenuAnchor = null; }
        };
        menu.IsOpen = true;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SetMenuWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
