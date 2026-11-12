using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quota.Desktop;

public partial class MainWindow
{
    private bool? appliedSimplifiedMode;
    private void SimplifiedPointerDown(object sender, MouseButtonEventArgs args) => PanelDrag(sender, args);
    private void SimplifiedModeClick(object sender, RoutedEventArgs args) => SetSimplifiedMode(true);

    internal void SetSimplifiedMode(bool enabled)
    {
        if (settings.SimplifiedMode == enabled) return;
        SavePlacement();
        settings.SimplifiedMode = enabled;
        ApplySimplifiedLayout();
        WindowPlacementGuard.EnsureVisible(this);
        SavePlacement();
    }

    private void ApplySimplifiedLayout()
    {
        var simplified = settings.SimplifiedMode;
        FullLayout.Visibility = simplified ? Visibility.Collapsed : Visibility.Visible;
        SimplifiedPanel.Visibility = simplified ? Visibility.Visible : Visibility.Collapsed;
        SimplifiedAccounts.ItemTemplate = (DataTemplate)FindResource(settings.SimplifiedLayout == "bar" ? "SimplifiedBarAccount" : "SimplifiedRingAccount");
        SimplifiedPanel.ContextMenu ??= new ContextMenu();
        MinWidth = simplified ? 220 : 360;
        MinHeight = simplified ? 84 : 280;
        ResizeMode = simplified ? ResizeMode.NoResize : ResizeMode.CanResize;
        if (simplified)
        {
            Width = 252;
            var rowHeight = settings.SimplifiedLayout == "bar" ? 46 : 72;
            Height = Math.Min(Math.Max(84, 18 + Math.Max(1, visibleAccounts.Count) * rowHeight), Math.Min(450, SystemParameters.WorkArea.Height - 24));
        }
        else if (appliedSimplifiedMode == true)
        {
            Width = settings.Width; Height = settings.Height;
        }
        appliedSimplifiedMode = simplified;
    }

    private void SimplifiedMenuOpening(object sender, ContextMenuEventArgs args)
    {
        var menu = SimplifiedPanel.ContextMenu;
        menu.Items.Clear();
        void Item(string text, string icon, Action action, bool selected = false)
        {
            var item = new MenuItem { Header = SettingsControls.Label(text, icon), IsCheckable = selected, IsChecked = selected };
            AutomationProperties.SetName(item, text);
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Item(L.T("返回完整界面"), "monitor", () => SetSimplifiedMode(false));
        Item(L.T("圆环模式"), "rotate-cw", () => SetSimplifiedLayout("ring"), settings.SimplifiedLayout == "ring");
        Item(L.T("状态条模式"), "activity", () => SetSimplifiedLayout("bar"), settings.SimplifiedLayout == "bar");
        Item(L.T("设置"), "settings-2", () => SettingsClick(this, new()));
    }

    internal void SetSimplifiedLayout(string layout)
    {
        settings.SimplifiedLayout = layout == "bar" ? "bar" : "ring";
        ApplySimplifiedLayout();
        WindowPlacementGuard.EnsureVisible(this);
        SaveSettings();
    }
}
