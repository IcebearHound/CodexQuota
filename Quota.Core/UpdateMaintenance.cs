using System.Diagnostics;

namespace Quota.Core;

public sealed record UpdateLease(int ProcessId, long StartedUtcTicks, string Nonce, string JobRoot);
public static class UpdateMaintenance
{
    public const string Marker = ".codexquota-update.json";
    public static bool BlocksStartup(string installRoot, string? healthNonce = null)
    {
        var path = Path.Combine(installRoot, Marker);
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 4096) return false;
            var lease = UpdateInstaller.Read<UpdateLease>(path);
            if (!Guid.TryParseExact(lease.Nonce, "N", out _)) return false;
            using var owner = Process.GetProcessById(lease.ProcessId);
            var live = owner.StartTime.ToUniversalTime().Ticks == lease.StartedUtcTicks
                && Path.GetFullPath(owner.MainModule!.FileName).Equals(Path.Combine(lease.JobRoot, "Updater.exe"), StringComparison.OrdinalIgnoreCase);
            return live && lease.Nonce != healthNonce;
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
