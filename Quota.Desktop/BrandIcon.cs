using System.Windows;
using System.Windows.Media;

namespace Quota.Desktop;

public sealed class BrandIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Controls.Control.ForegroundProperty.AddOwner(
        typeof(BrandIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public BrandIcon()
    {
        Width = Height = 24; UseLayoutRounding = true; IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        SetResourceReference(ForegroundProperty, "Accent");
    }
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var pen = new Pen(Foreground, 3.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.DrawEllipse(null, pen, new Point(11.2, 11.2), 7.2, 7.2);
        drawing.DrawLine(pen, new Point(14.1, 14.1), new Point(20, 20));
        drawing.Pop();
    }
}
