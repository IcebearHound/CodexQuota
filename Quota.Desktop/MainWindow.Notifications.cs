using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Quota.Core;

namespace Quota.Desktop;
public partial class MainWindow
{
    private QuotaAlerts? alerts;
    private string? desktopModeNotice;
    private string? notificationNotice;
    private void ApplyDesktopWindowMode()
    {
        desktopModeNotice = UpdateRecoveryNotice(desktopHost.Apply(this, settings.DesktopOnly),
            L.F("桌面显示模式应用失败，请重新选择窗口位置，或切换为普通窗口后重试。", "Desktop placement could not be applied. Select a window mode again, or switch to Normal and retry."), desktopModeNotice);
    }
    private string? UpdateRecoveryNotice(bool healthy, string message, string? previous)
    {
        if (healthy)
        {
            if (previous is not null && Notice.Text == previous) NoticeBorder.Visibility = Visibility.Collapsed;
            return null;
        }
        if (previous is null) { ShowNotice(message); return message; }
        if (Notice.Text == previous) { Notice.Text = message; return message; }
        return previous;
    }
    internal void SwitchFromTaskbar(object sender, RoutedEventArgs args) => SwitchAccountClick(sender, args);
    private void MaintainDesktopWindows()
    {
        if (exiting || !settings.DesktopOnly) return;
        ApplyDesktopWindowMode();
    }
    private void CheckNotifications()
    {
        if (demo) return;
        var windows = new List<AlertWindow>();
        var resetCards = new List<ResetCreditDeadline>();
        foreach (var group in visibleAccounts)
        {
            var accountKey = WidgetKey(group.Key);
            foreach (var source in group.Sources.Where(source => source.Snapshot is not null && !source.HasError && !source.IsCached).GroupBy(source => source.Snapshot!.AccountId ?? source.Record.Id).Select(sources => sources.OrderByDescending(source => source.Snapshot!.FetchedAt).First()))
            {
                foreach (var bucket in source.Snapshot!.Buckets)
                    for (var index = 0; index < bucket.Windows.Count; index++)
                    {
                        var window = bucket.Windows[index];
                        var slot = window.Kind switch { "primary" => 0, "secondary" => 1, _ => index };
                        windows.Add(new(accountKey, (source.Snapshot.AccountId ?? source.Record.Id) + "/" + bucket.Name + "/" + slot, window.Remaining, window.ResetsAt, window.Kind));
                    }
                foreach (var credit in source.Snapshot.ResetCredits.Where(credit => credit.ExpiryKnown && credit.ExpiresAt is not null))
                    resetCards.Add(new(accountKey, (source.Snapshot.AccountId ?? source.Record.Id) + "/" + credit.Id, DateTimeOffset.FromUnixTimeSeconds(credit.ExpiresAt!.Value)));
            }
        }
        try
        {
            alerts ??= new QuotaAlerts(store.Root);
            var pending = alerts.Evaluate(windows, settings, DateTimeOffset.Now, resetCards);
            notificationNotice = UpdateRecoveryNotice(true, "", notificationNotice);
            foreach (var alert in pending)
            {
                var group = visibleAccounts.FirstOrDefault(group => WidgetKey(group.Key) == alert.AccountKey);
                if (group is null) continue;
                var text = alert.Kind switch
                {
                    "low" => L.F("{0} 额度即将用尽，剩余 {1:0.#}%。", "{0} is running low, {1:0.#}% remaining.", group.Name, alert.Value),
                    "exhausted" => L.F("{0} 的额度已用尽。", "{0} has exhausted its quota.", group.Name),
                    "reset" => L.F("{0} 的额度已重置。", "{0} quota has reset.", group.Name),
                    "expiry" => L.F("{0} 有 {1} 张重置卡将在 {2} 天内到期。", "{0} has {1} reset cards expiring within {2} days.", group.Name, alert.Count, alert.Value),
                    _ => L.F("{0} 有 {1} 张重置卡已到期。", "{0} has {1} expired reset cards.", group.Name, alert.Count)
                };
                tray.ShowBalloonTip(8000, "Codex Quota", text, alert.Kind is "low" or "exhausted" ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            notificationNotice = UpdateRecoveryNotice(false, L.T("通知状态未能保存，请检查本地目录权限。"), notificationNotice);
        }
    }
}
