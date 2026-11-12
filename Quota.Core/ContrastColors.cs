namespace Quota.Core;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public static class ContrastColors
{
    private static readonly RgbColor Black = new(0, 0, 0);
    private static readonly RgbColor White = new(255, 255, 255);
    public static double Luminance(RgbColor color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.Red) + 0.7152 * Linear(color.Green) + 0.0722 * Linear(color.Blue);
    }
    public static double Contrast(RgbColor first, RgbColor second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    public static RgbColor TextOn(RgbColor background) => Contrast(Black, background) >= Contrast(White, background) ? Black : White;
    public static RgbColor TextOn(RgbColor background, RgbColor? previous)
        => previous is { } color && Contrast(color, background) >= 4.5 ? color : TextOn(background);

    public static RgbColor? AverageBackground(IEnumerable<RgbColor> samples)
    {
        var colors = samples.OrderBy(Luminance).ToArray();
        if (colors.Length == 0) return null;
        var trim = colors.Length / 5;
        var middle = colors.Skip(trim).Take(colors.Length - 2 * trim).ToArray();
        return new((byte)Math.Round(middle.Average(color => color.Red)), (byte)Math.Round(middle.Average(color => color.Green)), (byte)Math.Round(middle.Average(color => color.Blue)));
    }

    public static RgbColor AccentOn(RgbColor source, RgbColor background, double minimumContrast = 3)
    {
        minimumContrast = Math.Clamp(minimumContrast, 1, 4.5);
        var (hue, saturation, lightness) = ToHsl(source);
        var (backgroundHue, backgroundSaturation, _) = ToHsl(background);
        var distance = (hue - backgroundHue + 540) % 360 - 180;
        if (saturation > 0.15 && backgroundSaturation > 0.15 && Math.Abs(distance) < 24)
            hue = (hue + (distance >= 0 ? 6 : -6) + 360) % 360;
        var adjusted = FromHsl(hue, saturation, lightness);
        if (Contrast(adjusted, background) >= minimumContrast) return adjusted;
        var dark = Contrast(Black, background) >= Contrast(White, background);
        var valid = dark ? 0d : 1d;
        var invalid = lightness;
        // Find the closest tone with sufficient contrast while retaining its hue and saturation.
        for (var step = 0; step < 24; step++)
        {
            var middle = (valid + invalid) / 2;
            if (Contrast(FromHsl(hue, saturation, middle), background) >= minimumContrast) valid = middle;
            else invalid = middle;
        }
        return FromHsl(hue, saturation, valid);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(RgbColor color)
    {
        var red = color.Red / 255d; var green = color.Green / 255d; var blue = color.Blue / 255d;
        var maximum = Math.Max(red, Math.Max(green, blue)); var minimum = Math.Min(red, Math.Min(green, blue));
        var delta = maximum - minimum; var lightness = (maximum + minimum) / 2;
        if (delta == 0) return (0, 0, lightness);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        var hue = maximum == red ? (green - blue) / delta : maximum == green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
        return ((hue * 60 + 360) % 360, saturation, lightness);
    }
    private static RgbColor FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var secondary = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var match = lightness - chroma / 2;
        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, secondary, 0d), < 120 => (secondary, chroma, 0d),
            < 180 => (0d, chroma, secondary), < 240 => (0d, secondary, chroma),
            < 300 => (secondary, 0d, chroma), _ => (chroma, 0d, secondary)
        };
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
        return new(Channel(red + match), Channel(green + match), Channel(blue + match));
    }
}
