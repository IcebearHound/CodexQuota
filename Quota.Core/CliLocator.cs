using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Quota.Core;

public static class CliLocator
{
    public static string? Find(string? preferred = null)
    {
        var explicitPath = ValidPath(preferred) ?? ValidPath(Environment.GetEnvironmentVariable("CODEX_CLI_PATH"));
        if (explicitPath is not null) return explicitPath;

        // A running desktop app has the current package version even if this process inherited an old PATH.
        foreach (var processName in new[] { "codex", "ChatGPT" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        var directory = Path.GetDirectoryName(executable);
                        if (directory is null) continue;
                        var candidate = processName == "codex" ? executable : Path.Combine(directory, "resources", "codex.exe");
                        if (ValidPath(candidate) is { } found) return found;
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var package in InstalledPackagePaths())
                if (ValidPath(Path.Combine(package, "app", "resources", "codex.exe")) is { } found) return found;
        }

        var paths = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            paths.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        foreach (var directory in paths.SelectMany(path => (path ?? "").Split(Path.PathSeparator)).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (ValidPath(Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), "codex.exe")) is { } found) return found; }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string? ValidPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static IEnumerable<string> InstalledPackagePaths()
    {
        const string family = "OpenAI.Codex_2p2nqsd0c76g0";
        uint count = 0, bufferLength = 0;
        if (GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero) != 122 || count == 0) yield break;
        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        var found = new List<string>();
        try
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref bufferLength, buffer) == 0)
            {
                for (var index = 0; index < count; index++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                    if (name is null) continue;
                    uint length = 0;
                    if (GetPackagePathByFullName(name, ref length, null) != 122) continue;
                    var path = new StringBuilder((int)length);
                    if (GetPackagePathByFullName(name, ref length, path) == 0) found.Add(path.ToString());
                }
            }
        }
        finally { Marshal.FreeHGlobal(names); Marshal.FreeHGlobal(buffer); }
        foreach (var path in found) yield return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr fullNames, ref uint bufferLength, IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder? path);
}
