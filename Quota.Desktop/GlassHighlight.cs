using System;
using System.Windows;
using System.Windows.Media;

namespace Quota.Desktop;

internal sealed class GlassHighlight : FrameworkElement
{
    internal GlassHighlight() { IsHitTestVisible = false; Focusable = false; }
    protected override void OnRender(DrawingContext drawing)
    {
        if (ActualWidth < 4 || ActualHeight < 4) return;
        var bounds = new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1);
        var radius = WindowSurface.CornerRadius;
        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        var reflection = new RadialGradientBrush
        {
            Center = new Point(0.05, 0), GradientOrigin = new Point(0, 0),
            RadiusX = 0.95, RadiusY = 0.65,
            GradientStops = new()
            {
                new(Color.FromArgb(ThemeService.IsLight ? (byte)110 : (byte)55, 255, 255, 255), 0),
                new(Colors.Transparent, 1)
            }
        };
        drawing.DrawRectangle(reflection, null, bounds);
        var rim = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new()
            {
                new(Color.FromArgb(245, 255, 255, 255), 0),
                new(Color.FromArgb(35, 255, 255, 255), 0.34),
                new(Color.FromArgb(10, 255, 255, 255), 0.56),
                new(Color.FromArgb(170, 255, 255, 255), 1)
            }
        };
        drawing.DrawRoundedRectangle(null, new Pen(rim, 1.2), bounds, radius, radius);
        var inner = new Rect(2, 2, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 4));
        drawing.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(ThemeService.IsLight ? (byte)65 : (byte)35, 255, 255, 255)), 0.7), inner, Math.Max(0, radius - 2), Math.Max(0, radius - 2));
        drawing.Pop();
    }
}
