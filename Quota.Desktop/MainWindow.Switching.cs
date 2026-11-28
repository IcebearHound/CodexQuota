using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Quota.Core;

namespace Quota.Desktop;
public partial class MainWindow
{
    private bool switching;
    private DateTimeOffset nextAutomaticRestart;
    private string? automaticSwitchNotice;
    private Func<Func<CancellationToken, Task<string?>>?, Func<CancellationToken, Task>?, bool, CancellationToken, Task<CodexRestartOutcome>>
        restartCodex = CodexDesktopRestart.RestartWithSessionAsync;

    internal void BuildAccountSwitchingOptions(Panel parent, bool inGuide = false)
    {
        var section = inGuide ? new StackPanel() : SettingsControls.Section(parent, L.F("账号切换", "Account switching"), "arrow-right-left");
        if (inGuide) parent.Children.Add(SettingsControls.Card(section));
        SettingsControls.Toggle(section, L.F("额度耗尽时自动切换账号", "Switch accounts when quota runs out"), "arrow-right-left",
            settings.AutoSwitchOnExhaustion, enabled =>
            {
                settings.AutoSwitchOnExhaustion = enabled;
                nextAutomaticRestart = default;
                SaveSettings();
                if (inGuide && settingsPage == "账号") QueueSettingsRender();
                if (!inGuide) guideWindow?.RefreshAccountSwitchingOptions();
            }, L.F("需要至少一个已连接且有额度的备用账号。切换会重启运行中的 Codex 桌面端，并打开重启前的会话；不会自动重跑任务。",
                "Requires a connected backup account with quota. A running Codex desktop app restarts and reopens the previous chat; tasks are not rerun automatically."));
    }

