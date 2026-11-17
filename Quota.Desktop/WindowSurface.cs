using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Quota.Desktop;

internal static class WindowSurface
{
    internal const int CornerRadius = 8;
    private static bool registered;
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached("Applied", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty GlassEnabledProperty = DependencyProperty.RegisterAttached("GlassEnabled", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty PreparedProperty = DependencyProperty.RegisterAttached("Prepared", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty NativeMaterialProperty = DependencyProperty.RegisterAttached("NativeMaterial", typeof(int), typeof(WindowSurface), new PropertyMetadata(0));
    private static readonly DependencyProperty BlurPendingProperty = DependencyProperty.RegisterAttached("BlurPending", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    internal static bool IsGlassEnabled(Window window) => (bool)window.GetValue(GlassEnabledProperty);
    public static void Register()
    {
        if (registered) return;
        registered = true;
        WindowChrome.WindowChromeProperty.OverrideMetadata(typeof(Window), new PropertyMetadata(null,
            (sender, args) => { if (sender is Window window && args.NewValue is WindowChrome) Prepare(window); }));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Loaded));
    }
    private static void Prepare(Window window)
    {
        if ((bool)window.GetValue(PreparedProperty)) return;
        window.SetValue(PreparedProperty, true);
        window.SourceInitialized += (_, _) => ApplyMaterial(window);
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) ApplyMaterial(window); };
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window) || window.AllowsTransparency || (bool)window.GetValue(AppliedProperty)) return;
        Prepare(window);
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
            if (WindowChrome.GetWindowChrome(window) is { } currentChrome
                && currentChrome.CornerRadius != new CornerRadius(0)) ApplyMaterial(window);
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
        if (window.AllowsTransparency || WindowChrome.GetWindowChrome(window) is not { } chrome) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source?.CompositionTarget is null) return;
        var handle = source.Handle;
        var dark = ThemeService.IsLight ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var topLevel = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) == 0;
        var requested = ThemeService.GlassRequested && topLevel;
        // Keep the blur source attached across material changes. Solid root brushes
        // cover it completely, avoiding DWM's stale backdrop when blur is rebuilt.
        var nativeRequested = topLevel && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var nativeMaterial = (int)window.GetValue(NativeMaterialProperty);
        var nativeChanged = nativeMaterial == 0 || nativeRequested != (nativeMaterial > 1);
        if (nativeChanged)
        {
            var backdrop = 1;
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            var hostBackdrop = nativeRequested ? 1 : 0;
            DwmSetWindowAttribute(handle, 17, ref hostBackdrop, sizeof(int));
            var nativeBlur = SetBackdropBlur(handle, nativeRequested);
            nativeMaterial = nativeRequested && nativeBlur ? 2 : 1;
            if (nativeRequested && !nativeBlur)
            {
                backdrop = 3;
                if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0) nativeMaterial = 3;
            }
            window.SetValue(NativeMaterialProperty, nativeMaterial);
        }
        var glass = requested && nativeMaterial > 1;
        window.SetValue(GlassEnabledProperty, glass);
        // Keep the top-level composition canvas transparent across material changes.
        // Solid mode is painted by the opaque root brush, preserving the same HWND.
        var frame = new Thickness(topLevel ? -1 : 0);
        var frameChanged = chrome.GlassFrameThickness != frame;
        chrome.CornerRadius = new CornerRadius(0);
        chrome.GlassFrameThickness = frame;
        var margins = new Margins { Left = topLevel ? -1 : 0, Right = topLevel ? -1 : 0, Top = topLevel ? -1 : 0, Bottom = topLevel ? -1 : 0 };
        DwmExtendFrameIntoClientArea(handle, ref margins);
        source.CompositionTarget.BackgroundColor = topLevel ? Colors.Transparent : ThemeService.OpaqueBackground.Color;
        var resources = System.Windows.Application.Current.Resources;
        window.Resources["Background"] = glass ? ThemeService.GlassBackground : ThemeService.PanelBackground;
        window.Resources["Surface"] = glass ? resources["Surface"] : ThemeService.OpaqueSurface;
        window.Background = topLevel ? Brushes.Transparent : ThemeService.OpaqueBackground;
        if (window.Content is Border { Background: null } border) border.SetResourceReference(Border.BackgroundProperty, "Background");
        DesktopWindowHost.SynchronizeVisibility(window, handle);
        if (nativeChanged || frameChanged) QueueBackdropBlur(window, handle);
    }
    private static void QueueBackdropBlur(Window window, nint handle)
    {
        if ((bool)window.GetValue(BlurPendingProperty)) return;
        window.SetValue(BlurPendingProperty, true);
        // Commit the backdrop after WindowChrome has applied its frame changes.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            window.SetValue(BlurPendingProperty, false);
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is null || window.Dispatcher.HasShutdownStarted) return;
            // Native blur can reveal a hidden HWND without restoring WPF content.
            // Startup and close-to-tray may hide the window before this callback runs.
            if (!window.IsVisible) { DesktopWindowHost.SynchronizeVisibility(window, handle); return; }
            var topLevel = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) == 0;
            var blurBehind = new BlurBehind { Flags = 1, Enable = topLevel };
            DwmEnableBlurBehindWindow(handle, ref blurBehind);
            if ((int)window.GetValue(NativeMaterialProperty) == 2) SetBackdropBlur(handle, true);
            DesktopWindowHost.SynchronizeVisibility(window, handle);
        });
    }
    private static bool SetBackdropBlur(nint window, bool enabled)
    {
        // A thin transparent tint over native blur leaves the desktop colors visible.
        // The opaque system acrylic tint would otherwise obscure this glass treatment.
        var policy = new AccentPolicy { State = enabled ? 3 : 0 };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = 19, Data = memory, Size = (nuint)Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(window, ref data);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public nint Data; public nuint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind { public int Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enable; public nint Region; [MarshalAs(UnmanagedType.Bool)] public bool Transition; }
    [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(nint window, ref CompositionData data);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
