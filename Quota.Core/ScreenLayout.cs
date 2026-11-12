namespace Quota.Core;

// All values in this contract are physical pixels, including negative monitor origins.
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public long Right => (long)Left + Width;
    public long Bottom => (long)Top + Height;
    public double CenterX => Left + Width / 2d;
    public double CenterY => Top + Height / 2d;
}

public static class ScreenLayout
{
    public static PixelRect EnsureVisible(PixelRect window, IEnumerable<PixelRect> workAreas)
    {
        if (window.Width <= 0 || window.Height <= 0) throw new ArgumentException("Window size must be positive.", nameof(window));
        var areas = workAreas.Where(a => a.Width > 0 && a.Height > 0).ToArray();
        if (areas.Length == 0) throw new ArgumentException("At least one display work area is required.", nameof(workAreas));
        var selected = areas.OrderByDescending(area => IntersectionArea(window, area)).ThenBy(area => DistanceSquared(window, area)).First();
        var width = Math.Min(window.Width, selected.Width); var height = Math.Min(window.Height, selected.Height);
        var left = Math.Clamp((long)window.Left, selected.Left, selected.Right - width);
        var top = Math.Clamp((long)window.Top, selected.Top, selected.Bottom - height);
        return new((int)left, (int)top, width, height);
    }
    private static long IntersectionArea(PixelRect a, PixelRect b)
        => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static double DistanceSquared(PixelRect a, PixelRect b)
    {
        var x = Math.Clamp(a.CenterX, b.Left, b.Right); var y = Math.Clamp(a.CenterY, b.Top, b.Bottom);
        return Math.Pow(a.CenterX - x, 2) + Math.Pow(a.CenterY - y, 2);
    }
}
