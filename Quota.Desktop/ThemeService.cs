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
        new("monochrome", "黑白", "#24262B", "#F0F1F3", "#F3F4F6", "#141518", "#202126", "#191B1F", "#555A64", "#B8BDC7"),
        new("mint", "薄荷", "#008560", "#38E8AE", "#E5F7EF", "#071E19", "#10352B", "#102F27", "#46675B", "#A0CDBD"),
        new("ocean", "海蓝", "#2161E8", "#63ADFF", "#E8F1FF", "#08182F", "#122D4C", "#142C4A", "#506780", "#ABC5E5"),
        new("violet", "紫罗兰", "#7C3AED", "#C08BFF", "#F1EAFF", "#1C112E", "#332047", "#322047", "#6B587F", "#CDB5E5"),
        new("amber", "琥珀", "#B45A08", "#FFC14F", "#FFF1D8", "#271B0D", "#44301A", "#443014", "#79603E", "#DEC39A")
    ];
    internal static bool GlassRequested { get; private set; }
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

    public static void ApplyAppearance(string preference, string palette, string material)
    {
        var light = preference == "light" || preference == "system" && SystemIsLight();
        var preset = Array.Find(Palettes, candidate => candidate.Key == palette) ?? Palettes[0];
        var glass = material == "glass" && !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        var key = $"{light}/{preset.Key}/{glass}";
        if (key == applied) return;
        applied = key; IsLight = light; GlassRequested = glass;
        AccentColor = light ? preset.LightAccent : preset.DarkAccent;
        SecondaryColor = light ? preset.LightMuted : preset.DarkMuted;
        var accent = Parse(AccentColor);
        var background = Parse(light ? preset.LightBackground : preset.DarkBackground);
        var surface = light ? Colors.White : Parse(preset.DarkSurface);
        OpaqueBackground = Brush(background); OpaqueSurface = Brush(surface);
        PanelBackground = OpaqueBackground;
        // A single flat tint lets the scene through while keeping text legible
        // when a light panel sits over dark content (or vice versa).
        var frost = light ? Parse("#F6F7F9") : Parse("#17191E");
        GlassBackground = Brush(Tint(frost, light ? (byte)207 : (byte)211));
        var colors = new Dictionary<string, Color>
        {
            ["Background"] = background, ["Surface"] = surface,
            ["BorderBrush"] = Mix(surface, accent, light ? 0.12 : 0.15),
            ["Text"] = Parse(light ? preset.LightText : preset.Key == "monochrome" ? "#F5F5F5" : "#F8FAFF"), ["Muted"] = Parse(SecondaryColor),
            ["Accent"] = accent, ["Track"] = Mix(surface, accent, light ? 0.13 : 0.18),
            ["ButtonFill"] = Mix(surface, accent, light ? 0.045 : 0.08), ["InputFill"] = surface,
            ["HoverFill"] = Mix(surface, accent, light ? 0.14 : 0.18),
            ["Error"] = Parse(DangerColor),
            ["ToggleThumb"] = light ? Colors.White : Colors.Black,
            ["SelectedFill"] = Mix(surface, accent, light ? 0.10 : 0.16),
            ["SidebarFill"] = Mix(background, surface, light ? 0.62 : 0.31)
        };
        var resources = System.Windows.Application.Current.Resources;
        foreach (var pair in colors) resources[pair.Key] = Brush(pair.Value);
        resources["MenuSurface"] = OpaqueSurface;
        resources["FlyoutCardSurface"] = OpaqueSurface;
        resources["FlyoutCardBorder"] = Brush(Mix(surface, accent, light ? 0.10 : 0.18));
        if (glass)
        {
            var sheet = light ? Colors.White : Parse("#24262C");
            var ink = light ? Parse("#252832") : Colors.White;
            resources["Background"] = GlassBackground;
            resources["Surface"] = Brush(Tint(sheet, light ? (byte)104 : (byte)132));
            resources["SidebarFill"] = Brush(Tint(sheet, light ? (byte)55 : (byte)90));
            resources["FlyoutCardSurface"] = resources["Surface"];
            resources["FlyoutCardBorder"] = Brush(Tint(ink, light ? (byte)26 : (byte)44));
            resources["BorderBrush"] = Brush(Tint(ink, light ? (byte)30 : (byte)46));
            resources["ButtonFill"] = Brush(Tint(Mix(sheet, ink, 0.06), light ? (byte)155 : (byte)185));
            resources["HoverFill"] = Brush(Tint(Mix(sheet, ink, 0.15), 205));
            resources["SelectedFill"] = Brush(Tint(Mix(sheet, accent, 0.12), 235));
            resources["InputFill"] = Brush(Tint(sheet, light ? (byte)190 : (byte)220));
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
