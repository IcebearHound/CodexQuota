using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Quota.Desktop;
internal static class ThemeService
{
    public static void Apply(string preference)
    {
        var light = preference == "light" || preference == "system" && SystemIsLight();
        var colors = light
            ? new Dictionary<string, string> { ["Background"] = "#F4F7F6", ["Surface"] = "#FFFFFF", ["BorderBrush"] = "#DCE5E2", ["Text"] = "#192F29", ["Muted"] = "#647B72", ["Accent"] = "#197A5F", ["Track"] = "#DFEAE5", ["ButtonFill"] = "#E5EEEA", ["InputFill"] = "#FFFFFF" }
            : new Dictionary<string, string> { ["Background"] = "#10171C", ["Surface"] = "#192329", ["BorderBrush"] = "#2B383F", ["Text"] = "#F0F5F4", ["Muted"] = "#93A6AD", ["Accent"] = "#84E1BF", ["Track"] = "#304048", ["ButtonFill"] = "#25333A", ["InputFill"] = "#10191E" };
        colors["Error"] = light ? "#B3261E" : "#FFB4AB";
        colors["SelectedFill"] = light ? "#D2E9DE" : "#263E35";
        foreach (var pair in colors) System.Windows.Application.Current.Resources[pair.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
    }
    private static bool SystemIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }
}
