namespace Quota.Core;

public static class WidgetPlacement
{
    public static PixelRect Place(PixelRect workArea, int width, int height, double x, double y)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || width <= 0 || height <= 0) throw new ArgumentException("Positive work area and widget dimensions are required.");
        width = Math.Min(width, workArea.Width); height = Math.Min(height, workArea.Height);
        x = double.IsFinite(x) ? Math.Clamp(x, 0, 1) : 0.5;
        y = double.IsFinite(y) ? Math.Clamp(y, 0, 1) : 0.5;
        return new(workArea.Left + (int)Math.Round((workArea.Width - width) * x), workArea.Top + (int)Math.Round((workArea.Height - height) * y), width, height);
    }
    public static (double X, double Y) Normalize(PixelRect workArea, PixelRect widget)
        => (Math.Clamp((widget.Left - (double)workArea.Left) / Math.Max(1, workArea.Width - widget.Width), 0, 1),
            Math.Clamp((widget.Top - (double)workArea.Top) / Math.Max(1, workArea.Height - widget.Height), 0, 1));
}
