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
                new(Color.FromArgb(ThemeService.TransparentGlass ? (byte)24 : (byte)42, 255, 255, 255), 0),
                new(Colors.Transparent, 1)
            }
        };
        drawing.DrawRectangle(reflection, null, bounds);
        drawing.Pop();
    }
}
