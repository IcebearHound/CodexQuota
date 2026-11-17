using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Quota.Core;

namespace Quota.Desktop;

internal static class UpdateRunner
{
    internal static string EventName(string nonce, string suffix) => @"Local\CodexQuota.Update." + nonce + "." + suffix;
    internal static async Task<int> RunAsync(string planPath, bool recover)
    {
        UpdatePlan? plan = null;
        var parentExited = false;
        var ownsLease = false;
        using var installation = new Semaphore(1, 1, @"Local\CodexQuota.Update.Install");
        var acquired = installation.WaitOne(0);
        try
        {
            plan = UpdateInstaller.Read<UpdatePlan>(planPath);
            if (!acquired) throw new IOException("Another update or recovery is already running.");
            if (!Path.GetFullPath(plan.JobRoot).Equals(Path.GetDirectoryName(Path.GetFullPath(planPath)), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(plan.JobRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater must run from its independent recovery directory.");
            UpdateInstaller.EnsurePhysical(plan.JobRoot);
            using var jobLock = new FileStream(Path.Combine(plan.JobRoot, "running.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (recover)
            {
                // Windows refuses the exclusive write probe if the application is still running.
                var executable = UpdateInstaller.FileAt(plan.InstallRoot, "CodexQuota.exe");
                if (File.Exists(executable)) { using (new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                UpdateInstaller.Restore(plan);
                UpdateInstaller.Write(Path.Combine(plan.JobRoot, "result.json"), new UpdateResult("rolled-back", plan.Version, "Manual recovery."));
                Start(plan, false); return 0;
            }
            _ = UpdateInstaller.Validate(plan);
            if (UpdateMaintenance.BlocksStartup(plan.InstallRoot)) throw new IOException("Another update is already running.");
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks
                || !Path.GetFullPath(parent.MainModule!.FileName).Equals(UpdateInstaller.FileAt(plan.InstallRoot, "CodexQuota.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update parent process no longer matches.");
            using (var self = Process.GetCurrentProcess())
                UpdateInstaller.Write(Path.Combine(plan.InstallRoot, UpdateMaintenance.Marker), new UpdateLease(self.Id, self.StartTime.ToUniversalTime().Ticks, plan.Nonce, plan.JobRoot));
            ownsLease = true;
            using (var ready = EventWaitHandle.OpenExisting(EventName(plan.Nonce, "Ready"))) ready.Set();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30))) await parent.WaitForExitAsync(timeout.Token);
            parent.Dispose(); // Release the old image's process handle before replacing it.
            parentExited = true;
            using var health = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(plan.Nonce, "Health"));
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(plan.Nonce, "Stop"));
            var result = await UpdateInstaller.ApplyAsync(plan, async () =>
            {
                using var candidate = Start(plan, true);
                var healthy = await Task.Run(() => health.WaitOne(TimeSpan.FromSeconds(25)));
                if (healthy) { await Task.Delay(2000); healthy = !candidate.HasExited; }
                if (healthy) return true;
                stop.Set();
                if (!candidate.HasExited)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await candidate.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { if (!candidate.HasExited) { candidate.Kill(); await candidate.WaitForExitAsync(); } }
                }
                return false;
            });
            if (result.Status == "rolled-back") { ReleaseLease(plan); ownsLease = false; Start(plan, false); }
            return result.Status == "success" ? 0 : 1;
        }
        catch (Exception error)
        {
            if (plan is not null && Directory.Exists(plan.JobRoot))
            {
                try { UpdateInstaller.Write(Path.Combine(plan.JobRoot, "result.json"), new UpdateResult("failed", plan.Version, error.GetType().Name + ": " + error.Message)); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                if (parentExited && !File.Exists(Path.Combine(plan.JobRoot, "transaction.json")))
                {
                    try { ReleaseLease(plan); ownsLease = false; Start(plan, false); }
                    catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception) { }
                }
            }
            return 1;
        }
        finally { if (ownsLease && plan is not null) ReleaseLease(plan); if (acquired) installation.Release(); }
    }
    private static void ReleaseLease(UpdatePlan plan)
    {
        try
        {
            var path = Path.Combine(plan.InstallRoot, UpdateMaintenance.Marker);
            if (File.Exists(path) && UpdateInstaller.Read<UpdateLease>(path).Nonce == plan.Nonce) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }
    private static Process Start(UpdatePlan plan, bool health)
    {
        var start = new ProcessStartInfo(UpdateInstaller.FileAt(plan.InstallRoot, "CodexQuota.exe")) { UseShellExecute = false, WorkingDirectory = plan.InstallRoot };
        foreach (var argument in plan.RestartArguments) start.ArgumentList.Add(argument);
        if (health) { start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(plan.Nonce); }
        return Process.Start(start) ?? throw new IOException("The application could not restart.");
    }
}
