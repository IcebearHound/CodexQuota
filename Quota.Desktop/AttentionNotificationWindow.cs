using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Quota.Core;

namespace Quota.Desktop;

public sealed class AttentionNotificationWindow : Window
{
    private readonly TextBlock heading = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 68, Margin = new Thickness(0, 5, 0, 0) };
    private readonly AppIcon icon = new() { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
    private readonly DispatcherTimer dismissTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    internal string NotificationKey { get; private set; } = "";

    public AttentionNotificationWindow()
    {
        Width = 332; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Text");
        message.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        WindowChrome.SetWindowChrome(this, new() { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var row = new Grid { Margin = new Thickness(16) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(icon);
        var text = new StackPanel(); text.Children.Add(heading); text.Children.Add(message);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var close = new Button
        {
            Content = new AppIcon { Kind = "x", Width = 14, Height = 14 },
            Style = (Style)FindResource("IconButton"), Width = 24, Height = 24,
            Padding = new Thickness(0), Margin = new Thickness(10, -3, -4, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        close.ToolTip = L.T("关闭"); AutomationProperties.SetName(close, L.T("关闭"));
        close.Click += (_, _) => Dismiss();
        Grid.SetColumn(close, 2); row.Children.Add(close);
        var body = new Border { BorderThickness = new Thickness(1), Child = row };
        body.SetResourceReference(Border.BackgroundProperty, "Background");
        body.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = body;

        SourceInitialized += (_, _) => ((HwndSource)PresentationSource.FromVisual(this)).AddHook(MouseActivation);
        dismissTimer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => dismissTimer.Stop();
        MouseLeave += (_, _) => { if (IsVisible) dismissTimer.Start(); };
        Closed += (_, _) => dismissTimer.Stop();
    }

    internal void Present(string key, string title, string detail, string glyph, string color, Window anchor, bool atTaskbar)
    {
        NotificationKey = key; Title = title; heading.Text = title; message.Text = detail;
        icon.Kind = glyph; icon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        AutomationProperties.SetName(this, title + Environment.NewLine + detail);
        var anchorHandle = new WindowInteropHelper(anchor).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(anchorHandle);
        var area = screen.WorkingArea;
        var scale = Math.Max(1, GetDpiForWindow(anchorHandle)) / 96d;
        MaxWidth = area.Width / scale; MaxHeight = area.Height / scale;
        ((FrameworkElement)Content).Measure(new Size(Math.Min(Width, MaxWidth), MaxHeight));
        var height = Math.Max(1, ((FrameworkElement)Content).DesiredSize.Height);
        var width = Math.Min((int)Math.Ceiling(Width * scale), area.Width);
        var pixelsHigh = Math.Min((int)Math.Ceiling(height * scale), area.Height);
        var left = area.Right - width - (int)(12 * scale);
        var top = area.Bottom - pixelsHigh - (int)(12 * scale);
        if (atTaskbar && GetWindowRect(anchorHandle, out var bounds))
        {
            var rect = new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            var target = TaskbarLayout.PlaceFlyout(rect, rect, new(area.Left, area.Top, area.Width, area.Height), scale, height);
            left = target.Left; top = target.Top; width = target.Width; pixelsHigh = target.Height;
        }
        // Position the native surface before revealing it, without activating the window.
        Opacity = 0; Show();
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, -1, Math.Max(area.Left, left), Math.Max(area.Top, top), width, pixelsHigh, 0x0010 | 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        Opacity = 1;
        dismissTimer.Stop(); if (!IsMouseOver) dismissTimer.Start();
    }

    internal void Dismiss() { dismissTimer.Stop(); Hide(); }
    private static nint MouseActivation(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0;
        handled = true; return 3; // MA_NOACTIVATE allows the close button without stealing focus.
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
