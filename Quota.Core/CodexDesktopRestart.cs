using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Quota.Core;

public static class CodexDesktopRestart
{
    internal static bool IsDesktopExecutable(string? path)
        => path is not null && path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase);
    public static async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")).ToArray();
        var targets = new List<Process>();
        string? executable = null;
        try
        {
            foreach (var process in processes)
            {
                string? path;
                try { path = process.MainModule?.FileName; } catch (Exception error) when (error is Win32Exception or InvalidOperationException) { continue; }
                if (!IsDesktopExecutable(path)) continue;
                executable = path; targets.Add(process);
            }
            if (executable is null) return false;
            // Resolve the registered entry before closing anything. Starting the EXE
            // directly bypasses packaged activation and can split taskbar grouping.
            var applicationId = FindApplicationId(executable);
            if (applicationId is null) return false;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var process in targets) { try { process.CloseMainWindow(); } catch (InvalidOperationException) { } }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try { await Task.WhenAll(targets.Select(process => process.WaitForExitAsync(deadline.Token))); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                foreach (var process in targets) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
                await Task.WhenAll(targets.Select(process => process.WaitForExitAsync(cancellationToken)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ActivateApplication(applicationId);
            return true;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static string? FindApplicationId(string executable)
    {
        if (!OperatingSystem.IsWindows() || !IsDesktopExecutable(executable)) return null;
        var root = Directory.GetParent(executable)?.Parent;
        if (root is null) return null;
        var manifestPath = Path.Combine(root.FullName, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var length = 0u;
            if (PackageFamilyNameFromFullName(root.Name, ref length, null) != 122 || length is 0 or > 1024) return null;
            var family = new StringBuilder((int)length);
            if (PackageFamilyNameFromFullName(root.Name, ref length, family) != 0) return null;
            using var reader = XmlReader.Create(manifestPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return ResolveApplicationId(XDocument.Load(reader), root.FullName, executable, family.ToString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException) { return null; }
    }

    internal static string? ResolveApplicationId(XDocument manifest, string packageRoot, string executable, string packageFamily)
    {
        XNamespace schema = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        if ((string?)manifest.Root?.Element(schema + "Identity")?.Attribute("Name") != "OpenAI.Codex") return null;
        foreach (var application in manifest.Root?.Element(schema + "Applications")?.Elements(schema + "Application") ?? [])
        {
            var relative = (string?)application.Attribute("Executable");
            var id = (string?)application.Attribute("Id");
            if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(id)) continue;
            var candidate = Path.GetFullPath(Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (candidate.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return packageFamily + "!" + id;
        }
        return null;
    }

    private static void ActivateApplication(string applicationId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            var result = manager.ActivateApplication(applicationId, null, 0, out var processId);
            if (result < 0) throw new Win32Exception(result, "Windows could not activate the registered Codex application.");
            if (processId == 0) throw new InvalidOperationException("Windows did not return an activated Codex process.");
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(string packageFullName, ref uint length, StringBuilder? family);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string applicationId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string applicationId, nint items, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string applicationId, nint items, out uint processId);
    }
}
