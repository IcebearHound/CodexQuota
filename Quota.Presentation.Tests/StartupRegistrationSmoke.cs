using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Quota.Desktop;

internal static class StartupRegistrationSmoke
{
    public static void Run()
    {
        var fixture = @"Software\CodexQuota\Tests\" + Guid.NewGuid().ToString("N");
        var runPath = fixture + @"\Run";
        var approvalPath = fixture + @"\StartupApproved";
        var type = typeof(MainWindow).Assembly.GetType("Quota.Desktop.StartupRegistration", true)!;
        var enabled = type.GetProperty("IsEnabled")!;
        var set = type.GetMethod("SetEnabled")!;
        var refresh = type.GetMethod("RefreshRegisteredPath")!;
        object Service(string executable, string? data = null, string? assembly = null)
            => Activator.CreateInstance(type, [executable, data, assembly, runPath, approvalPath])!;
        bool IsEnabled(object service) => (bool)enabled.GetValue(service)!;
        string Command()
        {
            using var key = Registry.CurrentUser.OpenSubKey(runPath);
            return (string)key!.GetValue("CodexQuota")!;
        }
        try
        {
            var current = type.GetMethod("ForCurrentProcess")!.Invoke(null, null)!;
            var currentCommand = (string)type.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
            var currentArguments = Arguments(currentCommand);
            Check(currentArguments[0] == Environment.ProcessPath && currentArguments.Contains("--background"), "current-process registration uses the running host and background entry point");
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                Check(currentArguments[1] == Path.GetFullPath(Environment.GetCommandLineArgs()[0]), "dotnet-hosted factory retains the managed entry assembly");
            var app = Service(@"C:\Program Files\CodexQuota\CodexQuota.exe");
            Check(!IsEnabled(app), "startup is off without a registration");
            using (var key = Registry.CurrentUser.CreateSubKey(runPath)) key.SetValue("OtherApp", "keep");
            set.Invoke(app, [true]); set.Invoke(app, [true]);
            Check(IsEnabled(app), "enable is idempotent");
            Check(Arguments(Command()).SequenceEqual([@"C:\Program Files\CodexQuota\CodexQuota.exe", "--background"]), "startup quotes the executable and launches in the background");
            using (var key = Registry.CurrentUser.CreateSubKey(approvalPath))
            {
                key.SetValue("CodexQuota", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                key.SetValue("OtherApp", new byte[] { 3 }, RegistryValueKind.Binary);
            }
            Check(!IsEnabled(app), "external startup-app disabling is reflected");
            var updated = Service(@"C:\Apps\CodexQuota rc31\CodexQuota.exe");
            refresh.Invoke(updated, null);
            Check(!IsEnabled(updated) && Arguments(Command())[0] == @"C:\Apps\CodexQuota rc31\CodexQuota.exe", "upgrades refresh the path while preserving external disabled state");
            set.Invoke(updated, [true]);
            Check(IsEnabled(updated), "enabling restores the application's startup approval");
            set.Invoke(updated, [false]); set.Invoke(updated, [false]);
            Check(!IsEnabled(updated), "disable is idempotent");
            using (var key = Registry.CurrentUser.OpenSubKey(runPath)) Check((string?)key!.GetValue("OtherApp") == "keep", "other startup commands are preserved");
            using (var key = Registry.CurrentUser.OpenSubKey(approvalPath)) Check(key!.GetValue("OtherApp") is byte[], "other startup approvals are preserved");
            var custom = Service(@"C:\Apps\CodexQuota.exe", @"C:\");
            set.Invoke(custom, [true]);
            Check(Arguments(Command()).SequenceEqual([@"C:\Apps\CodexQuota.exe", "--background", "--data-dir", @"C:\"]), "custom root directories retain their trailing slash after command-line parsing");
            refresh.Invoke(updated, null);
            Check(Arguments(Command()).Contains("--data-dir"), "default-profile upgrades preserve explicit custom-profile registrations");
            var managed = Service(@"C:\Program Files\dotnet\dotnet.exe", null, @"C:\My App\CodexQuota.dll");
            set.Invoke(managed, [true]);
            Check(Arguments(Command()).SequenceEqual([@"C:\Program Files\dotnet\dotnet.exe", @"C:\My App\CodexQuota.dll", "--background"]), "managed-host launches include the entry assembly");
            set.Invoke(managed, [false]);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(fixture, false); }
    }

    private static string[] Arguments(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == 0) throw new InvalidOperationException("Could not parse the startup command.");
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint value);
}
