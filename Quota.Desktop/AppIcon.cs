using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Quota.Desktop;

public sealed class AppIcon : FrameworkElement
{
    private static readonly Dictionary<string, Geometry[]> Shapes = new();
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(AppIcon), new FrameworkPropertyMetadata("info", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Controls.Control.ForegroundProperty.AddOwner(typeof(AppIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public AppIcon() { UseLayoutRounding = true; RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified); Width = 16; Height = 16; IsHitTestVisible = false; VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Center; }
    protected override void OnRender(DrawingContext drawing)
    {
        if (!Shapes.TryGetValue(Kind, out var shapes))
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/CodexQuota;component/Assets/lucide/" + Kind + ".svg")).Stream;
            var document = XDocument.Load(stream);
            var paths = new List<Geometry>();
            foreach (var shape in document.Root!.Elements())
            {
                double Number(string name) => double.Parse(shape.Attribute(name)!.Value, CultureInfo.InvariantCulture);
                Geometry? geometry = shape.Name.LocalName switch
                {
                    "path" => Geometry.Parse(shape.Attribute("d")!.Value),
                    "circle" => new EllipseGeometry(new Point(Number("cx"), Number("cy")), Number("r"), Number("r")),
                    "line" => new LineGeometry(new Point(Number("x1"), Number("y1")), new Point(Number("x2"), Number("y2"))),
                    "polyline" or "polygon" => Geometry.Parse("M" + shape.Attribute("points")!.Value + (shape.Name.LocalName == "polygon" ? " Z" : "")),
                    "rect" => new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")), (double?)shape.Attribute("rx") ?? 0, (double?)shape.Attribute("rx") ?? 0),
                    _ => null
                };
                if (geometry is not null) { geometry.Freeze(); paths.Add(geometry); }
            }
            shapes = paths.ToArray(); Shapes.Add(Kind, shapes);
        }
        drawing.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        if (scale <= 0) { drawing.Pop(); return; }
        var thickness = Math.Max(1.5, Math.Round(1.8 * scale * dpi.DpiScaleX * 2) / 2) / (scale * dpi.DpiScaleX);
        var pen = new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var shape in shapes) drawing.DrawGeometry(null, pen, shape);
        drawing.Pop();
    }
}
