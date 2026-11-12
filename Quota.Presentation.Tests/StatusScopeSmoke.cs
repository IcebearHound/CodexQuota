using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class StatusScopeSmoke
{
    public static async Task Run(MainWindow main)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var activity = typeof(MainWindow).GetField("activity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var update = typeof(MainWindow).GetMethod("UpdateCodexStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalActivity = activity.GetValue(main);
        var originalLanguage = L.Language;
        var current = accounts.Single(account => account.Record.IsCurrent);
        var original = current.Snapshot!;
        var inactiveSnapshot = Copy(original); inactiveSnapshot.AccountId = "inactive-workspace";
        foreach (var window in inactiveSnapshot.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100;
        var inactive = new AccountViewModel(new() { Name = "Inactive workspace", Email = original.Email }); inactive.SetSnapshot(inactiveSnapshot);
        accounts.Add(inactive);
        var strip = new TaskbarWidget(main, groups, null);
        try
        {
            activity.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Working, Running: 1));
            update.Invoke(main, null);
            Check(groups.Single(group => group.Sources.Contains(current)).FocusWindow!.Remaining == 0, "fixture merges an exhausted inactive workspace");
            Check(main.CodexStatus.Signal == CodexSignal.Working, "inactive workspace quota cannot warn about the active Codex account");
            inactive.SetError("登录已失效，请重新登录后重试。"); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Working, "inactive credential errors stay outside the active runtime lamp");
            var low = Copy(original);
            foreach (var window in low.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100 - settings.LowQuotaThreshold / 2;
            current.SetSnapshot(low); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Low, "active account quota produces its low warning");
            var exhausted = Copy(original);
            foreach (var window in exhausted.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100;
            current.SetSnapshot(exhausted); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Exhausted, "active account exhaustion produces its red warning");
            activity.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Offline)); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Offline, "offline status retains gray precedence");
            Console.WriteLine("PASS runtime quota warnings use only the active Codex account across merged workspaces");
            var lamps = new[] { (StatusLamp)main.FindName("RuntimeLamp"), (StatusLamp)strip.FindName("RuntimeLamp"), (StatusLamp)strip.FindName("FullRuntimeLamp") };
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Apply(language); update.Invoke(main, null);
                foreach (var lamp in lamps)
                    Check(ToolTipService.GetIsEnabled(lamp) && lamp.ToolTip is ToolTip { Content: TextBlock text } && text.Text == main.CodexStatus.Message && text.TextWrapping == TextWrapping.Wrap,
                        "lamps expose their current localized status through a wrapping tooltip");
            }
            var target = lamps[0]; var tooltip = (ToolTip)target.ToolTip;
            tooltip.PlacementTarget = target; tooltip.Placement = PlacementMode.Bottom; tooltip.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var border = (Border)VisualTreeHelper.GetChild(tooltip, 0);
            Check(tooltip.IsOpen && border.CornerRadius.TopLeft == 12, "status hover uses the shared rounded tooltip design");
            tooltip.IsOpen = false;
            Console.WriteLine("PASS main and both taskbar layouts expose localized styled status tooltips");
        }
        finally
        {
            strip.Close(); current.SetSnapshot(original); accounts.Remove(inactive); activity.SetValue(main, originalActivity);
            L.Apply(originalLanguage); update.Invoke(main, null);
        }
    }
    private static UsageSnapshot Copy(UsageSnapshot source) => JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(source))!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
