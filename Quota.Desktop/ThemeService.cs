using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Quota.Desktop;
internal static class ThemeService
{
    internal sealed record PalettePreset(string Key, string Name, string LightAccent, string DarkAccent);
    internal static readonly PalettePreset[] Palettes =
    [
        new("mint", "薄荷", "#197A5F", "#84E1BF"),
        new("ocean", "海蓝", "#1766B5", "#83C7FF"),
        new("violet", "紫罗兰", "#7450AE", "#CEADFF"),
        new("amber", "琥珀", "#88600F", "#F2CC7B")
    ];
    internal static bool GlassRequested { get; private set; }
    internal static bool IsLight { get; private set; }
    internal static SolidColorBrush OpaqueBackground { get; private set; } = new(Color.FromRgb(16, 23, 28));
    internal static SolidColorBrush OpaqueSurface { get; private set; } = new(Color.FromRgb(25, 35, 41));
    private static string? applied;

    public static void Apply(string preference) => ApplyAppearance(preference, "mint", "solid");

    public static void ApplyAppearance(string preference, string palette, string material)
    {
        var light = preference == "light" || preference == "system" && SystemIsLight();
        var preset = Array.Find(Palettes, candidate => candidate.Key == palette) ?? Palettes[0];
        var glass = material == "glass" && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var key = $"{light}/{preset.Key}/{glass}";
        if (key == applied) return;
        applied = key; IsLight = light; GlassRequested = glass;
        var accent = Parse(light ? preset.LightAccent : preset.DarkAccent);
        var background = Mix(Parse(light ? "#F4F7F6" : "#10171C"), accent, preset.Key == "mint" ? 0 : 0.09);
        var surface = Mix(Parse(light ? "#FFFFFF" : "#192329"), accent, preset.Key == "mint" ? 0 : 0.08);
        OpaqueBackground = Brush(background); OpaqueSurface = Brush(surface);
        var colors = new Dictionary<string, Color>
        {
            ["Background"] = background, ["Surface"] = surface,
            ["BorderBrush"] = Mix(surface, accent, light ? 0.23 : 0.19),
            ["Text"] = Parse(light ? "#192F29" : "#F0F5F4"), ["Muted"] = Parse(light ? "#526B64" : "#A2B5BB"),
            ["Accent"] = accent, ["Track"] = Mix(surface, accent, 0.16),
            ["ButtonFill"] = Mix(surface, accent, light ? 0.09 : 0.08), ["InputFill"] = surface,
            ["Error"] = Parse(light ? "#B3261E" : "#FFB4AB"),
            ["SelectedFill"] = Mix(surface, accent, light ? 0.18 : 0.16)
        };
        var resources = System.Windows.Application.Current.Resources;
        foreach (var pair in colors) resources[pair.Key] = Brush(pair.Value);
        resources["MenuSurface"] = OpaqueSurface;
        if (glass)
        {
            resources["Background"] = new LinearGradientBrush(Tint(background, 180), Tint(background, 145), 70);
            resources["Surface"] = Brush(Tint(surface, 110));
            resources["BorderBrush"] = new LinearGradientBrush(Tint(light ? Colors.White : accent, 140), Tint(accent, 45), 65);
            resources["InputFill"] = Brush(Tint(surface, 185));
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
    private static bool SystemIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
