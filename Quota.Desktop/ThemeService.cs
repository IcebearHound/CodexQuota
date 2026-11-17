using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Quota.Desktop;
internal static class ThemeService
{
    internal sealed record PalettePreset(string Key, string Name, string LightAccent, string DarkAccent,
        string LightBackground, string DarkBackground, string DarkSurface, string LightText, string LightMuted, string DarkMuted);
    internal static readonly PalettePreset[] Palettes =
    [
        new("monochrome", "黑白", "#242424", "#F0F0F0", "#F3F3F3", "#111111", "#202020", "#181818", "#626262", "#B7B7B7"),
        new("mint", "薄荷", "#008560", "#38E8AE", "#E5F7EF", "#071E19", "#10352B", "#102F27", "#46675B", "#A0CDBD"),
        new("ocean", "海蓝", "#2161E8", "#63ADFF", "#E8F1FF", "#08182F", "#122D4C", "#142C4A", "#506780", "#ABC5E5"),
        new("violet", "紫罗兰", "#7C3AED", "#C08BFF", "#F1EAFF", "#1C112E", "#332047", "#322047", "#6B587F", "#CDB5E5"),
        new("amber", "琥珀", "#B45A08", "#FFC14F", "#FFF1D8", "#271B0D", "#44301A", "#443014", "#79603E", "#DEC39A")
    ];
    internal static bool GlassRequested { get; private set; }
    internal static bool TransparentGlass { get; private set; }
    internal static bool IsLight { get; private set; }
    internal static SolidColorBrush OpaqueBackground { get; private set; } = new(Color.FromRgb(17, 17, 17));
    internal static SolidColorBrush OpaqueSurface { get; private set; } = new(Color.FromRgb(32, 32, 32));
    internal static Brush PanelBackground { get; private set; } = OpaqueBackground;
    internal static Brush GlassBackground { get; private set; } = Brushes.Transparent;
    internal static string AccentColor { get; private set; } = "#F0F0F0";
    internal static string WarningColor => IsLight ? "#A65D00" : "#FFC14F";
    internal static string DangerColor => IsLight ? "#DC3545" : "#FF7180";
    internal static string SecondaryColor { get; private set; } = "#B7B7B7";
    private static string? applied;

    public static void Apply(string preference) => ApplyAppearance(preference, "monochrome", "solid");

    public static void ApplyAppearance(string preference, string palette, string material, bool transparentGlass = false)
    {
        var light = preference == "light" || preference == "system" && SystemIsLight();
        var preset = Array.Find(Palettes, candidate => candidate.Key == palette) ?? Palettes[0];
        var glass = material == "glass" && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var clear = glass && transparentGlass;
        var key = $"{light}/{preset.Key}/{glass}/{clear}";
        if (key == applied) return;
        applied = key; IsLight = light; GlassRequested = glass; TransparentGlass = clear;
        AccentColor = light ? preset.LightAccent : preset.DarkAccent;
        SecondaryColor = light ? preset.LightMuted : preset.DarkMuted;
        var accent = Parse(AccentColor);
        var background = Parse(light ? preset.LightBackground : preset.DarkBackground);
        var surface = light ? Colors.White : Parse(preset.DarkSurface);
        OpaqueBackground = Brush(background); OpaqueSurface = Brush(surface);
        PanelBackground = Gradient((background, 0), (Mix(background, surface, 0.52), 0.6), (background, 1));
        var topAlpha = clear ? (light ? (byte)52 : (byte)110) : (light ? (byte)112 : (byte)180);
        var middleAlpha = clear ? (light ? (byte)16 : (byte)52) : (light ? (byte)36 : (byte)92);
        var bottomAlpha = clear ? (light ? (byte)42 : (byte)88) : (light ? (byte)76 : (byte)150);
        GlassBackground = Gradient(
            (Tint(light ? Colors.White : background, topAlpha), 0),
            (Tint(light ? background : surface, middleAlpha), 0.48),
            (Tint(light ? Colors.White : background, bottomAlpha), 1));
        var colors = new Dictionary<string, Color>
        {
            ["Background"] = background, ["Surface"] = surface,
            ["BorderBrush"] = Mix(surface, accent, light ? 0.19 : 0.26),
            ["Text"] = Parse(light ? preset.LightText : preset.Key == "monochrome" ? "#F5F5F5" : "#F8FAFF"), ["Muted"] = Parse(SecondaryColor),
            ["Accent"] = accent, ["Track"] = Mix(surface, accent, light ? 0.13 : 0.18),
            ["ButtonFill"] = Mix(surface, accent, light ? 0.07 : 0.10), ["InputFill"] = surface,
            ["HoverFill"] = Mix(surface, accent, light ? 0.14 : 0.18),
            ["Error"] = Parse(DangerColor),
            ["SelectedFill"] = Mix(surface, accent, light ? 0.20 : 0.25)
        };
        var resources = System.Windows.Application.Current.Resources;
        foreach (var pair in colors) resources[pair.Key] = Brush(pair.Value);
        resources["MenuSurface"] = OpaqueSurface;
        resources["FlyoutCardSurface"] = OpaqueSurface;
        resources["FlyoutCardBorder"] = Brush(Mix(surface, accent, light ? 0.10 : 0.18));
        if (glass)
        {
            resources["Background"] = GlassBackground;
            resources["Surface"] = Brush(Tint(light ? Colors.White : background, clear ? (byte)82 : light ? (byte)138 : (byte)125));
            resources["FlyoutCardSurface"] = Brush(Tint(light ? Colors.White : background, clear ? (byte)58 : (byte)88));
            resources["FlyoutCardBorder"] = Brush(Tint(Colors.White, light ? (byte)45 : (byte)28));
            resources["BorderBrush"] = Gradient((Tint(Colors.White, 155), 0), (Tint(accent, 35), 0.5), (Tint(Colors.White, 90), 1));
            resources["HoverFill"] = Brush(Tint(light ? Colors.White : surface, 90));
            resources["InputFill"] = Brush(Tint(surface, light ? (byte)190 : (byte)220));
        }
        foreach (Window window in System.Windows.Application.Current.Windows) WindowSurface.ApplyMaterial(window);
    }
    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    private static Color Mix(Color source, Color target, double amount) => Color.FromRgb(
        (byte)Math.Round(source.R + (target.R - source.R) * amount),
        (byte)Math.Round(source.G + (target.G - source.G) * amount),
        (byte)Math.Round(source.B + (target.B - source.B) * amount));
    private static Color Tint(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static LinearGradientBrush Gradient(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.8, 1) };
        foreach (var stop in stops) brush.GradientStops.Add(new(stop.Color, stop.Offset));
        brush.Freeze(); return brush;
    }
    private static bool SystemIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
