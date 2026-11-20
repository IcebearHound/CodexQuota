using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Quota.Desktop;

public partial class MainWindow
{
    // Coalesce adapter events and read the Windows proxy configuration locally. No connectivity probe is sent.
    private readonly DispatcherTimer connectionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int networkChanged;
    private bool connectionRetryPending;
    private WindowsProxySettings? lastProxySettings;

    private void StartMonitoringQuotaConnection()
    {
        if (connectionTimer.IsEnabled) return;
        lastProxySettings = WindowsProxySettings.Read();
        NetworkChange.NetworkAddressChanged += NetworkSettingsChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkSettingsChanged;
        connectionTimer.Tick += CheckQuotaConnection;
        connectionTimer.Start();
    }

    private void StopMonitoringQuotaConnection()
    {
        if (!connectionTimer.IsEnabled) return;
        connectionTimer.Stop();
        connectionTimer.Tick -= CheckQuotaConnection;
        NetworkChange.NetworkAddressChanged -= NetworkSettingsChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkSettingsChanged;
    }

    private void NetworkSettingsChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref networkChanged, 1);
    private async void CheckQuotaConnection(object? sender, EventArgs args) => await ObserveQuotaConnectionAsync(WindowsProxySettings.Read());

    private async Task ObserveQuotaConnectionAsync(WindowsProxySettings? proxySettings)
    {
        if (Interlocked.Exchange(ref networkChanged, 0) != 0) connectionRetryPending = true;
        if (proxySettings is not null)
        {
            if (lastProxySettings is not null && lastProxySettings != proxySettings) connectionRetryPending = true;
            lastProxySettings = proxySettings;
        }
        // Keep the change pending if a refresh or a credential switch is still using the old connection.
        if (!connectionRetryPending || exiting || switching || refreshing || accountRefreshes.Count > 0 || appUpdateState.Phase == "installing") return;
        connectionRetryPending = false;
        await RefreshQuotasAsync(unsyncedOnly: true);
    }
}

internal sealed record WindowsProxySettings(bool AutoDetect, string? Script, string? Server, string? Bypass)
{
    internal static WindowsProxySettings? Read()
    {
        var success = WinHttpGetIEProxyConfigForCurrentUser(out var config);
        var error = Marshal.GetLastPInvokeError();
        try
        {
            if (!success) return error == 2 ? new(false, null, null, null) : null; // No configured proxy settings.
            return new(config.AutoDetect, Marshal.PtrToStringUni(config.Script), Marshal.PtrToStringUni(config.Server), Marshal.PtrToStringUni(config.Bypass));
        }
        finally
        {
            if (config.Script != 0) GlobalFree(config.Script);
            if (config.Server != 0) GlobalFree(config.Server);
            if (config.Bypass != 0) GlobalFree(config.Bypass);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect;
        public nint Script, Server, Bypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out ProxyConfig config);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
