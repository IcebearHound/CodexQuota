namespace Quota.Core;

public static class TaskbarLayout
{
    public static PixelRect? FindAutomaticSpace(PixelRect taskbar, double scale, bool startAlignedLeft, PixelRect? widgets, PixelRect? tray, IEnumerable<PixelRect> occupied, double desiredWidth = 44, bool fillHeight = false, string placement = "auto")
    {
        var candidate = Place(taskbar, scale, 0, desiredWidth, fillHeight);
        var gap = (int)Math.Ceiling(8 * scale);
        var obstacles = occupied.Where(rect => rect.Width > 0 && rect.Height > 0 && rect.Top < taskbar.Bottom && rect.Bottom > taskbar.Top).ToArray();
        var rightAligned = placement == "right" || placement != "left" && startAlignedLeft;
        if (!rightAligned)
        {
            var offset = widgets is { Width: > 0, Height: > 0 } widget && widget.CenterX < taskbar.CenterX && widget.Top < taskbar.Bottom && widget.Bottom > taskbar.Top
                ? Math.Max(0, widget.Right - taskbar.Left + gap) / scale : 0;
            return FindSpace(taskbar, scale, offset, obstacles, desiredWidth, fillHeight);
        }
        var right = tray is { Width: > 0, Height: > 0 } area && area.Top < taskbar.Bottom && area.Bottom > taskbar.Top
            ? Math.Clamp((long)area.Left, taskbar.Left, taskbar.Right) : taskbar.Right;
        candidate = candidate with { Left = (int)Math.Max(taskbar.Left, right - gap - candidate.Width) };
        foreach (var obstacle in obstacles.OrderByDescending(rect => rect.Right))
        {
            if (candidate.Right + gap <= obstacle.Left || candidate.Left >= obstacle.Right + gap) continue;
            var left = (long)obstacle.Left - gap - candidate.Width;
            if (left < taskbar.Left) return null;
            candidate = candidate with { Left = (int)left };
        }
        return candidate.Right + gap <= right ? candidate : null;
    }

    public static bool CoversMonitor(PixelRect window, PixelRect monitor)
        => monitor.Width > 0 && monitor.Height > 0 && window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    public static PixelRect PlaceFlyout(PixelRect taskbar, PixelRect anchor, PixelRect workArea, double scale, double preferredHeight)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentException("A visible work area is required.", nameof(workArea));
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!double.IsFinite(preferredHeight) || preferredHeight <= 0) throw new ArgumentOutOfRangeException(nameof(preferredHeight));
        var gap = Math.Min((int)Math.Ceiling(8 * scale), (workArea.Height - 1) / 2);
        var width = (int)Math.Min(Math.Ceiling(332 * scale), workArea.Width);
        var height = (int)Math.Min(Math.Ceiling(preferredHeight * scale), workArea.Height - gap * 2);
        var left = Math.Clamp((long)anchor.Left, workArea.Left, workArea.Right - width);
        var top = taskbar.CenterY <= workArea.CenterY ? taskbar.Bottom + gap : taskbar.Top - height - (long)gap;
        top = Math.Clamp(top, workArea.Top + gap, workArea.Bottom - height - gap);
        return new((int)left, (int)top, width, height);
    }

    public static PixelRect? FindSpace(PixelRect taskbar, double scale, double offset, IEnumerable<PixelRect> occupied, double desiredWidth = 44, bool fillHeight = false)
    {
        var candidate = Place(taskbar, scale, offset, desiredWidth, fillHeight);
        var gap = (int)Math.Ceiling(8 * scale);
        foreach (var obstacle in occupied.Where(rect => rect.Width > 0 && rect.Height > 0 && rect.Top < taskbar.Bottom && rect.Bottom > taskbar.Top).OrderBy(rect => rect.Left))
        {
            if (candidate.Right + gap <= obstacle.Left || candidate.Left >= obstacle.Right + gap) continue;
            var next = obstacle.Right + gap;
            if (next + candidate.Width > taskbar.Right) return null;
            candidate = candidate with { Left = (int)next };
        }
        return candidate.Right <= taskbar.Right ? candidate : null;
    }

    public static PixelRect Place(PixelRect taskbar, double scale, double offset, double desiredWidth = 44, bool fillHeight = false)
    {
        if (taskbar.Width <= 0 || taskbar.Height <= 0 || taskbar.Height > taskbar.Width) throw new ArgumentException("A horizontal taskbar is required.", nameof(taskbar));
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!double.IsFinite(desiredWidth) || desiredWidth <= 0) throw new ArgumentOutOfRangeException(nameof(desiredWidth));
        var margin = fillHeight ? 0 : Math.Min((int)Math.Round(4 * scale), Math.Max(0, (taskbar.Height - 1) / 2));
        var width = (int)Math.Max(1, Math.Min(Math.Round(desiredWidth * scale), taskbar.Width));
        var maximum = taskbar.Width - width;
        var left = (int)Math.Clamp(double.IsFinite(offset) ? Math.Round(offset * scale) : 0, 0, maximum);
        return new(taskbar.Left + left, taskbar.Top + margin, width, taskbar.Height - margin * 2);
    }
}
