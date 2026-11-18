using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Runtime.InteropServices;
using Quota.Core;

namespace Quota.Desktop;

public partial class MainWindow
{
    private bool? appliedSimplifiedMode;
    private void PanelPointerDown(object sender, MouseButtonEventArgs args)
    {
        if (args.Handled || args.ClickCount != 2 || !IsPanelBlank(args.OriginalSource as DependencyObject)) return;
        args.Handled = true;
        SetSimplifiedMode(!settings.SimplifiedMode);
    }
    private bool IsPanelBlank(DependencyObject? source)
    {
        while (source is not null && source != this)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb or TextBox or AccountName or StatusLamp
                or TextBlock or QuotaRing or ProgressBar or AppIcon or BrandIcon) return false;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return source == this;
    }
    private void SnapSimplifiedPanel()
    {
        if (settings.SimplifiedMode) WindowPlacementGuard.SnapToEdges(this);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MovingRectangle { public int Left, Top, Right, Bottom; }
    private bool SnapMovingPanel(nint address)
    {
        if (!settings.SimplifiedMode || address == 0) return false;
        var rect = Marshal.PtrToStructure<MovingRectangle>(address);
        var width = (long)rect.Right - rect.Left; var height = (long)rect.Bottom - rect.Top;
        if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue) return false;
        var current = new PixelRect(rect.Left, rect.Top, (int)width, (int)height);
        var snapped = WindowPlacementGuard.SnapBounds(this, current);
        if (snapped == current) return false;
        rect.Left = snapped.Left; rect.Top = snapped.Top; rect.Right = (int)snapped.Right; rect.Bottom = (int)snapped.Bottom;
        Marshal.StructureToPtr(rect, address, false);
        return true;
    }
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
            ResizeSimplifiedPanel();
        }
        else if (appliedSimplifiedMode == true)
        {
            Width = settings.Width; Height = settings.Height;
        }
        appliedSimplifiedMode = simplified;
    }

    private void ResizeSimplifiedPanel()
    {
        var rowHeight = settings.SimplifiedLayout == "bar" ? 46 : 72;
        Height = Math.Min(Math.Max(84, 44 + Math.Max(1, visibleAccounts.Count) * rowHeight), Math.Min(450, SystemParameters.WorkArea.Height - 24));
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
