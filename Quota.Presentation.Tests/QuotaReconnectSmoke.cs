using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class QuotaReconnectSmoke
{
    public static async Task Run(MainWindow main, AccountStore store)
    {
        const BindingFlags nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        FieldInfo Field(string name) => type.GetField(name, nonPublic)!;
        MethodInfo Method(string name) => type.GetMethod(name, nonPublic)!;
        await (Task)Field("startupRefresh").GetValue(main)!;
        ((DispatcherTimer)Field("activityTimer").GetValue(main)!).Stop();
        Field("activity").SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Working, Running: 1));
        void UpdateLamp() => Method("UpdateCodexStatus").Invoke(main, null);
        const string timeout = "读取超时。保留上次额度，稍后可重试。";
        var accounts = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var current = accounts.Single(account => account.Record.IsCurrent);
        var currentHome = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        Check(currentHome.StartsWith(store.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "uses an isolated Codex home");
        Check(current.Snapshot is not null && !current.HasError, "fixture starts with a successful quota read");

        var inactive = new AccountViewModel(new() { Name = "Inactive" });
        inactive.SetSnapshot(new() { Email = "inactive@example.com" });
        var inactiveGroup = new AccountGroupViewModel("inactive", [inactive]);
        var neutral = inactiveGroup.StatusColor;
        inactiveGroup.Dispose();
        inactive.SetError(timeout, timedOut: true);
        store.CreateProfile(inactive.Record.Id);
        accounts.Add(inactive);
        var group = groups.Single(group => group.Sources.Contains(inactive));
        UpdateLamp();
        Check(group.Status == L.T("已过期") && group.StatusColor == neutral && main.CodexStatus.Signal == CodexSignal.Working,
            "an inactive timeout stays visible without a red warning");

        var healthy = new AccountViewModel(new() { Name = "Healthy" });
        var healthySnapshot = new UsageSnapshot { Email = "healthy@example.com" };
        healthy.SetSnapshot(healthySnapshot);
        accounts.Add(healthy);

        var scenario = Path.Combine(currentHome, ".quota-test-scenario");
        File.WriteAllText(scenario, "slow");
        current.SetError(timeout, timedOut: true); UpdateLamp();
        Check(main.CodexStatus.Signal == CodexSignal.Error, "the active account's completed timeout can light the lamp");
        Method("NetworkSettingsChanged").Invoke(main, [null, EventArgs.Empty]);
        await WaitFor(() => current.IsRefreshing, TimeSpan.FromSeconds(4), "the running connectivity monitor detects the adapter event and starts a retry");
        Check(current.Status == L.T("更新中") && main.CodexStatus.Signal == CodexSignal.Working, "the pending retry clears the old stale warning");
        await WaitFor(() => !inactive.HasError && !inactive.IsRefreshing, TimeSpan.FromSeconds(4), "network change also resyncs an inactive stale account");
        Check(ReferenceEquals(healthy.Snapshot, healthySnapshot) && !healthy.IsRefreshing, "healthy accounts are skipped");
        await WaitFor(() => !current.IsRefreshing, TimeSpan.FromSeconds(29), "the fake CLI reaches its real RPC timeout");
        Check(current.Status == L.T("已过期") && main.CodexStatus.Signal == CodexSignal.Error && main.CodexStatus.Message.Contains(L.Error(timeout)),
            "stale returns only after the retry reaches its timeout");
        Console.WriteLine("PASS connectivity retry starts promptly, skips healthy accounts, and only shows stale once the request times out");

        var observe = Method("ObserveQuotaConnectionAsync");
        var proxyType = observe.GetParameters()[0].ParameterType;
        var oldProxy = Activator.CreateInstance(proxyType, false, null, null, null)!;
        var newProxy = Activator.CreateInstance(proxyType, false, null, "127.0.0.1:45551", null)!;
        Field("lastProxySettings").SetValue(main, oldProxy);
        File.WriteAllText(scenario, "normal");
        Field("switching").SetValue(main, true);
        try
        {
            await (Task)observe.Invoke(main, [newProxy])!;
            Check(!current.IsRefreshing, "proxy changes do not start a quota read inside a credential switch");
        }
        finally { Field("switching").SetValue(main, false); }
        await (Task)observe.Invoke(main, [newProxy])!;
        Check(!current.HasError && current.Status == L.T("已同步") && main.CodexStatus.Signal == CodexSignal.Working,
            "a queued proxy-only change retries after the switch gate clears and restores the real quota");
        Console.WriteLine("PASS proxy-only changes queue a retry behind an active credentials transaction");
    }

    private static async Task WaitFor(Func<bool> ready, TimeSpan timeout, string message)
    {
        var started = Stopwatch.StartNew();
        while (!ready() && started.Elapsed < timeout) await Task.Delay(20);
        Check(ready(), message);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