    private async Task TryAutomaticSwitchAsync(string cli)
    {
        if (!settings.AutoSwitchOnExhaustion || demo || switching || refreshing || loggingIn || exiting
            || accountRefreshes.Count != 0 || appUpdateState.Phase == "installing") return;
        var current = accounts.FirstOrDefault(source => source.Record.IsCurrent);
        if (FreshSnapshot(current) is not { } exhausted) return;
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromMinutes(settings.RefreshMinutes + 1);
        if (!QuotaFailover.IsExhausted(exhausted, now, maxAge)) return;
        if (now < nextAutomaticRestart) return;

        var candidates = accounts.Where(source => source.Managed && FreshSnapshot(source) is not null)
            .Select(source => (Source: source, Remaining: QuotaFailover.Available(exhausted, source.Snapshot!, now, maxAge)))
            .Where(candidate => candidate.Remaining is not null).OrderByDescending(candidate => candidate.Remaining)
            .ThenBy(candidate => candidate.Source.Record.Id, StringComparer.Ordinal).Select(candidate => candidate.Source).ToArray();
        if (candidates.Length == 0) return;

        switching = true;
        var committed = false;
        try
        {
            foreach (var target in candidates)
            {
                await RefreshAccountTrackedAsync(cli, target);
                if (!settings.AutoSwitchOnExhaustion || exiting) return;
                var verifiedAt = DateTimeOffset.UtcNow;
                if (!QuotaFailover.IsExhausted(exhausted, verifiedAt, maxAge)) return;
                if (FreshSnapshot(target) is not { } verified || QuotaFailover.Available(exhausted, verified, verifiedAt, maxAge) is null) continue;
                var name = visibleAccounts.First(group => group.Sources.Contains(target)).Name;
                var switcher = new AccountSwitcher(store);
                var outcome = await restartCodex(async cancellationToken =>
                {
                    var session = await CaptureRestartSessionAsync(cancellationToken, includeQuotaBlocked: true);
                    if (!settings.AutoSwitchOnExhaustion) throw new OperationCanceledException();
                    return session;
                }, async cancellationToken =>
                {
                    var commitTime = DateTimeOffset.UtcNow;
                    if (!settings.AutoSwitchOnExhaustion || !QuotaFailover.IsExhausted(exhausted, commitTime, maxAge)
                        || QuotaFailover.Available(exhausted, verified, commitTime, maxAge) is null) return;
                    await switcher.CommitVerifiedAsync(target.Record, verified, cancellationToken, exhausted.AccountId);
                    committed = true;
                    PublishQuotaSnapshot(current!, verified, fromCache: true);
                }, false, lifetime.Token);

                if (outcome is CodexRestartOutcome.Unavailable or CodexRestartOutcome.StillRunning)
                {
                    ReportAutomaticSwitchFailure(L.F("当前账号未更改。请关闭仅在命令行运行的 Codex，或从桌面端手动重启后再试。",
                        "The account is unchanged. Close any CLI-only Codex session, or restart the desktop app and try again."));
                }
                else if (committed)
                {
                    await RefreshAccountTrackedAsync(cli, current!);
                    if (automaticSwitchNotice is not null && Notice.Text == automaticSwitchNotice) NoticeBorder.Visibility = Visibility.Collapsed;
                    automaticSwitchNotice = null;
                    var detail = outcome switch
                    {
                        CodexRestartOutcome.SwitchedWhileOffline => L.F("下次打开 Codex 时将使用新账号。", "The new account will be used the next time Codex opens."),
                        CodexRestartOutcome.SessionRequested => L.F("已请求打开重启前的会话。", "Requested to reopen the previous chat."),
                        CodexRestartOutcome.SessionUnavailable => L.F("Codex 已重启，请从异常会话列表打开原会话。", "Codex restarted. Open the previous chat from Session Issues."),
                        _ => L.F("Codex 已重启。", "Codex restarted.")
                    };
                    ShowAttentionNotification("quota/auto-switch", L.F("已切换到 {0}", "Switched to {0}", name), detail, "arrow-right-left", CodexStatusViewModel.SuccessColor);
                    if (outcome == CodexRestartOutcome.SessionUnavailable) ShowNotice(detail);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (exiting || lifetime.IsCancellationRequested || !settings.AutoSwitchOnExhaustion) { }
        catch (Exception error) when (error is CodexException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ReportAutomaticSwitchFailure(committed
                ? L.F("新账号已保存，但 Codex 未能重启。请从开始菜单启动 Codex，再打开原会话。", "The new account is saved, but Codex could not restart. Start Codex and reopen the previous chat.")
                : L.F("当前账号未更改。请刷新额度或手动切换账号。", "The account is unchanged. Refresh quota or switch accounts manually."));
        }
        finally { switching = false; if (!exiting) UpdateSummary(); }
    }

    private static UsageSnapshot? FreshSnapshot(AccountViewModel? source)
        => source is { IsCached: false, HasError: false, IsRefreshing: false } ? source.Snapshot : null;

    private void ReportAutomaticSwitchFailure(string detail)
    {
        nextAutomaticRestart = DateTimeOffset.UtcNow.AddMinutes(5);
        if (exiting) return;
        ShowNotice(automaticSwitchNotice = detail);
        ShowAttentionNotification("quota/auto-switch-failed", L.F("未完成自动切换", "Automatic switch incomplete"),
            detail, "arrow-right-left", CodexStatusViewModel.WarningColor);
    }

    private void SwitchAccountClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: AccountGroupViewModel group }) return;
        SwitchAccount(group.Sources.Where(source => source.Managed || source.Record.IsApi).OrderBy(source => source.HasError)
            .ThenByDescending(source => source.Snapshot?.FetchedAt).FirstOrDefault(), group.IsCurrent);
    }

    private void SwitchAccount(AccountViewModel? target, bool isCurrent)
    {
        if (appUpdateState.Phase == "installing" || switching || loggingIn || isCurrent || target is null || !accounts.Contains(target)) return;
        var cli = demo ? null : GetCli();
        if (!demo && cli is null) return;
        var switcher = new AccountSwitcher(store);
        switching = true;
        try
        {
            if (!demo && target.Record.IsApi && !switcher.HasApiKey(target.Record) && !EditApiKey(target)) return;
            ShowSwitchAccountDialog(target, cli, switcher);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DialogLayout.Alert(AccountDialogOwner, L.T("切换未完成"), L.F("无法读取 API Key，请检查本地文件权限。", "Could not read the API key. Check local file permissions."));
        }
        finally { switching = false; }
    }

