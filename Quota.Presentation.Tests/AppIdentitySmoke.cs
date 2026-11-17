using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Quota.Core;

internal static class AppIdentitySmoke
{
    public static void Run()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Quota.WidgetProvider/Package.appxmanifest"));
        var document = XDocument.Load(path);
        XNamespace schema = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var apps = document.Root!.Element(schema + "Applications")!.Elements(schema + "Application").ToArray();
        if (apps.Length != 1 || (string?)apps[0].Attribute("Id") != "Desktop" || (string?)apps[0].Attribute("Executable") != @"Desktop\CodexQuota.exe")
            throw new InvalidOperationException("The package must expose a single desktop application");
        if (apps[0].Descendants().Count(element => (string?)element.Attribute("Category") == "windows.comServer") != 1
            || apps[0].Descendants().Count(element => (string?)element.Attribute("Category") == "windows.appExtension") != 1)
            throw new InvalidOperationException("The desktop application must own its Widgets extensions");
        if (apps[0].Element(uap + "VisualElements")?.Attribute("DisplayName")?.Value != "Codex Quota")
            throw new InvalidOperationException("The Start menu must use the primary application name");
        CodexAppIdentity.Apply();
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var pointer));
        try
        {
            if (Marshal.PtrToStringUni(pointer) != CodexAppIdentity.AppUserModelId)
                throw new InvalidOperationException("The running process does not use the shared application identity");
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
