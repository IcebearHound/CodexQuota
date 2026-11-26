using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Quota.Core;

namespace Quota.Desktop;
public partial class MainWindow
{
    // Preserve notification occurrence identities from releases before 2.3.
    private static string WidgetKey(string key) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    private QuotaAlerts? alerts;
    private string? desktopModeNotice;
    private string? notificationNotice;
    private AttentionNotificationWindow? attentionNotification;
    private void ShowAttentionNotification(string key, string title, string detail, string glyph, string color)
    {
        if (exiting || !IsLoaded) return;
        attentionNotification ??= new AttentionNotificationWindow();
        var anchor = taskbarWidget is { DockVisible: true } widget ? (Window)widget : this;
        attentionNotification.Present(key, title, detail, glyph, color, anchor, anchor is TaskbarWidget);
    }
    private void ApplyDesktopWindowMode()
    {
        desktopModeNotice = UpdateRecoveryNotice(desktopHost.Apply(this, settings.DesktopOnly),
            L.F("桌面显示模式应用失败，请重新选择窗口位置，或切换为普通窗口后重试。", "Desktop placement could not be applied. Select a window mode again, or switch to Normal and retry."), desktopModeNotice);
        WindowSurface.ApplyMaterial(this);
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
                        if (window.ApiCredit is { } api)
                        {
                            windows.Add(new(accountKey, "api-grant", api.RemainingPercent ?? (api.RemainingUsd > 0 ? 100 : 0),
                                api.ExpiryBoundary?.ToUnixTimeSeconds(), window.Kind, api.RemainingUsd, api.LatestGrant, api.RemainingPercent is not null));
                            if (api.ExpiryBoundary is { } expiry && expiry.Date >= DateTimeOffset.Now.Date)
                                resetCards.Add(new(accountKey, "api-grant", expiry, IsApi: true));
                            continue;
                        }
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
            var exhausted = new List<string>();
            foreach (var alert in pending)
            {
                var group = visibleAccounts.FirstOrDefault(group => WidgetKey(group.Key) == alert.AccountKey);
                if (group is null) continue;
                var text = group.IsApi ? ApiAlertText(group, alert) : alert.Kind switch
                {
                    "low" => L.F("{0} 额度即将用尽，剩余 {1:0.#}%。", "{0} is running low, {1:0.#}% remaining.", group.Name, alert.Value),
                    "exhausted" => L.F("{0} 的额度已用尽。", "{0} has exhausted its quota.", group.Name),
                    "reset" => L.F("{0} 的额度已重置。", "{0} quota has reset.", group.Name),
                    "expiry" => L.F("{0} 有 {1} 张重置卡将在 {2} 天内到期。", "{0} has {1} reset cards expiring within {2} days.", group.Name, alert.Count, alert.Value),
                    _ => L.F("{0} 有 {1} 张重置卡已到期。", "{0} has {1} expired reset cards.", group.Name, alert.Count)
                };
                if (alert.Kind == "exhausted") exhausted.Add(text);
                else tray.ShowBalloonTip(8000, "Codex Quota", text, alert.Kind == "low" ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            }
            if (exhausted.Count > 0)
                ShowAttentionNotification("quota/exhausted", L.F("额度已耗尽", "Quota exhausted"),
                    string.Join(Environment.NewLine, exhausted.Distinct()), "minus", CodexStatusViewModel.ErrorColor);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            notificationNotice = UpdateRecoveryNotice(false, L.T("通知状态未能保存，请检查本地目录权限。"), notificationNotice);
        }
    }
    private static string ApiAlertText(AccountGroupViewModel group, QuotaAlert alert) => alert.Kind switch
    {
        "low" => L.F("{0} 赠送额度偏低，剩余 {1}（{2:0.#}%）。", "{0} promotional credits are low: {1} left ({2:0.#}%).", group.DisplayName, group.FocusWindow?.ApiRemainingText ?? "—", alert.Value),
        "exhausted" => L.F("{0} 的赠送额度已用尽。", "{0} has used up its promotional credits.", group.DisplayName),
        "reset" => L.F("{0} 有新发放的赠送额度。", "{0} has received new promotional credits.", group.DisplayName),
        _ when alert.Value == 0 => L.F("{0} 的下一笔赠送额度今天到期。", "The next grant for {0} expires today.", group.DisplayName),
        _ => L.F("{0} 的下一笔赠送额度将在 {1} 天内到期。", "The next grant for {0} expires within {1} days.", group.DisplayName, alert.Value)
    };
}
