using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Quota.Core;

namespace Quota.Desktop;

internal sealed class DesktopWindowHost
{
    private bool attached;
    private nint attachedParent;
    private nint originalStyle;
    private nint originalOwner;
    private bool originalShowInTaskbar;
    private Point dragStart;
    private PixelRect dragBounds;
    private bool dragging;

    public PixelRect Bounds(Window window) => WindowPlacementGuard.TryGetBounds(window, out var bounds) ? bounds : default;

    public bool Place(Window window, PixelRect workArea, double x, double y)
    {
        if (!WindowPlacementGuard.TryGetBounds(window, out var current)) return false;
        return WindowPlacementGuard.SetBounds(window, WidgetPlacement.Place(workArea, current.Width, current.Height, x, y));
    }

    public Point GetPosition(Window window)
    {
        if (!attached || !WindowPlacementGuard.TryGetBounds(window, out var bounds)) return new Point(window.Left, window.Top);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        return new Point(bounds.Left / dpi.DpiScaleX, bounds.Top / dpi.DpiScaleY);
    }

    public void BeginDrag(Window window)
    {
        if (!GetCursorPos(out var cursor) || !WindowPlacementGuard.TryGetBounds(window, out dragBounds)) return;
        dragStart = new Point(cursor.X, cursor.Y); dragging = true; window.CaptureMouse();
    }

    public void MoveDrag(Window window)
    {
        if (!dragging || !GetCursorPos(out var cursor)) return;
        var target = dragBounds with
        {
            Left = dragBounds.Left + cursor.X - (int)dragStart.X,
            Top = dragBounds.Top + cursor.Y - (int)dragStart.Y
        };
        WindowPlacementGuard.SetBounds(window, target, 0x0001);
    }

    public void EndDrag(Window window) { dragging = false; window.ReleaseMouseCapture(); }

    public bool Apply(Window window, bool desktopOnly)
    {
        if (!WindowPlacementGuard.TryGetBounds(window, out var bounds)) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (attached && (GetParent(handle) != attachedParent || !IsWindow(attachedParent)))
            if (!Detach(window, bounds)) return false;
        if (desktopOnly == attached) { SynchronizeVisibility(window, handle); return true; }
        if (!desktopOnly) return Detach(window, bounds);
        var desktop = FindWindow("Progman", null);
        EnumWindows((candidate, _) =>
        {
            if (FindWindowEx(candidate, 0, "SHELLDLL_DefView", null) == 0) return true;
            desktop = candidate; return false;
        }, 0);
        return desktop != 0 && Attach(window, desktop, bounds);
    }

    // Commit native attachment after parenting and placement both succeed.
    private bool Attach(Window window, nint parent, PixelRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        originalStyle = GetWindowLongPtr(handle, -16);
        originalOwner = GetWindowLongPtr(handle, -8);
        originalShowInTaskbar = window.ShowInTaskbar;
        // WPF updates native ownership when ShowInTaskbar changes.
        window.ShowInTaskbar = false;
        SetWindowLongPtr(handle, -16, (nint)(((long)originalStyle & ~0x80000000L) | 0x40000000L));
        SetParent(handle, parent);
        if (GetParent(handle) != parent || !WindowPlacementGuard.SetBounds(window, bounds, 0x0020))
        {
            SetParent(handle, 0); SetWindowLongPtr(handle, -16, originalStyle);
            window.ShowInTaskbar = originalShowInTaskbar;
            SetWindowLongPtr(handle, -8, originalOwner);
            WindowPlacementGuard.SetBounds(window, bounds, 0x0020); return false;
        }
        attached = true; attachedParent = parent;
        SynchronizeVisibility(window, handle); return true;
    }

    private bool Detach(Window window, PixelRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var previous = SetParent(handle, 0);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) return false;
        SetWindowLongPtr(handle, -16, originalStyle);
        window.ShowInTaskbar = originalShowInTaskbar;
        SetWindowLongPtr(handle, -8, originalOwner);
        attached = false; attachedParent = 0;
        var placed = WindowPlacementGuard.SetBounds(window, bounds, 0x0020);
        SynchronizeVisibility(window, handle); return placed;
    }

    private static void SynchronizeVisibility(Window window, nint handle)
    {
        var visible = (GetWindowLongPtr(handle, -16).ToInt64() & 0x10000000L) != 0;
        var requested = window.Visibility == Visibility.Visible;
        if (visible != requested) ShowWindow(handle, requested ? 4 : 0);
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
