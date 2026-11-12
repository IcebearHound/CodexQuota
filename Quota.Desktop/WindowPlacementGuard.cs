using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Quota.Core;
using Forms = System.Windows.Forms;

namespace Quota.Desktop;
internal static class WindowPlacementGuard
{
    public static bool IsAlive(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != 0 && IsWindow(handle);
    }
    public static bool EnsureVisible(Window window)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal || !TryGetBounds(window, out var current)) return false;
        var workAreas = WorkAreas();
        if (workAreas.Length == 0) return false;
        var adjusted = ScreenLayout.EnsureVisible(current, workAreas);
        return adjusted != current && SetBounds(window, adjusted);
    }
    public static bool RestorePosition(Window window, int left, int top)
    {
        if (!TryGetBounds(window, out var bounds)) return false;
        var workAreas = WorkAreas();
        if (workAreas.Length == 0) return false;
        var adjusted = ScreenLayout.EnsureVisible(bounds with { Left = left, Top = top }, workAreas);
        return SetBounds(window, adjusted);
    }
    public static bool TryGetBounds(Window window, out PixelRect bounds)
    {
        bounds = default;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var native)) return false;
        var width = (long)native.Right - native.Left; var height = (long)native.Bottom - native.Top;
        if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue) return false;
        bounds = new(native.Left, native.Top, (int)width, (int)height); return true;
    }
    public static bool SetBounds(Window window, PixelRect bounds, uint extraFlags = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || bounds.Width <= 0 || bounds.Height <= 0) return false;
        var point = new NativePoint { X = bounds.Left, Y = bounds.Top };
        var parent = GetParent(handle);
        if (((long)GetWindowLongPtr(handle, -16) & 0x40000000L) != 0 && parent != 0) MapWindowPoints(0, parent, ref point, 1);
        return SetWindowPos(handle, 0, point.X, point.Y, bounds.Width, bounds.Height, 0x0004 | 0x0010 | extraFlags);
    }
    private static PixelRect[] WorkAreas() => Forms.Screen.AllScreens.Select(screen => new PixelRect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height)).ToArray();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, ref NativePoint point, uint count);
}