    private void ShowSwitchAccountDialog(AccountViewModel target, string? cli, AccountSwitcher switcher)
    {
        var dialog = DialogLayout.Create(AccountDialogOwner, L.T("正在切换"), 420);
        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) }; DialogLayout.Frame(dialog, root);
        var heading = DialogLayout.Text(L.T("正在切换"), 20); root.Children.Add(heading);
        var message = DialogLayout.Text(L.T("正在验证账号并更新认证，请稍候。"), 12, true); root.Children.Add(message);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 10, 0, 18) }; root.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(actions);
        var completed = false;
        var restarting = false;
        dialog.Closing += (_, eventArgs) => { if (!completed || restarting) eventArgs.Cancel = true; };
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                if (demo) await System.Threading.Tasks.Task.Delay(650);
                else
                {
                    await Task.WhenAll(accountRefreshes.Where(pair => pair.Key.Record.IsCurrent || ReferenceEquals(pair.Key, target)).Select(pair => pair.Value).ToArray());
                    var verified = await switcher.SwitchAsync(cli!, target.Record, lifetime.Token);
                    var local = accounts.FirstOrDefault(source => source.Record.IsCurrent);
                    if (verified is not null && local is not null) PublishQuotaSnapshot(local, verified, fromCache: true);
                }
                completed = true; progress.Visibility = Visibility.Collapsed;
                heading.Text = L.T("账号已切换"); dialog.Title = heading.Text;
                message.Text = L.F("重启 Codex 以加载新账号。重启后会打开最近运行的会话，未完成的任务需要手动继续。",
                    "Restart Codex to load the new account. The most recent running chat will reopen; continue unfinished work there.");
                var later = new Button { Content = L.T("稍后重启"), Margin = new Thickness(0, 0, 8, 0) }; later.Click += (_, _) => dialog.Close();
                var restart = new Button { Content = L.T("立即重启") };
                restart.Click += async (_, _) =>
                {
                    restart.IsEnabled = false; later.IsEnabled = false; restarting = true;
                    try
                    {
                        var outcome = demo ? CodexRestartOutcome.Restarted : await restartCodex(token => CaptureRestartSessionAsync(token), null, true, lifetime.Token);
                        if (outcome == CodexRestartOutcome.Unavailable)
                        {
                            message.Text = L.T("未找到可用的 Codex 注册启动入口，请从开始菜单启动 Codex。");
                            return;
                        }
                        if (outcome == CodexRestartOutcome.StillRunning)
                        {
                            message.Text = L.F("请先退出 Codex 桌面端，再从开始菜单启动以加载新账号。",
                                "Quit the Codex desktop app, then start it again to load the new account.");
                            return;
                        }
                        if (outcome == CodexRestartOutcome.SessionUnavailable)
                            ShowNotice(L.F("Codex 已重启，请从异常会话列表打开原会话。", "Codex restarted. Open the previous chat from Session Issues."));
                        restarting = false; dialog.Close();
                    }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException or OperationCanceledException)
                    { message.Text = L.T("Codex 未能自动重启，请手动启动并恢复会话。"); }
                    finally { restarting = false; restart.IsEnabled = true; later.IsEnabled = true; }
                };
                actions.Children.Add(later); actions.Children.Add(restart);
                var current = accounts.FirstOrDefault(source => source.Record.IsCurrent);
                if (!demo && current is not null) await RefreshAccountTrackedAsync(cli!, current);
            }
            catch (Exception error) when (error is CodexException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
            {
                completed = true; progress.Visibility = Visibility.Collapsed;
                heading.Text = L.T("切换未完成"); message.Text = target.Record.IsApi
                    ? L.F("请检查 API Key 和本地文件权限后重试。", "Check the API key and local file permissions, then try again.")
                    : error is CodexException ? L.Error(error.Message) : L.T("请刷新账号或重新登录后再试。");
                var close = new Button { Content = L.T("关闭") }; close.Click += (_, _) => dialog.Close(); actions.Children.Add(close);
            }
        };
        dialog.ShowDialog();
    }
}
