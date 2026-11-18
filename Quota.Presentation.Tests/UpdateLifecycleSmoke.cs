using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Quota.Core;

internal static class UpdateLifecycleSmoke
{
    public static async Task<int> Run(string[] args)
    {
        var directory = Path.GetFullPath(args[1]); var app = Path.GetFullPath(args[2]);
        var options = args[3..].ToList();
        var fromIndex = options.IndexOf("--from");
        if (fromIndex >= 0 && fromIndex + 1 >= options.Count) throw new ArgumentException("--from requires the previous portable executable.");
        var previous = fromIndex >= 0 ? Path.GetFullPath(options[fromIndex + 1]) : app;
        if (fromIndex >= 0) options.RemoveRange(fromIndex, 2);
        if (Directory.Exists(directory)) throw new ArgumentException("Choose a fresh lifecycle fixture directory.");
        Directory.CreateDirectory(directory);
        await Scenario(directory, previous, app, "success", false, true);
        await Scenario(directory, previous, app, "rollback", true, false);
        if (options.Count > 0) await Scenario(directory, previous, app, "health-failure", false, false, Path.GetFullPath(options[0]));
        Console.WriteLine("PASS real executable handoff, original-directory replacement, healthy restart, independent recovery and failed-launch rollback");
        return 0;
    }
    private static async Task Scenario(string directory, string previous, string app, string name, bool broken, bool recover, string? healthFailure = null)
    {
        var root = Path.Combine(directory, name, "install"); var job = Path.Combine(directory, name, "job"); var profile = Path.Combine(directory, name, "profile");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(job, "stage"));
        UpdatePayload CopyPayload(string executable, string destination)
        {
            var source = Path.GetDirectoryName(executable)!;
            var payload = UpdateInstaller.Read<UpdatePayload>(Path.Combine(source, "install-files.json"));
            foreach (var file in payload.Files)
            {
                var target = UpdateInstaller.FileAt(destination, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(UpdateInstaller.FileAt(source, file), target);
            }
            return payload;
        }
        var previousManifest = CopyPayload(previous, root);
        var manifest = CopyPayload(app, Path.Combine(job, "stage"));
        File.Copy(previous, Path.Combine(job, "Updater.exe"));
        File.WriteAllText(Path.Combine(root, "README.md"), "old readme");
        File.WriteAllText(Path.Combine(root, "user-file.txt"), "keep this user file");
        File.WriteAllText(Path.Combine(job, "stage", "README.md"), "new readme");
        if (broken) File.WriteAllText(Path.Combine(job, "stage", "CodexQuota.exe"), "deliberately invalid executable");
        if (healthFailure is not null) File.Copy(healthFailure, Path.Combine(job, "stage", "CodexQuota.exe"), true);
        var store = new AccountStore(profile);
        store.SaveSettings(new() { GuideVersion = 1, LayoutVersion = 4, TaskbarVersion = 1, TaskbarWidgetEnabled = false, CloseToTray = false, AutoCheckUpdates = false, LowQuotaThreshold = 12.5, Language = name == "success" ? "zh-CN" : "en", Material = recover ? "glass" : "solid", Topmost = recover });
        File.WriteAllText(Path.Combine(profile, "auth-sentinel.txt"), "untouched account data");
        var parentNonce = Guid.NewGuid().ToString("N"); var nonce = Guid.NewGuid().ToString("N");
        using var parentHealth = new EventWaitHandle(false, EventResetMode.ManualReset, Event(parentNonce, "Health"));
        using var parentStop = new EventWaitHandle(false, EventResetMode.ManualReset, Event(parentNonce, "Stop"));
        var installedExe = Path.Combine(root, "CodexQuota.exe");
        var start = new ProcessStartInfo(installedExe) { UseShellExecute = false };
        foreach (var arg in new[] { "--demo", "--data-dir", profile, "--update-health", parentNonce }) start.ArgumentList.Add(arg);
        var startup = Stopwatch.StartNew(); using var parent = Process.Start(start)!;
        Check(await Task.Run(() => parentHealth.WaitOne(TimeSpan.FromSeconds(25))), "fixture parent starts and renders");
        Console.WriteLine($"MEASURE {name} startup to rendered UI {startup.Elapsed.TotalMilliseconds:0} ms");
        string[] launchArgs = ["--demo", "--data-dir", profile];
        // An update started from settings while the main panel is hidden restarts in the tray.
        var plan = new UpdatePlan(root, job, manifest.Version, parent.Id, parent.StartTime.ToUniversalTime().Ticks, nonce,
            recover ? [.. launchArgs, "--background"] : launchArgs);
        var planPath = Path.Combine(job, "plan.json"); UpdateInstaller.Write(planPath, plan);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Event(nonce, "Ready"));
        var helperStart = new ProcessStartInfo(Path.Combine(job, "Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
        helperStart.ArgumentList.Add("--apply-update"); helperStart.ArgumentList.Add(planPath);
        using var helper = Process.Start(helperStart)!;
        Check(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(25))), "external updater validates and signals readiness");
        Check(UpdateMaintenance.BlocksStartup(root) && !UpdateMaintenance.BlocksStartup(root, nonce), "update lease prevents accidental old-app relaunch but permits the health candidate");
        parentStop.Set(); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        parent.Dispose();
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        var result = UpdateInstaller.Read<UpdateResult>(Path.Combine(job, "result.json"));
        Check(result.Status == (broken || healthFailure is not null ? "rolled-back" : "success"), "expected update result: " + result.Status + " " + result.Error);
        var installedVersion = result.Status == "success" ? manifest.Version : previousManifest.Version;
        Check(FileVersionInfo.GetVersionInfo(installedExe).ProductVersion == installedVersion, "installed executable matches the expected version");
        Check(File.ReadAllText(Path.Combine(root, "README.md")) == (broken || healthFailure is not null ? "old readme" : "new readme"), "payload content matches the outcome");
        Check(File.ReadAllText(Path.Combine(profile, "auth-sentinel.txt")) == "untouched account data" && store.LoadSettings().LowQuotaThreshold == 12.5, "account data and preferences survive");
        Check(File.ReadAllText(Path.Combine(root, "user-file.txt")) == "keep this user file", "unknown install files survive");
        Check(!UpdateMaintenance.BlocksStartup(root), "completed jobs release their maintenance lease");
        if (recover) await WakeAndCloseFixture(checkHidden: true);
        else await CloseFixture(root);
        if (recover)
        {
            File.Delete(installedExe);
            var restore = new ProcessStartInfo(Path.Combine(job, "Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
            restore.ArgumentList.Add("--recover-update"); restore.ArgumentList.Add(planPath);
            using var recovery = Process.Start(restore)!; await recovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(recovery.ExitCode == 0 && File.ReadAllText(Path.Combine(root, "README.md")) == "old readme", "external recovery restores and restarts the previous payload");
            Check(FileVersionInfo.GetVersionInfo(installedExe).ProductVersion == previousManifest.Version, "external recovery restores the previous executable version");
            await WakeAndCloseFixture(checkHidden: false);
        }
        Console.WriteLine("PASS update lifecycle " + name + (recover ? " and external recovery" : ""));

        async Task WakeAndCloseFixture(bool checkHidden)
        {
            using var candidate = FindFixtureProcess(root);
            Check(candidate is not null, "the background restart is still running");
            var wasHidden = candidate!.MainWindowHandle == 0;
            var wakeStart = new ProcessStartInfo(installedExe) { UseShellExecute = false };
            foreach (var argument in launchArgs) wakeStart.ArgumentList.Add(argument);
            using var wake = Process.Start(wakeStart)!;
            await wake.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await CloseFixture(root);
            if (checkHidden)
            {
                Check(wasHidden, "the background update leaves no visible native main window before wake");
                Console.WriteLine("PASS background update restart stays hidden until explicitly opened");
            }
        }
    }
    private static Process? FindFixtureProcess(string root)
    {
        Process? process = null;
        foreach (var candidate in Process.GetProcessesByName("CodexQuota"))
        {
            try
            {
                if (process is null && candidate.MainModule?.FileName.Equals(Path.Combine(root, "CodexQuota.exe"), StringComparison.OrdinalIgnoreCase) == true)
                    process = candidate;
            }
            catch (System.ComponentModel.Win32Exception) { }
            if (!ReferenceEquals(candidate, process)) candidate.Dispose();
        }
        return process;
    }
    internal static async Task CloseFixture(string root)
    {
        root = Path.GetFullPath(root);
        Process? process = null;
        for (var retry = 0; retry < 100; retry++)
        {
            process = FindFixtureProcess(root);
            if (process is not null && process.MainWindowHandle != 0) break;
            process?.Dispose(); process = null; await Task.Delay(100);
        }
        using (process)
        {
            Check(process is not null, "restarted fixture app has a visible main window");
            _ = PostMessage(process!.MainWindowHandle, 0x0010, 0, 0);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
    private static string Event(string nonce, string suffix) => @"Local\CodexQuota.Update." + nonce + "." + suffix;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
