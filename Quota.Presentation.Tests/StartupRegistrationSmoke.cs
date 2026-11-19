using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
using Quota.Desktop;

internal static class StartupRegistrationSmoke
{
    public static void Run()
    {
        var id = Guid.NewGuid().ToString("N");
        var fixture = @"Software\CodexQuota\Tests\" + id;
        var runPath = fixture + @"\Run";
        var approvalPath = fixture + @"\StartupApproved";
        var taskName = "CodexQuota test " + id;
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        dynamic Task() => folder.GetTask(taskName);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Definition() => XElement.Parse((string)Task().Xml);
        XElement Action() => Definition().Element(ns + "Actions")!.Element(ns + "Exec")!;
        string[] TaskArguments()
        {
            var action = Action();
            return Arguments($"\"{action.Element(ns + "Command")!.Value}\" {action.Element(ns + "Arguments")!.Value}");
        }
        var type = typeof(MainWindow).Assembly.GetType("Quota.Desktop.StartupRegistration", true)!;
        var enabled = type.GetProperty("IsEnabled")!;
        var set = type.GetMethod("SetEnabled")!;
        var refresh = type.GetMethod("RefreshRegisteredPath")!;
        var remove = type.GetMethod("RemoveForCurrentExecutable")!;
        object Service(string executable, string? data = null, string? assembly = null)
            => Activator.CreateInstance(type, [executable, data, assembly, runPath, approvalPath, taskName])!;
        bool IsEnabled(object service) => (bool)enabled.GetValue(service)!;
        string? LegacyCommand()
        {
            using var key = Registry.CurrentUser.OpenSubKey(runPath);
            return (string?)key?.GetValue("CodexQuota");
        }
        try
        {
            var current = type.GetMethod("ForCurrentProcess")!.Invoke(null, null)!;
            var currentCommand = (string)type.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
            var currentArguments = Arguments(currentCommand);
            Check(string.Equals(currentArguments[0], Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)
                && currentArguments.Contains("--background"), $"current-process registration uses the running host and background entry point: {currentCommand} (host: {Environment.ProcessPath})");
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                Check(currentArguments[1] == Path.GetFullPath(Environment.GetCommandLineArgs()[0]), "dotnet-hosted factory retains the managed entry assembly");
            var app = Service(@"C:\Program Files\CodexQuota\CodexQuota.exe");
            Check(!IsEnabled(app), "startup is off without a registration");
            using (var key = Registry.CurrentUser.CreateSubKey(runPath))
            {
                key.SetValue("OtherApp", "keep");
                key.SetValue("CodexQuota", "\"C:\\Old App\\CodexQuota.exe\" --background");
            }
            using (var key = Registry.CurrentUser.CreateSubKey(approvalPath))
            {
                key.SetValue("CodexQuota", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                key.SetValue("OtherApp", new byte[] { 3 }, RegistryValueKind.Binary);
            }
            Check(!IsEnabled(app), "legacy external disabling is reflected");
            var updated = Service(@"C:\Apps\CodexQuota rc31\CodexQuota.exe");
            refresh.Invoke(updated, [false]);
            Check(!IsEnabled(updated) && Arguments(LegacyCommand()!)[0] == @"C:\Apps\CodexQuota rc31\CodexQuota.exe", "upgrades do not migrate disabled legacy registrations");
            using (var key = Registry.CurrentUser.OpenSubKey(approvalPath, true)) key!.DeleteValue("CodexQuota");
            Check(IsEnabled(updated), "an enabled legacy registration remains enabled before migration");
            refresh.Invoke(app, [true]);
            Check(LegacyCommand() is not null, "install does not claim a legacy registration pointing at another copy");
            refresh.Invoke(app, [false]);
            Check(IsEnabled(app) && LegacyCommand() is null, "migration replaces the enabled Run entry with a task");
            Check(TaskArguments().SequenceEqual([@"C:\Program Files\CodexQuota\CodexQuota.exe", "--background"]), "the task starts the spaced executable path in the background");
            var definition = Definition();
            var logon = definition.Element(ns + "Triggers")!.Elements().Single();
            using var identity = WindowsIdentity.GetCurrent();
            var loggedOnUser = logon.Element(ns + "UserId")?.Value;
            Check(logon.Name == ns + "LogonTrigger" && (loggedOnUser == identity.User!.Value
                || string.Equals(loggedOnUser, identity.Name, StringComparison.OrdinalIgnoreCase))
                && (logon.Element(ns + "Delay")?.Value is null or "PT0S"), $"the current user's logon triggers the task without delay: {logon}");
            dynamic principal = Task().Definition.Principal;
            Check((int)principal.LogonType == 3 && (int)principal.RunLevel == 0, "the tray task uses the interactive session and does not elevate");
            var settings = definition.Element(ns + "Settings")!;
            Check(settings.Element(ns + "ExecutionTimeLimit")?.Value == "PT0S"
                && settings.Element(ns + "DisallowStartIfOnBatteries")?.Value == "false"
                && settings.Element(ns + "StopIfGoingOnBatteries")?.Value == "false", "tray startup works on battery and stays alive across days");
            Task().Enabled = false;
            refresh.Invoke(updated, [false]);
            Check(!IsEnabled(updated) && TaskArguments()[0] == @"C:\Apps\CodexQuota rc31\CodexQuota.exe", "path refresh preserves externally disabled tasks");
            set.Invoke(updated, [true]); set.Invoke(updated, [true]);
            Check(IsEnabled(updated), "enabling is idempotent and explicitly restores a disabled task");
            remove.Invoke(app, null);
            Check(IsEnabled(updated), "uninstall leaves another copy's registration intact");
            remove.Invoke(updated, null);
            set.Invoke(updated, [false]); set.Invoke(updated, [false]);
            Check(!IsEnabled(updated), "disable is idempotent");
            using (var key = Registry.CurrentUser.OpenSubKey(runPath)) Check((string?)key!.GetValue("OtherApp") == "keep", "other startup commands are preserved");
            using (var key = Registry.CurrentUser.OpenSubKey(approvalPath)) Check(key!.GetValue("OtherApp") is byte[], "other startup approvals are preserved");
            var custom = Service(@"C:\Apps\CodexQuota.exe", @"C:\");
            set.Invoke(custom, [true]);
            Check(TaskArguments().SequenceEqual([@"C:\Apps\CodexQuota.exe", "--background", "--data-dir", @"C:\"]), "custom root directories retain their trailing slash after command-line parsing");
            refresh.Invoke(updated, [false]);
            Check(TaskArguments().Contains("--data-dir"), "default-profile upgrades preserve explicit custom-profile registrations");
            var managed = Service(@"C:\Program Files\dotnet\dotnet.exe", null, @"C:\My App\CodexQuota.dll");
            set.Invoke(managed, [true]);
            Check(TaskArguments().SequenceEqual([@"C:\Program Files\dotnet\dotnet.exe", @"C:\My App\CodexQuota.dll", "--background"]), "managed-host launches include the entry assembly");
            set.Invoke(managed, [false]);
            Console.WriteLine("PASS login task registration, migration, enable/disable, upgrade and uninstall");
        }
        finally
        {
            try { folder.DeleteTask(taskName, 0); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { }
            finally { Registry.CurrentUser.DeleteSubKeyTree(fixture, false); }
        }
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
