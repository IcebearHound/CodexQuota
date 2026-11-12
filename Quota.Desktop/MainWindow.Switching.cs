using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Quota.Core;

namespace Quota.Desktop;
public partial class MainWindow
{
    private bool switching;
    private void HandleWidgetSwitchRequest()
    {
        if (demo || switching || loggingIn || refreshing) return;
        var request = Path.Combine(store.Root, "widget-switch-request.txt");
        if (!File.Exists(request)) return;
        try
        {
            var id = File.ReadAllText(request).Trim(); File.Delete(request);
            if (!Guid.TryParseExact(id, "N", out _)) return;
            var group = visibleAccounts.FirstOrDefault(group => group.Sources.Any(source => source.Record.Id == id));
            if (group is not null) SwitchAccountClick(new Button { Tag = group }, new());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowNotice(L.T("请刷新账号或重新登录后再试。")); }
    }
    private async void SwitchAccountClick(object sender, RoutedEventArgs args)
    {
        if (switching || loggingIn || sender is not Button { Tag: AccountGroupViewModel group }) return;
        if (group.IsCurrent) return;
        var target = group.Sources.Where(source => source.Managed).OrderBy(source => source.HasError).ThenByDescending(source => source.Snapshot?.FetchedAt).FirstOrDefault();
        if (target is null || (!demo && GetCli() is null)) return;
        switching = true;
        var dialog = DialogLayout.Create(AccountDialogOwner, L.T("正在切换"), 420);
        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) }; DialogLayout.Frame(dialog, root);
        var heading = DialogLayout.Text(L.T("正在切换"), 20); root.Children.Add(heading);
        var message = DialogLayout.Text(L.T("正在验证账号并更新认证，请稍候。"), 12, true); root.Children.Add(message);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 10, 0, 18) }; root.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(actions);
        var completed = false;
        dialog.Closing += (_, eventArgs) => { if (!completed) eventArgs.Cancel = true; };
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var cli = demo ? null : GetCli()!;
                if (demo) await System.Threading.Tasks.Task.Delay(650);
                else
                {
                    await Task.WhenAll(accountRefreshes.Where(pair => pair.Key.Record.IsCurrent || ReferenceEquals(pair.Key, target)).Select(pair => pair.Value).ToArray());
                    var verified = await new AccountSwitcher(store).SwitchAsync(cli!, target.Record, lifetime.Token);
                    var local = accounts.FirstOrDefault(source => source.Record.IsCurrent);
                    if (verified is not null && local is not null)
                    {
                        local.SetSnapshot(verified, fromCache: true);
                        local.SetError("等待验证当前登录。");
                        try { store.SaveCache(local.Record.Id, verified); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowNotice(L.T("额度已更新，但本地缓存未能保存。")); }
                    }
                }
                completed = true; progress.Visibility = Visibility.Collapsed;
                heading.Text = L.T("账号已切换"); dialog.Title = heading.Text;
                message.Text = L.T("需要重启 Codex 才能加载新账号。重启会中断当前会话，会话需要在重启后手动恢复。");
                var later = new Button { Content = L.T("稍后重启"), Margin = new Thickness(0, 0, 8, 0) }; later.Click += (_, _) => dialog.Close();
                var restart = new Button { Content = L.T("立即重启") };
                restart.Click += async (_, _) =>
                {
                    restart.IsEnabled = false; later.IsEnabled = false;
                    try { if (!demo && !await CodexDesktopRestart.RestartAsync(lifetime.Token)) { message.Text = L.T("未找到可用的 Codex 注册启动入口，请从开始菜单启动 Codex。"); later.IsEnabled = true; return; } dialog.Close(); }
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException or OperationCanceledException) { message.Text = L.T("Codex 未能自动重启，请手动启动并恢复会话。"); later.IsEnabled = true; }
                };
                actions.Children.Add(later); actions.Children.Add(restart);
                var current = accounts.FirstOrDefault(source => source.Record.IsCurrent);
                if (!demo && current is not null) await RefreshAccountTrackedAsync(cli!, current);
            }
            catch (Exception error) when (error is CodexException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
            {
                completed = true; progress.Visibility = Visibility.Collapsed;
                heading.Text = L.T("切换未完成"); message.Text = error is CodexException ? L.Error(error.Message) : L.T("请刷新账号或重新登录后再试。");
                var close = new Button { Content = L.T("关闭") }; close.Click += (_, _) => dialog.Close(); actions.Children.Add(close);
            }
        };
        try { dialog.ShowDialog(); } finally { switching = false; }
    }
}
