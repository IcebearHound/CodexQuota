using System.Windows;
using System.Windows.Media;

namespace Quota.Desktop;

public sealed class BrandIcon : FrameworkElement
{
    private static readonly Brush Color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 185, 170));
    public BrandIcon()
    {
        Width = Height = 24; UseLayoutRounding = true; IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var pen = new Pen(Color, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.DrawEllipse(null, pen, new Point(10.7, 10.7), 8.2, 8.2);
        drawing.DrawLine(pen, new Point(16.7, 16.7), new Point(21.1, 21.1));
        drawing.DrawLine(pen, new Point(10.7, 10.7), new Point(15.3, 6.1));
        drawing.Pop();
    }
}
