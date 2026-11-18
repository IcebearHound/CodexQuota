using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Quota.Desktop;

/// <summary>Frame-paced wheel scrolling for physical-pixel viewports.</summary>
internal static class SmoothScroll
{
    private static readonly DependencyProperty MotionProperty = DependencyProperty.RegisterAttached(
        "Motion", typeof(Motion), typeof(SmoothScroll));

    internal static void Register() => EventManager.RegisterClassHandler(typeof(ScrollViewer),
        UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    private static void OnWheel(object sender, MouseWheelEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        if (viewer.CanContentScroll || viewer.ScrollableHeight <= 0 || args.Delta == 0 ||
            Keyboard.Modifiers != ModifierKeys.None || !SystemParameters.ClientAreaAnimation ||
            SystemParameters.WheelScrollLines <= 0 || !IsNearestViewer(viewer, args.OriginalSource as DependencyObject)) return;

        var motion = (Motion?)viewer.GetValue(MotionProperty);
        if (motion is null) { motion = new(viewer); viewer.SetValue(MotionProperty, motion); }
        // High-resolution input already supplies intermediate positions. Apply those
        // directly rather than smoothing a trackpad's own motion a second time.
        var offset = -args.Delta / 120d * SystemParameters.WheelScrollLines * 20;
        motion.ScrollBy(offset, args.Delta % 120 == 0);
        args.Handled = true;
    }

    private static bool IsNearestViewer(ScrollViewer viewer, DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollViewer nearest) return nearest == viewer;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private sealed class Motion
    {
        private readonly ScrollViewer viewer;
        private bool rendering;
        private double target, position;
        private double? lastRequest;
        private TimeSpan? lastFrame;
        private const double ResponseMilliseconds = 42;

        internal Motion(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.Unloaded += (_, _) => Stop();
            viewer.IsVisibleChanged += (_, _) => { if (!viewer.IsVisible) Stop(); };
            viewer.PreviewKeyDown += (_, _) => Stop();
            viewer.PreviewMouseDown += (_, _) => Stop();
            viewer.ScrollChanged += (_, args) =>
            {
                if (args.OriginalSource != viewer || args.VerticalChange == 0) return;
                if (rendering && (lastRequest is not double expected || Math.Abs(args.VerticalOffset - Math.Min(expected, viewer.ScrollableHeight)) > 0.75))
                    Stop(); // A thumb, focus request, or programmatic scroll took over.
                lastRequest = null;
            };
        }

        internal void ScrollBy(double delta, bool animate)
        {
            var current = lastRequest ?? viewer.VerticalOffset;
            var basis = rendering && animate ? target : current;
            // Reverse immediately, rather than spending a wheel notch cancelling inertia.
            if ((basis - current) * delta < 0) basis = current;
            target = Math.Clamp(basis + delta, 0, viewer.ScrollableHeight);
            if (!animate)
            {
                Stop();
                if (target != viewer.VerticalOffset) lastRequest = target;
                viewer.ScrollToVerticalOffset(target);
                return;
            }
            if (Math.Abs(target - current) < 0.25) { Stop(); return; }
            if (rendering) return;
            position = current;
            lastFrame = null;
            rendering = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs args)
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (time == lastFrame) return;
            var elapsed = lastFrame is { } previous ? (time - previous).TotalMilliseconds : 8;
            lastFrame = time;
            target = Math.Min(target, viewer.ScrollableHeight);
            var remaining = Math.Exp(-Math.Max(0, elapsed) / ResponseMilliseconds);
            position = target + (position - target) * remaining;
            var finished = Math.Abs(position - target) < 0.5 || !SystemParameters.ClientAreaAnimation;
            lastRequest = finished ? target : position;
            viewer.ScrollToVerticalOffset(lastRequest.Value);
            if (finished) Stop(keepRequest: true);
        }

        private void Stop(bool keepRequest = false)
        {
            if (!keepRequest) lastRequest = null;
            if (!rendering) return;
            rendering = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }
}
