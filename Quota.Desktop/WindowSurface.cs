using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Quota.Desktop;

internal static class WindowSurface
{
    internal const int CornerRadius = 8;
    private static bool registered;
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached("Applied", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    public static void Register()
    {
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Loaded));
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window) || window.AllowsTransparency || (bool)window.GetValue(AppliedProperty)) return;
        window.SetValue(AppliedProperty, true);
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome is null) return;
        // DWM owns the outer shape and shadow. WPF draws one matching inner border.
        chrome.CornerRadius = new CornerRadius(0);
        var handle = new WindowInteropHelper(window).Handle;
        var round = 2; var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
        Border? clipped = null; Size previous = default; int previousRadius = -1;
        void ClipContent()
        {
            if (window.Content is not Border border) return;
            var radius = window.WindowState == WindowState.Maximized ? 0 : CornerRadius;
            var size = new Size(border.ActualWidth, border.ActualHeight);
            if (ReferenceEquals(clipped, border) && size == previous && radius == previousRadius) return;
            clipped = border; previous = size; previousRadius = radius;
            border.CornerRadius = new CornerRadius(radius);
            border.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, border.ActualWidth), Math.Max(0, border.ActualHeight)), radius, radius);
        }
        window.LayoutUpdated += (_, _) => ClipContent();
        window.SizeChanged += (_, _) => ClipContent();
        window.StateChanged += (_, _) => ClipContent();
        ClipContent();
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
