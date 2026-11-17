using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Quota.Core;

internal static class AppIdentitySmoke
{
    public static void Run()
    {
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
