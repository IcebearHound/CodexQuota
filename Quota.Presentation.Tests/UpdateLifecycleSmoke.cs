using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Quota.Core;

internal static class UpdateLifecycleSmoke
{
    public static async Task<int> Run(string[] args)
    {
        var directory = Path.GetFullPath(args[1]); var app = Path.GetFullPath(args[2]);
        if (Directory.Exists(directory)) throw new ArgumentException("Choose a fresh lifecycle fixture directory.");
        Directory.CreateDirectory(directory);
        await Scenario(directory, app, "success", false, true);
        await Scenario(directory, app, "rollback", true, false);
        if (args.Length > 3) await Scenario(directory, app, "health-failure", false, false, Path.GetFullPath(args[3]));
        Console.WriteLine("PASS real executable handoff, original-directory replacement, healthy restart, independent recovery and failed-launch rollback");
        return 0;
    }
    private static async Task Scenario(string directory, string app, string name, bool broken, bool recover, string? healthFailure = null)
    {
        var root = Path.Combine(directory, name, "install"); var job = Path.Combine(directory, name, "job"); var profile = Path.Combine(directory, name, "profile");
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(job, "stage"));
        var manifest = UpdateInstaller.Read<UpdatePayload>(Path.Combine(Path.GetDirectoryName(app)!, "install-files.json"));
        foreach (var file in manifest.Files)
        {
            foreach (var targetRoot in new[] { root, Path.Combine(job, "stage") })
            {
                var target = UpdateInstaller.FileAt(targetRoot, file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(UpdateInstaller.FileAt(Path.GetDirectoryName(app)!, file), target);
            }
        }
        File.Copy(app, Path.Combine(job, "Updater.exe"));
        File.WriteAllText(Path.Combine(root, "README.md"), "old readme");
        File.WriteAllText(Path.Combine(root, "user-file.txt"), "keep this user file");
        File.WriteAllText(Path.Combine(job, "stage", "README.md"), "new readme");
        if (broken) File.WriteAllText(Path.Combine(job, "stage", "CodexQuota.exe"), "deliberately invalid executable");
        if (healthFailure is not null) File.Copy(healthFailure, Path.Combine(job, "stage", "CodexQuota.exe"), true);
        var store = new AccountStore(profile);
        store.SaveSettings(new() { GuideVersion = 1, LayoutVersion = 4, TaskbarVersion = 1, TaskbarWidgetEnabled = false, CloseToTray = false, AutoCheckUpdates = false, LowQuotaThreshold = 12.5, Language = name == "success" ? "zh-CN" : "en" });
        File.WriteAllText(Path.Combine(profile, "auth-sentinel.txt"), "untouched account data");
        var parentNonce = Guid.NewGuid().ToString("N"); var nonce = Guid.NewGuid().ToString("N");
        using var parentHealth = new EventWaitHandle(false, EventResetMode.ManualReset, Event(parentNonce, "Health"));
        using var parentStop = new EventWaitHandle(false, EventResetMode.ManualReset, Event(parentNonce, "Stop"));
        var start = new ProcessStartInfo(Path.Combine(root, "CodexQuota.exe")) { UseShellExecute = false };
        foreach (var arg in new[] { "--demo", "--data-dir", profile, "--update-health", parentNonce }) start.ArgumentList.Add(arg);
        var startup = Stopwatch.StartNew(); using var parent = Process.Start(start)!;
        Check(await Task.Run(() => parentHealth.WaitOne(TimeSpan.FromSeconds(25))), "fixture parent starts and renders");
        Console.WriteLine($"MEASURE {name} startup to rendered UI {startup.Elapsed.TotalMilliseconds:0} ms");
        var plan = new UpdatePlan(root, job, manifest.Version, parent.Id, parent.StartTime.ToUniversalTime().Ticks, nonce, ["--demo", "--data-dir", profile]);
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
        Check(File.ReadAllText(Path.Combine(root, "README.md")) == (broken || healthFailure is not null ? "old readme" : "new readme"), "payload content matches the outcome");
        Check(File.ReadAllText(Path.Combine(profile, "auth-sentinel.txt")) == "untouched account data" && store.LoadSettings().LowQuotaThreshold == 12.5, "account data and preferences survive");
        Check(File.ReadAllText(Path.Combine(root, "user-file.txt")) == "keep this user file", "unknown install files survive");
        Check(!UpdateMaintenance.BlocksStartup(root), "completed jobs release their maintenance lease");
        await CloseFixture(root);
        if (recover)
        {
            File.Delete(Path.Combine(root, "CodexQuota.exe"));
            var restore = new ProcessStartInfo(Path.Combine(job, "Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
            restore.ArgumentList.Add("--recover-update"); restore.ArgumentList.Add(planPath);
            using var recovery = Process.Start(restore)!; await recovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(recovery.ExitCode == 0 && File.ReadAllText(Path.Combine(root, "README.md")) == "old readme", "external recovery restores and restarts the previous payload");
            await CloseFixture(root);
        }
        Console.WriteLine("PASS update lifecycle " + name + (recover ? " and external recovery" : ""));
    }
    internal static async Task CloseFixture(string root)
    {
        root = Path.GetFullPath(root);
        Process? process = null;
        for (var retry = 0; retry < 100; retry++)
        {
            foreach (var candidate in Process.GetProcessesByName("CodexQuota"))
            {
                try { if (candidate.MainModule?.FileName.Equals(Path.Combine(root, "CodexQuota.exe"), StringComparison.OrdinalIgnoreCase) == true) { process = candidate; break; } }
                catch (System.ComponentModel.Win32Exception) { }
                if (!ReferenceEquals(candidate, process)) candidate.Dispose();
            }
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
