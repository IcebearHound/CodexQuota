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
    private enum Backdrop { Uninitialized, Solid, AccentBlur, SystemBackdrop }
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached("Applied", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty GlassEnabledProperty = DependencyProperty.RegisterAttached("GlassEnabled", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty PreparedProperty = DependencyProperty.RegisterAttached("Prepared", typeof(bool), typeof(WindowSurface), new PropertyMetadata(false));
    private static readonly DependencyProperty NativeMaterialProperty = DependencyProperty.RegisterAttached("NativeMaterial", typeof(Backdrop), typeof(WindowSurface), new PropertyMetadata(Backdrop.Uninitialized));
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
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var round = 2; var noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
            ApplyMaterial(window);
        };
        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible) QueueBackdropCommit(window, new WindowInteropHelper(window).Handle);
        };
    }
    private static void Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window || !ReferenceEquals(args.OriginalSource, window) || window.AllowsTransparency ||
            WindowChrome.GetWindowChrome(window) is null || (bool)window.GetValue(AppliedProperty)) return;
        Prepare(window);
        window.SetValue(AppliedProperty, true);
        ApplyMaterial(window);
        QueueBackdropCommit(window, new WindowInteropHelper(window).Handle);
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
        // WindowChrome updates the non-client frame synchronously. Set it before
        // accent blur so its initial frame negotiation cannot undo the backdrop.
        var frame = new Thickness(topLevel ? -1 : 0);
        var frameChanged = chrome.GlassFrameThickness != frame;
        chrome.CornerRadius = new CornerRadius(0);
        chrome.GlassFrameThickness = frame;
        var nativeMaterial = (Backdrop)window.GetValue(NativeMaterialProperty);
        if (frameChanged || nativeMaterial == Backdrop.Uninitialized) ExtendFrame(handle, topLevel);
        // Keep the blur source attached across material changes. Solid root brushes
        // cover it completely, avoiding DWM's stale backdrop when blur is rebuilt.
        var nativeRequested = topLevel && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var nativeChanged = nativeMaterial == Backdrop.Uninitialized || nativeRequested != (nativeMaterial is Backdrop.AccentBlur or Backdrop.SystemBackdrop);
        if (nativeChanged)
        {
            var backdrop = 1;
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            var hostBackdrop = nativeRequested ? 1 : 0;
            DwmSetWindowAttribute(handle, 17, ref hostBackdrop, sizeof(int));
            var nativeBlur = SetBackdropBlur(handle, nativeRequested);
            nativeMaterial = nativeRequested && nativeBlur ? Backdrop.AccentBlur : Backdrop.Solid;
            if (nativeRequested && !nativeBlur)
            {
                backdrop = 3;
                if (DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0) nativeMaterial = Backdrop.SystemBackdrop;
            }
            window.SetValue(NativeMaterialProperty, nativeMaterial);
        }
        var glass = requested && nativeMaterial is Backdrop.AccentBlur or Backdrop.SystemBackdrop;
        var materialChanged = glass != IsGlassEnabled(window);
        window.SetValue(GlassEnabledProperty, glass);
        // Keep the top-level composition canvas transparent across material changes.
        // Solid mode is painted by the opaque root brush, preserving the same HWND.
        source.CompositionTarget.BackgroundColor = topLevel ? Colors.Transparent : ThemeService.OpaqueBackground.Color;
        var resources = System.Windows.Application.Current.Resources;
        window.Resources["Background"] = glass ? ThemeService.GlassBackground : ThemeService.PanelBackground;
        window.Resources["Surface"] = glass ? resources["Surface"] : ThemeService.OpaqueSurface;
        window.Background = topLevel ? Brushes.Transparent : ThemeService.OpaqueBackground;
        if (window.Content is Border { Background: null } border) border.SetResourceReference(Border.BackgroundProperty, "Background");
        DesktopWindowHost.SynchronizeVisibility(window, handle);
        if (nativeChanged || frameChanged || materialChanged) QueueBackdropCommit(window, handle);
    }
    private static void ExtendFrame(nint handle, bool topLevel)
    {
        var margin = topLevel ? -1 : 0;
        var margins = new Margins { Left = margin, Right = margin, Top = margin, Bottom = margin };
        DwmExtendFrameIntoClientArea(handle, ref margins);
    }
    private static void QueueBackdropCommit(Window window, nint handle)
    {
        if (handle == 0 || (bool)window.GetValue(BlurPendingProperty)) return;
        window.SetValue(BlurPendingProperty, true);
        // Creating the HWND is not the same as presenting it: Show can reset the
        // frame. Commit on every show, after that transition and before idle.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            window.SetValue(BlurPendingProperty, false);
            if (!window.IsVisible || window.Dispatcher.HasShutdownStarted ||
                HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return;
            var topLevel = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) == 0;
            var material = (Backdrop)window.GetValue(NativeMaterialProperty);
            ExtendFrame(handle, topLevel);
            target.BackgroundColor = topLevel ? Colors.Transparent : ThemeService.OpaqueBackground.Color;
            var blurBehind = new BlurBehind { Flags = 1, Enable = material == Backdrop.AccentBlur && topLevel };
            DwmEnableBlurBehindWindow(handle, ref blurBehind);
            if (material == Backdrop.AccentBlur && topLevel)
            {
                SetBackdropBlur(handle, true);
                if (IsGlassEnabled(window) && window.WindowState == WindowState.Normal)
                    RefreshRedirectionSurface(handle);
            }
            if (material == Backdrop.SystemBackdrop && topLevel)
            {
                var transientBackdrop = 3;
                DwmSetWindowAttribute(handle, 38, ref transientBackdrop, sizeof(int));
            }
            DesktopWindowHost.SynchronizeVisibility(window, handle);
        });
    }
    private static void RefreshRedirectionSurface(nint handle)
    {
        // Windows can retain the opaque redirection surface allocated for a new
        // HWND. A real client-size change rebuilds it; invalidating paint or
        // sending a frame-change notification leaves that surface in place.
        // Do both native steps before rendering, preserving position and focus.
        if (!GetWindowRect(handle, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        const uint preservePlacement = 0x0002 | 0x0004 | 0x0010; // NOMOVE | NOZORDER | NOACTIVATE
        if (width > 0 && height > 0 && SetWindowPos(handle, 0, 0, 0, width + 1, height, preservePlacement))
            SetWindowPos(handle, 0, 0, 0, width, height, preservePlacement);
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
