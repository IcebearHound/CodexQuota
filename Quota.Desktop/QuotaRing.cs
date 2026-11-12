using System;
using System.Windows;
using System.Windows.Media;

namespace Quota.Desktop;

public sealed class QuotaRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(QuotaRing), new FrameworkPropertyMetadata(Brushes.MediumAquamarine, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(QuotaRing), new FrameworkPropertyMetadata(Brushes.DarkSlateGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(QuotaRing), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var explicitStroke = double.IsFinite(StrokeThickness) && StrokeThickness > 0;
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - (explicitStroke ? StrokeThickness / 2 + 1 : 6));
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = explicitStroke ? StrokeThickness : Math.Max(5, radius * 0.095);
        drawing.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);
        var value = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) : 0;
        if (value <= 0 || radius <= 0) return;
        if (value >= 100) { drawing.DrawEllipse(null, new Pen(Foreground, thickness), center, radius, radius); return; }
        var angle = value / 100 * Math.PI * 2;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
        }
        var pen = new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawing.DrawGeometry(null, pen, geometry);
    }
}
