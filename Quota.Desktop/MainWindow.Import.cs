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
    private void RestoreImportedNames()
    {
        var names = CcSwitchImport.Scan(CcSwitchImport.DefaultDirectory).Accounts.ToDictionary(candidate => candidate.Key, candidate => candidate.Name);
        var changed = false;
        foreach (var source in accounts.Where(source => source.Record.ImportKey is not null && !source.Record.CustomName))
            if (names.TryGetValue(source.Record.ImportKey!, out var name) && source.Record.Name != name) { source.Record.Name = name; changed = true; }
        if (changed) { try { store.SaveAccounts(accounts.Select(source => source.Record)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
    }
    private void AddMenuClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        var browser = new MenuItem { Header = L.T("在浏览器中登录") }; browser.Click += AddClick;
        var import = new MenuItem { Header = L.T("同步 CC Switch") }; import.Click += async (_, _) => await SyncCcSwitchAsync();
        menu.Items.Add(browser); menu.Items.Add(import); menu.IsOpen = true;
    }
    private async Task<AccountConnectionResult> SyncCcSwitchAsync(string? location = null)
    {
        if (demo) { ShowNotice(L.T("请正常启动应用后导入真实账号。")); return new(AccountConnectionOutcome.Demo); }
        if (loggingIn || switching || refreshing || accounts.Any(account => account.IsRefreshing)) { ShowNotice(L.T("请等待当前更新完成后再同步。")); return new(AccountConnectionOutcome.Busy); }
        loggingIn = true;
        try
        {
            var scan = CcSwitchImport.Scan(location ?? CcSwitchImport.DefaultDirectory);
            var notices = scan.Notices.Distinct().ToArray();
            if (scan.Accounts.Count == 0)
            {
                var result = new AccountConnectionResult(AccountConnectionOutcome.NoAccounts, Notices: notices);
                DialogLayout.Alert(AccountDialogOwner, L.T("同步 CC Switch"), result.Description); return result;
            }
            var names = string.Join(Environment.NewLine, scan.Accounts.Select(account => settings.Privacy ? AccountViewModel.MaskEmail(account.Name) : account.Name));
            var details = string.Join(Environment.NewLine, notices.Select(L.T));
            var description = names + Environment.NewLine + Environment.NewLine + L.T("同步会更新认证和原始名称，手动编辑的名称会保留。");
            if (details.Length > 0) description += Environment.NewLine + Environment.NewLine + details;
            if (!DialogLayout.Confirm(AccountDialogOwner, L.T("同步 CC Switch"), description, "同步账号")) return new(AccountConnectionOutcome.Canceled, Notices: notices);
            var records = CcSwitchImport.Synchronize(store, accounts.Select(source => source.Record), scan.Accounts);
            foreach (var account in accounts.Where(source => source.Managed).ToArray()) accounts.Remove(account);
            foreach (var record in records)
            {
                var account = new AccountViewModel(record);
                if (store.LoadCache(record.Id) is { } snapshot) account.SetSnapshot(snapshot, true);
                accounts.Add(account);
            }
            ApplySettings(); UpdateSummary();
            if (GetCli() is { } cli) await Task.WhenAll(accounts.Where(account => account.Record.ImportKey is not null).Select(account => RefreshAccountAsync(cli, account)));
            ShowNotice(L.F("已同步 {0} 个 CC Switch 账号。", "Synced {0} CC Switch accounts.", scan.Accounts.Count) + (details.Length > 0 ? Environment.NewLine + details : ""));
            return new(AccountConnectionOutcome.Saved, Notices: notices);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            const string message = "同步失败，请检查 CC Switch 数据及本地写入权限。";
            DialogLayout.Alert(AccountDialogOwner, L.T("同步 CC Switch"), L.T(message));
            return new(AccountConnectionOutcome.Failed, message);
        }
        finally { loggingIn = false; UpdateSummary(); }
    }
    private async void BrowseCcSwitch()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true };
        if (Directory.Exists(CcSwitchImport.DefaultDirectory)) picker.InitialDirectory = CcSwitchImport.DefaultDirectory;
        if (picker.ShowDialog(settingsWindow ?? this) != true) return;
        settingsConnectionResult = await SyncCcSwitchAsync(picker.FileName);
        QueueSettingsRender();
    }
    private void RemoveSyncedAccounts()
    {
        var imported = accounts.Where(source => source.Record.ImportKey is not null).ToArray();
        if (imported.Length == 0) return;
        if (refreshing || loggingIn || switching || imported.Any(source => source.IsRefreshing))
        {
            DialogLayout.Alert(AccountDialogOwner, L.T("暂时无法移除账号"), L.T("请等待账号更新或切换完成后再移除。")); return;
        }
        if (!DialogLayout.Confirm(settingsWindow ?? this, L.T("删除这些同步账号？"), L.T("仅移除本程序保存的账号认证副本和缓存。CC Switch 内的账号由 CC Switch 管理。"))) return;
        try
        {
            store.SaveAccounts(accounts.Except(imported).Select(source => source.Record));
            foreach (var source in imported)
            {
                try { store.DeleteProfile(source.Record.Id); accounts.Remove(source); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { source.SetError("未能完全清理本地登录目录，请再次移除或重新登录。"); }
            }
            store.SaveAccounts(accounts.Select(source => source.Record)); UpdateSummary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ShowNotice(L.T("移除操作未全部完成，请检查本地文件权限后重试。")); }
    }
}
