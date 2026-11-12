using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Quota.Core;

namespace Quota.Desktop;

internal sealed class TaskbarWindowHost : IDisposable
{
    private nint taskbar;
    private nint handle;
    private int probing;
    private long lastProbe;
    private int layoutRevision;
    private double weatherWidth = 152;
    private LayoutSnapshot? snapshot;
    private nint foregroundHook;
    private WinEventCallback? foregroundCallback;
    private sealed record LayoutSnapshot(nint Taskbar, PixelRect Bounds, PixelRect[] Occupied, PixelRect? Widgets, PixelRect? Tray, bool StartAlignedLeft, long ReadAt);

    public bool IsAttached => handle != 0 && IsWindow(handle) && taskbar != 0 && GetParent(handle) == taskbar;
    public bool WindowAlive => handle != 0 && IsWindow(handle);
    public bool Visible => WindowAlive && IsWindowVisible(handle);
    public bool Layered => WindowAlive && (GetWindowLongPtr(handle, -20).ToInt64() & 0x00080000) != 0;
    public bool HitTestVisible => Visible && WindowFromPoint(new NativePoint { X = (int)Bounds.CenterX, Y = (int)Bounds.CenterY }) == handle;
    public bool LayoutReady { get; private set; }
    public bool Suppressed { get; private set; }
    public PixelRect Bounds { get; private set; }
    public PixelRect TaskbarBounds { get; private set; }
    public PixelRect FlyoutBounds { get; private set; }
    private nint flyoutHandle;
    public bool FlyoutNativeVisible => flyoutHandle != 0 && IsWindow(flyoutHandle) && IsWindowVisible(flyoutHandle);
    public bool FlyoutHitTestVisible => FlyoutNativeVisible && WindowFromPoint(new NativePoint { X = (int)FlyoutBounds.CenterX, Y = (int)FlyoutBounds.CenterY }) == flyoutHandle;
    public PixelRect[] OccupiedBounds { get; private set; } = [];
    public RgbColor? BackgroundColor { get; private set; }

    public void Hide() { if (WindowAlive) ShowWindow(handle, 0); }
    public void InvalidateLayout() { Interlocked.Increment(ref layoutRevision); lastProbe = 0; Volatile.Write(ref snapshot, null); }

    public void WatchForeground(Action changed)
    {
        if (foregroundHook != 0) return;
        foregroundCallback = (_, _, _, _, _, _, _) => changed();
        foregroundHook = SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
    }

    public void Dispose()
    {
        if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
        foregroundHook = 0;
    }

