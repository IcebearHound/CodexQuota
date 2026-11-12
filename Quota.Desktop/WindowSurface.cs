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
        ApplyMaterial(window);
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
    internal static void ApplyMaterial(Window window)
    {
        if (!window.IsLoaded || window.AllowsTransparency || WindowChrome.GetWindowChrome(window) is not { } chrome) return;
        var source = PresentationSource.FromVisual(window) as HwndSource;
        if (source?.CompositionTarget is null) return;
        var handle = source.Handle;
        var dark = ThemeService.IsLight ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var backdrop = ThemeService.GlassRequested ? 3 : 1;
        var topLevel = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) == 0;
        var glass = ThemeService.GlassRequested && topLevel && DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0;
        if (!glass) { backdrop = 1; DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)); }
        chrome.GlassFrameThickness = new Thickness(glass ? -1 : 0);
        var margins = new Margins { Left = glass ? -1 : 0, Right = glass ? -1 : 0, Top = glass ? -1 : 0, Bottom = glass ? -1 : 0 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        source.CompositionTarget.BackgroundColor = glass ? Colors.Transparent : ThemeService.OpaqueBackground.Color;
        var resources = System.Windows.Application.Current.Resources;
        window.Resources["Background"] = glass ? resources["Background"] : ThemeService.OpaqueBackground;
        window.Resources["Surface"] = glass ? resources["Surface"] : ThemeService.OpaqueSurface;
        window.SetCurrentValue(Window.BackgroundProperty, glass ? Brushes.Transparent : ThemeService.OpaqueBackground);
        if (window.Content is Border { Background: null } border) border.SetResourceReference(Border.BackgroundProperty, "Background");
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
