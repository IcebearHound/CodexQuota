using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Quota.Desktop;

internal static class WindowsTaskbarSettings
{
    private const string ExplorerSettings = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public static bool? WidgetsVisible => Read("TaskbarDa", 1) is { } value ? value != 0 : null;
    public static bool StartAlignedLeft => Read("TaskbarAl", 1) == 0;

    private static int? Read(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ExplorerSettings);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

}