    public void PositionFlyout(Window window)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(Bounds.Left, Bounds.Top));
        var scale = GetDpiForWindow(handle) / 96d;
        if (scale <= 0) scale = 1;
        var workArea = screen.WorkingArea;
        var pixels = new PixelRect(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        var maximum = TaskbarLayout.PlaceFlyout(TaskbarBounds, Bounds, pixels, scale, workArea.Height / scale);
        window.MaxWidth = maximum.Width / scale;
        window.MaxHeight = maximum.Height / scale;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.MaxWidth, window.MaxHeight));
        var target = TaskbarLayout.PlaceFlyout(TaskbarBounds, Bounds, pixels, scale, Math.Max(1, content.DesiredSize.Height));
        flyoutHandle = new WindowInteropHelper(window).Handle;
        GetWindowRect(flyoutHandle, out var current);
        if (!IsWindowVisible(flyoutHandle) || current.Left != target.Left || current.Top != target.Top || current.Right - current.Left != target.Width || current.Bottom - current.Top != target.Height)
            if (!SetWindowPos(flyoutHandle, -1, target.Left, target.Top, target.Width, target.Height, 0x0010 | 0x0040)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GetWindowRect(flyoutHandle, out var actual);
        FlyoutBounds = new(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
    }

    public bool Dock(Window window, bool detailed = false, string position = "auto")
    {
        LayoutReady = false;
        Suppressed = false;
        var parent = FindWindow("Shell_TrayWnd", null);
        handle = new WindowInteropHelper(window).Handle;
        if (parent == 0 || handle == 0 || !IsWindow(handle)) { Hide(); return false; }
        if (!GetWindowRect(parent, out var nativeBounds) || !GetClientRect(parent, out var client)) { Hide(); return false; }
        if (client.Right <= 0 || client.Bottom <= 0 || client.Bottom > client.Right) { Hide(); return false; }
        var ownerChanged = GetParent(handle) != parent;
        if (ownerChanged)
        {
            var style = GetWindowLongPtr(handle, -16).ToInt64();
            SetWindowLongPtr(handle, -16, (nint)((style & ~0x40000000L) | 0x80000000L));
            var extended = GetWindowLongPtr(handle, -20).ToInt64();
            var presentation = window.ShowInTaskbar ? 0x00040000L : 0x08000080L;
            var layered = window.AllowsTransparency ? 0x00080000L : 0;
            SetWindowLongPtr(handle, -20, (nint)((extended & ~0x080C00A0L) | presentation | layered));
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtr(handle, -8, parent);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        taskbar = parent;
        TaskbarBounds = new(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right - nativeBounds.Left, nativeBounds.Bottom - nativeBounds.Top);
        var dpi = GetDpiForWindow(parent);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var monitor = System.Windows.Forms.Screen.FromHandle(parent).Bounds;
        if (!IsWindowVisible(parent) || ForegroundIsFullscreen(parent, new(monitor.Left, monitor.Top, monitor.Width, monitor.Height)))
        {
            Suppressed = true; Hide(); return false;
        }
        ProbeLayout(parent, TaskbarBounds);
        var measured = Volatile.Read(ref snapshot);
        LayoutReady = measured is not null && measured.Taskbar == parent && measured.Bounds == TaskbarBounds && Stopwatch.GetElapsedTime(measured.ReadAt) <= TimeSpan.FromSeconds(15);
        if (!LayoutReady) { Hide(); return false; }
        OccupiedBounds = measured!.Occupied;
        if (measured.Widgets is { Width: > 0 } widgets && widgets.Width / scale >= 96) weatherWidth = widgets.Width / scale;
        var placement = TaskbarLayout.FindAutomaticSpace(TaskbarBounds, scale, measured.StartAlignedLeft, measured.Widgets, measured.Tray, OccupiedBounds, desiredWidth: detailed ? weatherWidth : 44, fillHeight: true, placement: position);
        if (placement is null) { Hide(); return false; }
        var target = placement.Value;
        if (Math.Abs(window.Width - target.Width / scale) > 0.1) window.Width = target.Width / scale;
        if (Math.Abs(window.Height - target.Height / scale) > 0.1) window.Height = target.Height / scale;
        if (target.Left < monitor.Left || target.Top < monitor.Top || target.Right > monitor.Right || target.Bottom > monitor.Bottom)
        {
            Suppressed = true; Hide(); return false;
        }
        GetWindowRect(handle, out var current);
        var moved = current.Left != target.Left || current.Top != target.Top;
        var resized = current.Right - current.Left != target.Width || current.Bottom - current.Top != target.Height;
        var newBackground = ownerChanged || moved || resized ? SampleBackground(target) : null;
        // A top-level owned window has its own DWM surface over the taskbar.
        if (ownerChanged || moved || resized || !Visible)
            if (!SetWindowPos(handle, -1, target.Left, target.Top, target.Width, target.Height, (uint)(0x0010 | 0x0040 | (ownerChanged ? 0x0020 : 0)))) throw new Win32Exception(Marshal.GetLastPInvokeError());
        GetWindowRect(handle, out var actual);
        Bounds = new(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
        BackgroundColor = newBackground ?? SampleBackground(Bounds) ?? BackgroundColor;
        return IsAttached && Visible;
    }

    private static RgbColor? SampleBackground(PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        var context = GetDC(0);
        if (context == 0) return null;
        nint memory = 0, bitmap = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(context);
            if (memory == 0) return null;
            var header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = bounds.Width, Height = -2, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(context, ref header, 0, out var pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) return null;
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) return null;
            // Copy the two transparent gutters together. Reading individual desktop
            // pixels repeatedly synchronizes with DWM and stalls the UI dispatcher.
            var inset = Math.Max(3, (int)Math.Ceiling(GetDpiForWindow(FindWindow("Shell_TrayWnd", null)) / 48d));
            if (!BitBlt(memory, 0, 0, bounds.Width, 1, context, bounds.Left, bounds.Top + inset, 0x00CC0020)
                || !BitBlt(memory, 0, 1, bounds.Width, 1, context, bounds.Left, (int)(bounds.Bottom - inset - 1), 0x00CC0020)) return null;
            GdiFlush();
            var colors = new List<RgbColor>(26);
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 13; column++)
                {
                    var x = (int)((column + 0.5) * bounds.Width / 13);
                    var pixel = (uint)Marshal.ReadInt32(pixels, (row * bounds.Width + x) * sizeof(int));
                    colors.Add(new((byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel));
                }
            return ContrastColors.AverageBackground(colors);
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, context);
        }
    }

    private bool ForegroundIsFullscreen(nint parent, PixelRect monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == parent || foreground == handle || foreground == flyoutHandle || IsIconic(foreground)) return false;
        var className = new StringBuilder(128);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW") return false;
        if (!GetWindowRect(foreground, out var bounds)) return false;
        return TaskbarLayout.CoversMonitor(new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), monitor);
    }

    private void ProbeLayout(nint parent, PixelRect bounds)
    {
        if (lastProbe != 0 && Stopwatch.GetElapsedTime(lastProbe) < TimeSpan.FromSeconds(5)) return;
        if (Interlocked.CompareExchange(ref probing, 1, 0) != 0) return;
        lastProbe = Stopwatch.GetTimestamp();
        var revision = Volatile.Read(ref layoutRevision);
        // UI Automation can wait on Explorer. Keep it off the WPF dispatcher.
        _ = Task.Run(() =>
        {
            try
            {
                var controlTypes = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
                var outsideProcess = new NotCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId));
                var condition = new AndCondition(controlTypes, outsideProcess);
                var root = AutomationElement.FromHandle(parent);
                // Owned popups appear as taskbar children. Skip their entire UIA subtree.
                var branches = root.FindAll(TreeScope.Children, outsideProcess).Cast<AutomationElement>().ToArray();
                var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
                cache.Add(AutomationElement.ProcessIdProperty); cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.AutomationIdProperty);
                var controls = new List<AutomationElement>();
                using (cache.Activate())
                    foreach (var branch in branches) controls.AddRange(branch.FindAll(TreeScope.Subtree, condition).Cast<AutomationElement>());
                var visible = controls.Select(element => element.Cached)
                    .Where(info => info.ProcessId != Environment.ProcessId && !info.IsOffscreen)
                    .Where(info => !info.BoundingRectangle.IsEmpty && info.BoundingRectangle.Width > 0 && info.BoundingRectangle.Height > 0 && info.BoundingRectangle.IntersectsWith(new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height))).ToArray();
                var occupied = visible.Select(info => Pixels(info.BoundingRectangle)).ToArray();
                var scale = GetDpiForWindow(parent) / 96d;
                if (scale <= 0) scale = 1;
                var leftAligned = WindowsTaskbarSettings.StartAlignedLeft || visible.Any(info => info.AutomationId.Equals("StartButton", StringComparison.OrdinalIgnoreCase) && info.BoundingRectangle.Left <= bounds.Left + 48 * scale);
                var leftControls = visible.Where(info => info.BoundingRectangle.Left <= bounds.Left + 48 * scale).ToArray();
                PixelRect? widgets = leftControls.Where(info => info.AutomationId.Contains("Widgets", StringComparison.OrdinalIgnoreCase) || info.AutomationId.Contains("NewsAndInterests", StringComparison.OrdinalIgnoreCase))
                    .Select(info => (PixelRect?)Pixels(info.BoundingRectangle)).FirstOrDefault();
                if (widgets is null && !leftAligned && WindowsTaskbarSettings.WidgetsVisible == true)
                    widgets = leftControls.Where(info => !info.AutomationId.Contains("Start", StringComparison.OrdinalIgnoreCase)).Select(info => (PixelRect?)Pixels(info.BoundingRectangle)).FirstOrDefault();
                PixelRect? tray = null;
                var trayWindow = FindWindowEx(parent, 0, "TrayNotifyWnd", null);
                if (trayWindow != 0 && GetWindowRect(trayWindow, out var trayBounds) && trayBounds.Right > trayBounds.Left && trayBounds.Bottom > trayBounds.Top)
                    tray = new(trayBounds.Left, trayBounds.Top, trayBounds.Right - trayBounds.Left, trayBounds.Bottom - trayBounds.Top);
                if (tray is null)
                {
                    var frame = branches.Select(branch => branch.FindFirst(TreeScope.Subtree, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayFrame"))).FirstOrDefault(element => element is not null);
                    if (frame is not null && frame.Current.BoundingRectangle is { IsEmpty: false } frameBounds) tray = Pixels(frameBounds);
                }
                if (occupied.Length > 0 && revision == Volatile.Read(ref layoutRevision)) Volatile.Write(ref snapshot, new(parent, bounds, occupied, widgets, tray, leftAligned, Stopwatch.GetTimestamp()));
            }
            catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A recent measured layout remains usable while the next probe retries.
            }
            finally { Interlocked.Exchange(ref probing, 0); }
        });
    }

    private static PixelRect Pixels(Rect rect) => new((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right) - (int)Math.Floor(rect.Left), (int)Math.Ceiling(rect.Bottom) - (int)Math.Floor(rect.Top));

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint context);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint context);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint context, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint context, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint context);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint firstEvent, uint lastEvent, nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder className, int length);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
