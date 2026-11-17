using System.Runtime.InteropServices;

namespace Quota.Core;

public static class CodexAppIdentity
{
    // Keep the registered package family and Desktop application ID stable across upgrades.
    public const string AppUserModelId = "CodexQuota.Widget_3f0p9vy5t317m!Desktop";
    public static void Apply()
    {
        if (OperatingSystem.IsWindows()) SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
