using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Quota.Desktop;
public partial class MainWindow
{
    private void AccountIssuesClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: AccountGroupViewModel group }) ShowIssues([group]);
    }
    private void SummaryIssuesClick(object sender, RoutedEventArgs args) => ShowIssues(visibleAccounts.Where(group => group.Sources.Any(source => source.HasError)).ToArray());
    private void ShowIssues(AccountGroupViewModel[] groups)
    {
        if (groups.Length == 0) return;
        var dialog = DialogLayout.Create(AccountDialogOwner, L.T("需要处理的账号"), 440);
        var root = new StackPanel { Margin = new Thickness(22, 10, 22, 22) }; DialogLayout.Frame(dialog, root);
        foreach (var group in groups)
        {
            root.Children.Add(DialogLayout.Text(group.Name, 15));
            foreach (var source in group.Sources.Where(source => source.HasError).DefaultIfEmpty(group.Sources[0]))
            {
                root.Children.Add(DialogLayout.Text(AccountGroupViewModel.SourceName(source), 10, true));
                root.Children.Add(DialogLayout.Text(source.HasError ? source.Error ?? "" : group.Status, 12, true));
                root.Children.Add(DialogLayout.Text(source.HasError ? source.ActionHint : group.ActionHint, 12));
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
                var refresh = new Button { Content = L.T("刷新此账号"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = !demo };
                refresh.Click += async (_, _) => { dialog.Close(); if (GetCli() is { } cli) await System.Threading.Tasks.Task.WhenAll(group.Sources.Select(source => RefreshAccountAsync(cli, source))); };
                actions.Children.Add(refresh);
                if (source.Managed && source.HasError)
                {
                    var login = new Button { Content = L.T("重新登录"), IsEnabled = !demo };
                    login.Click += async (_, _) => { dialog.Close(); await ReloginAsync(source); }; actions.Children.Add(login);
                }
                else { var settingsButton = new Button { Content = L.T("设置") }; settingsButton.Click += (_, _) => { dialog.Close(); SettingsClick(this, new()); }; actions.Children.Add(settingsButton); }
                root.Children.Add(actions);
            }
        }
        dialog.ShowDialog();
    }
    internal void RenameAccountGroup(AccountGroupViewModel group, string name)
    {
        if (group.Sources.Any(source => !accounts.Contains(source) || AccountGroupViewModel.KeyFor(source) != group.Key)) return;
        var records = group.Sources.Where(source => source.Managed).ToArray();
        var old = records.Select(source => (Source: source, source.Record.Name, source.Record.CustomName)).ToArray();
        var previousCurrentName = settings.CurrentAccountName;
        foreach (var source in records) { source.Record.CustomName = true; source.Rename(name); }
        if (records.Length == 0) { settings.CurrentAccountName = name; group.Sources[0].Record.CustomName = true; group.Sources[0].Rename(name); }
        try { store.SaveAccounts(accounts.Select(source => source.Record)); store.SaveSettings(settings); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            foreach (var item in old) { item.Source.Record.CustomName = item.CustomName; item.Source.Rename(item.Name); }
            settings.CurrentAccountName = previousCurrentName;
            if (records.Length == 0) group.Sources[0].Record.CustomName = previousCurrentName is not null;
            if (records.Length == 0) group.Sources[0].Rename(previousCurrentName ?? L.T("当前 Codex 账号"));
            ShowNotice(L.T("名称未保存，请重试。"));
        }
        RebuildAccountGroups();
    }
}
