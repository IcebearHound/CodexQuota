using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Quota.Core;
using Forms = System.Windows.Forms;

namespace Quota.Desktop;
public partial class MainWindow : Window
{
    private readonly AccountStore store;
    private readonly CodexService service;
    private readonly WidgetSettings settings;
    private readonly ObservableCollection<AccountViewModel> accounts = [];
    private readonly ObservableCollection<AccountGroupViewModel> visibleAccounts = [];
    private readonly HashSet<AccountViewModel> observedSources = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim concurrency = new(3);
    private readonly Dictionary<AccountViewModel, Task> accountRefreshes = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Forms.NotifyIcon tray;
    private readonly bool demo;
    private bool refreshing, exiting, loggingIn, noticeIsCli;
    private bool nativeWindowLost, visibleBeforeNativeLoss;
    private bool closedHandled;
    internal bool IsExiting => exiting;
    private readonly DesktopWindowHost desktopHost = new();
    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
    public MainWindow(bool demo)
    {
        InitializeComponent(); this.demo = demo;
        SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(NativeWindowMessage);
            if (source is not null) source.Disposed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (settings is not { DesktopOnly: true } || exiting || WindowPlacementGuard.IsAlive(this)) return;
                nativeWindowLost = true; visibleBeforeNativeLoss = Visibility == Visibility.Visible;
                MainClosed(this, EventArgs.Empty);
            });
        };
        var arguments = Environment.GetCommandLineArgs();
        var dataIndex = Array.IndexOf(arguments, "--data-dir");
        var root = dataIndex >= 0 && arguments.Length > dataIndex + 1 ? arguments[dataIndex + 1] : demo ? Path.Combine(Path.GetTempPath(), "CodexQuotaDemo") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget");
        store = new(root); service = new(store); settings = store.LoadSettings();
        Width = settings.Width; Height = settings.Height;
        if (settings.Left is { } l && settings.Top is { } t) { WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; }
        L.Apply(settings.Language);
        accounts.Add(new(new() { Id = "current", Name = settings.CurrentAccountName ?? L.T("当前 Codex 账号"), CustomName = settings.CurrentAccountName is not null }));
        if (!demo) foreach (var record in store.LoadAccounts()) accounts.Add(new(record));
        if (!demo) RestoreImportedNames();
        foreach (var account in accounts) { var cached = store.LoadCache(account.Record.Id); if (cached is not null) { account.SetSnapshot(cached, fromCache: true); if (account.Record.IsCurrent) account.SetError("等待验证当前登录。"); } }
        accounts.CollectionChanged += (_, _) => RebuildAccountGroups();
        RebuildAccountGroups();
        AccountList.ItemsSource = visibleAccounts;
        SimplifiedAccounts.ItemsSource = visibleAccounts;
        ApplySimplifiedLayout();
        tray = new Forms.NotifyIcon { Text = L.T("Codex Quota · 额度监控"), Icon = MakeIcon(), Visible = true };
        tray.MouseUp += (_, args) => { if (args.Button == Forms.MouseButtons.Right) Dispatcher.BeginInvoke(ShowTrayMenu); };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        PreviewKeyDown += OnKeyDown;
        MouseMove += (_, args) => { if (settings.DesktopOnly && args.LeftButton == MouseButtonState.Pressed) desktopHost.MoveDrag(this); };
        MouseLeftButtonUp += (_, _) => { desktopHost.EndDrag(this); SavePlacement(); };
        Loaded += async (_, _) =>
        {
            WindowPlacementGuard.EnsureVisible(this); ApplySettings();
            if (demo) { LoadDemo(); if (Array.IndexOf(arguments, "--guide") >= 0) ShowUserGuide(); }
            else
            {
                if (store.RecoveryNotice is { } notice) ShowNotice(notice);
                if (settings.GuideVersion < 1) ShowUserGuide();
                startupRefresh = RefreshAllAsync(); await startupRefresh;
            }
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        timer.Tick += async (_, _) => { foreach (var a in accounts) a.UpdateClock(); UpdateSummary(); CheckNotifications(); if (!demo && DateTimeOffset.UtcNow >= nextRefresh) await RefreshAllAsync(); };
        timer.Start();
        Closing += OnClosing;
        Closed += MainClosed;
    }
    private nint NativeWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0002 && settings.DesktopOnly && !exiting)
        {
            nativeWindowLost = true; visibleBeforeNativeLoss = Visibility == Visibility.Visible;
        }
        return 0;
    }
    private void MainClosed(object? sender, EventArgs args)
    {
        if (closedHandled) return;
        closedHandled = true;
        var recover = nativeWindowLost && !exiting;
        exiting = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        trayMenu?.SetCurrentValue(System.Windows.Controls.ContextMenu.IsOpenProperty, false); trayMenuAnchor?.Close();
        settingsWindow?.Close(); sessionIssuesWindow?.Close(); guideWindow?.Close();
        foreach (var group in visibleAccounts) group.Dispose();
        foreach (var source in observedSources) source.PropertyChanged -= AccountSourceChanged;
        lifetime.Cancel(); activityTimer.Stop(); taskbarMonitor.Stop(); taskbarWidget?.Close(); tray.Visible = false;
        var icon = tray.Icon; tray.Dispose(); icon?.Dispose(); timer.Stop();
        if (recover && System.Windows.Application.Current is App app) app.RecoverDesktopWindow(this, demo, visibleBeforeNativeLoss);
        else System.Windows.Application.Current.Shutdown();
    }
    private static Icon MakeIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/CodexQuota;component/Assets/app.ico"));
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
    private void ApplySettings()
    {
        WindowViewModel.AlertThreshold = settings.LowQuotaThreshold;
        ThemeService.Apply(settings.Theme);
        Topmost = settings.Topmost && !settings.DesktopOnly;
        if (settingsWindow is { } preferences) preferences.Topmost = Topmost;
        if (guideWindow is { } guide) guide.Topmost = Topmost;
        PinButton.Foreground = (System.Windows.Media.Brush)FindResource(settings.Topmost ? "Accent" : "Muted");
        PinButton.ToolTip = settings.Topmost ? L.T("取消置顶") : L.T("置顶窗口");
        foreach (var a in accounts) { a.Privacy = settings.Privacy; a.Compact = settings.Compact; }
        foreach (var group in visibleAccounts) group.ShowUsed = settings.ShowUsed;
        CompactPanel.Visibility = settings.Compact ? Visibility.Visible : Visibility.Collapsed;
        NormalPanel.Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded) ApplyDesktopWindowMode();
        UpdateSummary();
        ApplySimplifiedLayout();
        if (IsLoaded) { ApplyActivityMonitor(); ApplyTaskbarWidget(); }
        tray.Text = "Codex Quota";

    }
    private void SaveSettings() { try { store.SaveSettings(settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowNotice(L.T("设置未能保存，请检查本地文件权限。")); } }
    private void ShowNotice(string message, bool cli = false) { noticeIsCli = cli; Notice.Text = L.T(message); NoticeBorder.Visibility = Visibility.Visible; }
    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBorder.Visibility = Visibility.Collapsed;
    private string? GetCli()
    {
        var path = CliLocator.Find(settings.CodexPath);
        if (path is null) ShowNotice(L.T("未找到 Codex。请打开设置，选择已安装的 codex.exe。"), true);
        else if (noticeIsCli) { NoticeBorder.Visibility = Visibility.Collapsed; noticeIsCli = false; }
        return path;
    }
    private async Task RefreshAllAsync()
    {
        if (demo || refreshing || switching || exiting) return;
        var cli = GetCli(); if (cli is null) { nextRefresh = DateTimeOffset.UtcNow.AddMinutes(settings.RefreshMinutes); foreach (var a in accounts) a.SetError("未找到 Codex，请在设置中选择 codex.exe。"); UpdateSummary(); return; }
        refreshing = true; RefreshButton.IsEnabled = false;
        nextRefresh = DateTimeOffset.UtcNow.AddMinutes(settings.RefreshMinutes);
        try { await Task.WhenAll(accounts.ToArray().Where(a => !a.IsRefreshing).Select(a => RefreshAccountAsync(cli, a))); }
        finally
        {
            refreshing = false;
            if (!exiting) { RefreshButton.IsEnabled = true; UpdateSummary(); CheckNotifications(); HandleWidgetSwitchRequest(); }
        }
    }
    internal async Task RefreshFromWidgetAsync() { await RefreshAllAsync(); HandleWidgetSwitchRequest(); }
    private Task RefreshAccountAsync(string cli, AccountViewModel account)
        => switching ? Task.CompletedTask : RefreshAccountTrackedAsync(cli, account);
    private async Task RefreshAccountTrackedAsync(string cli, AccountViewModel account)
    {
        if (lifetime.IsCancellationRequested) return;
        if (accountRefreshes.TryGetValue(account, out var pending) && !pending.IsCompleted) { await pending; return; }
        var operation = RefreshAccountCoreAsync(cli, account);
        accountRefreshes[account] = operation;
        try { await operation; }
        finally
        {
            if (accountRefreshes.TryGetValue(account, out var tracked) && ReferenceEquals(operation, tracked)) accountRefreshes.Remove(account);
        }
    }
    private async Task RefreshAccountCoreAsync(string cli, AccountViewModel account)
    {
        if (account.IsRefreshing || lifetime.IsCancellationRequested) return;
        account.IsRefreshing = true; UpdateSummary();
        try
        {
            await concurrency.WaitAsync(lifetime.Token);
            UsageSnapshot snapshot;
            try { snapshot = await service.RefreshAsync(cli, account.Record, lifetime.Token); }
            finally { concurrency.Release(); }
            if (lifetime.IsCancellationRequested || !accounts.Contains(account)) return;
            account.SetSnapshot(snapshot);
            try { store.SaveCache(account.Record.Id, snapshot); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowNotice(L.T("额度已更新，但本地缓存未能保存。")); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is CodexException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { if (accounts.Contains(account)) account.SetError(e is CodexException ? e.Message : "读取未完成，请重试或重新登录。"); }
        finally { account.IsRefreshing = false; if (!exiting) { UpdateSummary(); CheckNotifications(); } }
    }
    private void UpdateSummary()
    {
        UpdateCodexStatus();
        var busy = visibleAccounts.Count(account => account.IsRefreshing);
        var low = visibleAccounts.Count(account => account.HasLowQuota);
        var errors = visibleAccounts.Sum(group => group.Sources.Count(source => source.HasError));
        Summary.Text = L.F("{0} 个账号", "{0} accounts", visibleAccounts.Count) + (busy > 0 ? L.F(" · {0} 个同步中", " · {0} updating", busy) : errors > 0 ? L.F(" · {0} 个来源需处理", errors == 1 ? " · {0} source needs attention" : " · {0} sources need attention", errors) : low > 0 ? L.F(" · {0} 个额度偏低", " · {0} low", low) : L.F(" · 已同步", " · up to date"));
        Summary.ToolTip = string.Join(Environment.NewLine, visibleAccounts.SelectMany(group => group.Sources.Where(source => source.HasError).Select(source => group.Name + " · " + AccountGroupViewModel.SourceName(source) + " · " + source.ActionHint)));
        PublishWidgetData();
    }
    private void RebuildAccountGroups()
    {
        foreach (var source in observedSources.Where(source => !accounts.Contains(source)).ToArray())
        {
            source.PropertyChanged -= AccountSourceChanged;
            observedSources.Remove(source);
        }
        foreach (var source in accounts)
            if (observedSources.Add(source)) source.PropertyChanged += AccountSourceChanged;
        foreach (var group in visibleAccounts) group.Dispose();
        visibleAccounts.Clear();
        foreach (var group in AccountGroupViewModel.Build(accounts)) { group.ShowUsed = settings.ShowUsed; visibleAccounts.Add(group); }
        var current = visibleAccounts.FirstOrDefault(group => group.Sources.Any(source => source.Record.IsCurrent)) ?? visibleAccounts.FirstOrDefault();
        FocusAccount.Content = current;
        OtherAccountList.ItemsSource = visibleAccounts.Where(group => group != current).ToArray();
        UpdateSummary();
        if (settings.SimplifiedMode) ApplySimplifiedLayout();
    }
    private void AccountSourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AccountViewModel.Identity) || sender is not AccountViewModel source) return;
        var group = visibleAccounts.FirstOrDefault(group => group.Sources.Contains(source));
        if (group?.Key != AccountGroupViewModel.KeyFor(source)) RebuildAccountGroups();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAllAsync();
    private void TogglePin(object sender, RoutedEventArgs e) { settings.DesktopOnly = false; settings.Topmost = !settings.Topmost; ApplySettings(); SaveSettings(); }
    private void PanelDrag(object sender, MouseButtonEventArgs args)
    {
        if (settings.SimplifiedMode && args.ClickCount == 2) { SetSimplifiedMode(false); args.Handled = true; return; }
        var element = args.OriginalSource as System.Windows.DependencyObject;
        while (element is not null && element != this)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar or TextBox or AccountName) return;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        if (args.LeftButton != MouseButtonState.Pressed) return;
        if (settings.DesktopOnly) { desktopHost.BeginDrag(this); args.Handled = true; return; }
        try { DragMove(); SavePlacement(); args.Handled = true; } catch (InvalidOperationException) { }
    }
    private void TitleDrag(object sender, MouseButtonEventArgs e) => PanelDrag(sender, e);
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void MinimizeWindow(object sender, RoutedEventArgs e) => Hide();
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; WindowPlacementGuard.EnsureVisible(this); Activate(); }
    private void DisplaySettingsChanged(object? sender, EventArgs args)
    {
        if (!exiting && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            if (WindowPlacementGuard.EnsureVisible(this)) SavePlacement();
            if (settingsWindow is { } preferences) WindowPlacementGuard.EnsureVisible(preferences);
        });
    }
    private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs args)
    {
        if (settings.Theme == "system" && !exiting) Dispatcher.BeginInvoke(() => ApplySettings());
    }
    private void Exit() { exiting = true; Close(); System.Windows.Application.Current.Shutdown(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        if (!exiting && settings.CloseToTray) { e.Cancel = true; Hide(); return; }
        exiting = true; lifetime.Cancel();
    }
    private void SavePlacement()
    {
        if (!IsLoaded || !WindowPlacementGuard.TryGetBounds(this, out _)) return;
        var position = desktopHost.GetPosition(this);
        if (!settings.SimplifiedMode) { settings.Width = ActualWidth; settings.Height = ActualHeight; }
        settings.Left = position.X; settings.Top = position.Y;
        SaveSettings();
    }
    private async void AddClick(object sender, RoutedEventArgs e) => await AddAccountAsync();
    private async Task<AccountConnectionResult> AddAccountAsync()
    {
        if (demo) { ShowNotice(L.T("当前为演示模式；正常启动后可连接真实账号。")); return new(AccountConnectionOutcome.Demo); }
        if (loggingIn || switching)
        {
            var result = new AccountConnectionResult(AccountConnectionOutcome.Busy);
            ShowNotice(result.Description); return result;
        }
        var cli = GetCli(); if (cli is null) return new(AccountConnectionOutcome.CliMissing);
        var record = new AccountRecord(); var committed = false;
        try
        {
            loggingIn = true; store.CreateProfile(record.Id);
            var dialog = new LoginDialog(AccountDialogOwner, L.T("账号 ") + accounts.Count, false, lifetime.Token, (progress, ct) => service.LoginAsync(cli, record.Id, progress, ct));
            if (dialog.ShowDialog() != true) return new(AccountConnectionOutcome.Canceled);
            record.Name = AccountStore.CleanName(dialog.AccountName); record.Email = dialog.Email;
            // Persist before exposing a new account to the UI.
            store.SaveAccounts(accounts.Select(a => a.Record).Append(record));
            committed = true; var account = new AccountViewModel(record); accounts.Add(account); ApplySettings(); await RefreshAccountAsync(cli, account);
            return new(AccountConnectionOutcome.Saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CodexException)
        {
            var message = ex is CodexException ? ex.Message : "账号保存失败，请检查本地文件权限。";
            ShowNotice(L.Error(message)); return new(AccountConnectionOutcome.Failed, message);
        }
        finally { if (!committed) TryRemoveProfile(record.Id); loggingIn = false; UpdateSummary(); }
    }
    private void TryRemoveProfile(string id) { try { store.DeleteProfile(id); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowNotice(L.T("未完成登录的目录暂时无法清理。可稍后重试。")); } }
    private void AccountMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountGroupViewModel group } button) return;
        var menu = new ContextMenu { Background = (System.Windows.Media.Brush)FindResource("Surface"), Foreground = (System.Windows.Media.Brush)FindResource("Text") };
        void Item(string text, Action action, bool enabled = true) { var item = new MenuItem { Header = text, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item(L.T("刷新此账号"), async () => { if (GetCli() is { } cli) await Task.WhenAll(group.Sources.Select(source => RefreshAccountAsync(cli, source))); }, !demo && !group.IsRefreshing);
        var managed = group.Sources.Where(source => source.Managed).ToArray();
        if (managed.Length > 0)
        {
            Item(L.T("重命名账号"), () => RenameGroup(group), !demo);
            foreach (var source in managed)
            {
                var suffix = group.HasMultipleSources ? " · " + AccountGroupViewModel.SourceName(source) + (managed.Length > 1 ? "（" + source.Name + "）" : "") : "";
                Item(L.T("重新登录") + suffix, async () => await ReloginAsync(source), !demo && !source.IsRefreshing && !loggingIn && !switching);
                Item((group.HasMultipleSources ? L.T("移除来源") : L.T("移除账号")) + suffix + "…", () => RemoveAccount(source), !demo && !source.IsRefreshing && !loggingIn && !switching);
            }
        }
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void RenameGroup(AccountGroupViewModel group)
    {
        if (DialogLayout.AskName(AccountDialogOwner, L.T("重命名账号"), group.EditableName) is not { } name) return;
        RenameAccountGroup(group, name);
    }
    private async Task ReloginAsync(AccountViewModel account)
    {
        var cli = GetCli(); if (cli is null || loggingIn || switching || account.IsRefreshing) return;
        var pending = new AccountRecord(); var committed = false;
        try
        {
            loggingIn = true; account.IsRefreshing = true; store.CreateProfile(pending.Id);
            var dialog = new LoginDialog(AccountDialogOwner, account.Name, true, lifetime.Token, (progress, ct) => service.LoginAsync(cli, pending.Id, progress, ct));
            if (dialog.ShowDialog() == true)
            {
                var replacement = CommitRelogin(account, pending, dialog.AccountName, dialog.Email); committed = true;
                TryRemoveProfile(account.Record.Id); ApplySettings(); await RefreshAccountAsync(cli, replacement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CodexException)
        {
            ShowNotice(ex is CodexException ? L.Error(ex.Message) : L.T("账号保存失败，请检查本地文件权限。"));
        }
        finally { if (!committed) TryRemoveProfile(pending.Id); account.IsRefreshing = false; loggingIn = false; }
    }
    private AccountViewModel CommitRelogin(AccountViewModel account, AccountRecord pending, string name, string? email)
    {
        pending.Name = name == account.Name ? account.Record.Name : AccountStore.CleanName(name);
        pending.Email = email;
        pending.CustomName = account.Record.CustomName || pending.Name != account.Record.Name;
        var imported = CcSwitchImport.Scan(Path.Combine(store.ProfilePath(pending.Id), "auth.json")).Accounts;
        pending.ImportKey = imported.Any(candidate => candidate.Key == account.Record.ImportKey) ? account.Record.ImportKey : null;
        store.SaveAccounts(accounts.Select(source => source == account ? pending : source.Record));
        var replacement = new AccountViewModel(pending) { Privacy = settings.Privacy, Compact = settings.Compact };
        accounts[accounts.IndexOf(account)] = replacement;
        return replacement;
    }
    private void RemoveAccount(AccountViewModel account)
    {
        if (loggingIn || switching || account.IsRefreshing)
        {
            DialogLayout.Alert(AccountDialogOwner, L.T("暂时无法移除账号"), L.T("请等待账号更新或切换完成后再移除。")); return;
        }
        if (!DialogLayout.Confirm(AccountDialogOwner, L.T("移除账号"), L.F(L.T("移除“{0}”及本程序保存的认证和缓存？"), "Remove “{0}” and its credentials and cache saved by this app?", account.Name))) return;
        try
        {
            store.SaveAccounts(accounts.Where(a => a != account).Select(a => a.Record));
            try { store.DeleteProfile(account.Record.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Keep the row available for retry when credential cleanup fails.
                store.SaveAccounts(accounts.Select(a => a.Record));
                account.SetError("未能完全清理本地登录目录，请再次移除或重新登录。"); UpdateSummary(); return;
            }
            accounts.Remove(account); UpdateSummary();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowNotice(L.T("移除操作未全部完成，请检查本地文件权限后重试。")); }
    }
    private async void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (settings.SimplifiedMode && e.Key == Key.Escape) { SetSimplifiedMode(false); e.Handled = true; return; }
        if (e.Key == Key.F5) { e.Handled = true; await RefreshAllAsync(); }
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; AddClick(this, new()); }
    }
    private void LoadDemo()
    {
        DemoBadge.Visibility = Visibility.Visible; accounts.Clear();
        AccountViewModel Sample(string id, string name, double primary, double weekly)
        {
            var account = new AccountViewModel(new() { Id = id, Name = name, Email = id + "@example.com" });
            account.SetSnapshot(new() { Email = id + "@example.com", AccountId = id, Buckets = [new() { Name = "Codex", Plan = primary < 30 ? "prolite" : primary > 80 ? "pro" : "promax", Windows = [new() { UsedPercent = primary, DurationMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddHours(2.5).ToUnixTimeSeconds() }, new() { UsedPercent = weekly, DurationMinutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(3.5).ToUnixTimeSeconds() }] }] }); return account;
        }
        accounts.Add(Sample("current", L.T("日常工作"), 22, 47)); accounts.Add(Sample(Guid.NewGuid().ToString("N"), L.T("个人项目"), 86, 64));
        var offline = Sample(Guid.NewGuid().ToString("N"), L.T("备用账号"), 43, 68); offline.SetError("连接暂时不可用。保留上次额度，稍后可重试。"); accounts.Add(offline);
        accounts.Add(Sample(Guid.NewGuid().ToString("N"), "Pro500", 98, 80));
        ApplySettings(); UpdateSummary();
    }
}
