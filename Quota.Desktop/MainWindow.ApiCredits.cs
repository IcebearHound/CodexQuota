using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Quota.Core;

namespace Quota.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? apiPollingScope;
    private DateTimeOffset nextApiRefresh;
    private bool apiBatchRunning;
    private Window? apiLoginWindow;
    internal bool ShowApiInTaskbar => settings.ShowApiInTaskbar;
    internal bool ShowApiInFlyout => settings.ShowApiInFlyout;

    private static string ApiBalanceUnavailable => L.F("账单页未单独显示赠送余额。混合余额不会记作赠送额度。", "Billing does not show a separate promotional balance. A mixed balance is not counted as promotional credit.");

    internal void BuildApiCreditOptions(Panel body, Window owner, bool inGuide = false)
    {
        var section = SettingsControls.Section(body, L.F("API 赠送额度监控", "API credit monitoring"), "layers",
            inGuide ? "" : L.F("只读 OpenAI Platform 账单。每个组织独立显示，不会切换当前 Codex 账号。", "Read OpenAI Platform billing. Each organization appears separately and never switches the active Codex account."));
        SettingsControls.Toggle(section, L.F("监控 API 赠送额度", "Monitor API promotional credits"), "refresh-cw",
            settings.MonitorApiCredits, SetApiMonitoring,
            L.F("默认关闭。开启后，按自动刷新间隔查询账单（最短 5 分钟）。关闭后停止查询和提醒，保留连接。", "Off by default. Checks billing at your auto-refresh interval, at most once every 5 minutes. Turning it off stops checks and alerts but keeps connections."));
        var add = SettingsControls.Action(L.F("连接 API 账号", "Connect API account"), "log-in", async () => await ConnectApiAccountAsync(owner));
        add.IsEnabled = settings.MonitorApiCredits;
        section.Children.Add(add);
        if (inGuide)
            section.Children.Add(DialogLayout.Text(L.F("在独立窗口登录 OpenAI Platform。应用会自动读取当前组织的账单并返回额度面板。",
                "Sign in to OpenAI Platform in the separate window. The app reads billing for the selected organization and returns to the quota panel."), 11, true));
        else
        {
            foreach (var account in accounts.Where(account => account.Record.IsApi))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
                var button = new Button { Content = SettingsControls.Label(L.F("管理", "Manage"), "ellipsis"), Margin = new Thickness(0, 0, 0, 6) };
                AutomationProperties.SetName(button, L.F("管理 API 账号 {0}", "Manage API account {0}", account.Name));
                button.Click += (_, _) => ShowApiAccountMenu(button, account);
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
                row.Children.Add(DialogLayout.Text(account.Name + " · API", 12));
                section.Children.Add(row);
            }
        }
    }

    private void SetApiMonitoring(bool enabled)
    {
        settings.MonitorApiCredits = enabled;
        apiPollingScope?.Cancel();
        apiPollingScope?.Dispose();
        apiPollingScope = null;
        nextApiRefresh = DateTimeOffset.MinValue;
        if (!enabled) apiLoginWindow?.Close();
        if (demo)
        {
            if (enabled && accounts.All(account => !account.Record.IsApi)) AddDemoApiAccount();
        }
        else if (enabled) _ = RefreshApiAccountsAsync();
        RebuildAccountGroups();
        SaveSettings();
        QueueSettingsRender(); guideWindow?.RefreshApiCreditOptions();
    }

    private async Task RefreshApiAccountsAsync()
    {
        if (!settings.MonitorApiCredits || demo || exiting || apiBatchRunning) return;
        nextApiRefresh = DateTimeOffset.UtcNow.AddMinutes(Math.Max(5, settings.RefreshMinutes));
        var targets = accounts.Where(account => account.Record.IsApi).ToArray();
        apiBatchRunning = true;
        try
        {
            foreach (var account in targets)
            {
                if (!settings.MonitorApiCredits || exiting) break;
                await RefreshApiAccountAsync(account);
            }
        }
        finally { apiBatchRunning = false; }
    }

    private async Task RefreshApiAccountAsync(AccountViewModel account)
    {
        if (!settings.MonitorApiCredits || demo || exiting || !IsLoaded || !accounts.Contains(account) ||
            account.IsRefreshing || account.Record.ApiOrganizationId is not { } org) return;
        apiPollingScope ??= CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(apiPollingScope.Token);
        var poll = apiPollingScope.Token;
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        account.IsRefreshing = true;
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await ApiBillingBrowser.OpenProfileAsync(store, account.Record.Id);
            timeout.Token.ThrowIfCancellationRequested();
            controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(this).EnsureHandle());
            timeout.Token.ThrowIfCancellationRequested();
            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1000, 760);
            var browser = controller.CoreWebView2;
            ApiBillingBrowser.Configure(browser);
            browser.NewWindowRequested += (_, args) => args.Handled = true;
            browser.NavigationStarting += (_, args) => { if (!ApiBillingBrowser.IsReadPage(args.Uri)) args.Cancel = true; };
            var (_, reading) = await ApiBillingBrowser.ReadAsync(browser, org, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!accounts.Contains(account)) return;
            if (reading is null) account.SetError(ApiBalanceUnavailable);
            else PublishQuotaSnapshot(account, reading.ToSnapshot(org));
        }
        catch (OperationCanceledException)
        {
            if (!poll.IsCancellationRequested)
                account.SetError(L.F("API 账单读取超时，稍后可重试。", "API billing did not respond in time. Try again shortly."), timedOut: true);
        }
        catch (Exception error) when (ApiBillingBrowser.IsExpectedFailure(error))
        {
            if (!poll.IsCancellationRequested) account.SetError(ApiBillingBrowser.ErrorMessage(error));
        }
        finally
        {
            controller?.Close();
            account.IsRefreshing = false;
            if (!exiting) { UpdateSummary(); CheckNotifications(); }
        }
    }

    private async Task ConnectApiAccountAsync(Window owner, AccountViewModel? reconnect = null)
    {
        if (!settings.MonitorApiCredits || exiting || reconnect?.IsRefreshing == true) return;
        if (demo)
        {
            DialogLayout.Alert(owner, "API", L.F("已显示演示账号。请正常启动应用后连接真实账号。", "The sample account is shown. Launch without demo mode to connect a real account."));
            return;
        }
        if (apiLoginWindow is not null) { apiLoginWindow.Activate(); return; }
        var pending = reconnect?.Record ?? new();
        var committed = reconnect is not null;
        var title = L.F("连接 OpenAI API 账号", "Connect an OpenAI API account");
        var window = new Window
        {
            Title = title, Width = Math.Min(940, SystemParameters.WorkArea.Width - 24), Height = Math.Min(760, SystemParameters.WorkArea.Height - 24),
            Owner = owner.IsVisible ? owner : null, WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false, FontFamily = owner.FontFamily, Topmost = owner.Topmost
        };
        window.SetResourceReference(Window.BackgroundProperty, "Background");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        var container = new DockPanel { LastChildFill = true };
        var status = DialogLayout.Text(L.F("登录后自动读取当前组织的赠送额度。", "Sign in to automatically read promotional credits for the selected organization."), 13);
        var address = DialogLayout.Text("https://platform.openai.com", 11, true);
        var bar = new StackPanel { Margin = new Thickness(14, 10, 14, 0) };
        bar.Children.Add(status); bar.Children.Add(address);
        DockPanel.SetDock(bar, Dock.Top); container.Children.Add(bar);
        var web = new WebView2();
        container.Children.Add(web); window.Content = container;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        window.Closed += (_, _) => { apiLoginWindow = null; closed.Cancel(); };
        apiLoginWindow = window;
        if (reconnect is not null) reconnect.IsRefreshing = true;

        window.Show();
        try
        {
            var environment = await ApiBillingBrowser.OpenProfileAsync(store, pending.Id);
            if (closed.IsCancellationRequested) return;
            await web.EnsureCoreWebView2Async(environment);
            if (closed.IsCancellationRequested) return;
            var browser = web.CoreWebView2;
            ApiBillingBrowser.Configure(browser);
            browser.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                    args.Cancel = true;
                else address.Text = uri.GetLeftPart(UriPartial.Authority);
            };
            browser.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                    browser.Navigate(uri.AbsoluteUri);
            };
            var (page, reading) = await ApiBillingBrowser.WaitForConnectionAsync(browser, pending.ApiOrganizationId,
                id => accounts.Any(account => account != reconnect && account.Record.ApiOrganizationId == id),
                message => status.Text = message, closed.Token);
            closed.Token.ThrowIfCancellationRequested();
            var account = reconnect;
            if (account is null)
            {
                pending.ApiOrganizationId = page.OrganizationId;
                pending.ApiOrganizationName = AccountStore.CleanName(page.OrganizationName);
                pending.Name = pending.ApiOrganizationName;
                store.SaveAccounts(accounts.Select(item => item.Record).Append(pending));
                committed = true;
                account = new(pending) { Privacy = settings.Privacy, Compact = settings.Compact };
                accounts.Add(account);
            }
            if (reading is { } value) PublishQuotaSnapshot(account, value.ToSnapshot(page.OrganizationId!));
            else account.SetError(ApiBalanceUnavailable);
            window.Close();
            ShowConnectedApiAccount(owner, account);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (ApiBillingBrowser.IsExpectedFailure(error))
        {
            if (!closed.IsCancellationRequested)
            {
                status.Text = ApiBillingBrowser.ErrorMessage(error);
                try { await Task.Delay(Timeout.Infinite, closed.Token); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            web.Dispose();
            if (!committed) TryRemoveProfile(pending.Id);
            if (reconnect is not null) reconnect.IsRefreshing = false;
            if (!exiting) { UpdateSummary(); CheckNotifications(); QueueSettingsRender(); }
        }
    }

    private void ShowConnectedApiAccount(Window owner, AccountViewModel account)
    {
        if (owner is GuideWindow) owner.Hide();
        else if (owner == settingsWindow) owner.Close();
        ShowFromTray();
        var items = settings.SimplifiedMode ? SimplifiedAccounts : settings.Compact ? AccountList : OtherAccountList;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var group = visibleAccounts.FirstOrDefault(group => group.Sources.Contains(account));
            if (group is not null && items.ItemContainerGenerator.ContainerFromItem(group) is FrameworkElement card) card.BringIntoView();
        });
    }

    private void ShowApiAccountMenu(Button button, AccountViewModel account)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        void Item(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item(L.T("刷新此账号"), async () => await RefreshApiAccountAsync(account), settings.MonitorApiCredits && !account.IsRefreshing && !demo);
        Item(L.F("每周期赠送额度…", "Full-cycle API allowance…"), () => EditApiCycleAllowance(account));
        Item(L.T("重命名账号"), () =>
        {
            using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(account), [account]);
            RenameGroup(group);
            QueueSettingsRender();
        }, !demo);
        Item(L.F("重新连接 API 登录", "Reconnect API sign-in"), async () => await ConnectApiAccountAsync(AccountDialogOwner, account),
            settings.MonitorApiCredits && !account.IsRefreshing && !demo);
        Item(L.T("移除账号") + "…", () => { RemoveAccount(account); QueueSettingsRender(); }, !account.IsRefreshing && !demo);
        menu.IsOpen = true;
    }

    private void EditApiCycleAllowance(AccountViewModel account)
    {
        var title = L.F("每周期赠送额度", "Full-cycle API allowance");
        var dialog = DialogLayout.Create(AccountDialogOwner, title, 420);
        var body = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
        DialogLayout.Frame(dialog, body);
        body.Children.Add(DialogLayout.Text(account.Name, 18));
        body.Children.Add(DialogLayout.Text(L.F("填写完整周期的美元额度。首笔按比例发放时，进度仍按完整周期计算。留空则按账单已发放额度显示。",
            "Enter the full-period allowance in USD. A prorated first grant still uses the full period as its scale. Leave blank to use the amount issued on the bill."), 12, true));
        var label = L.F("周期上限（美元）", "Cycle allowance (USD)");
        body.Children.Add(DialogLayout.Text(label, 12));
        var input = new TextBox { Text = account.Record.ApiCycleAllowanceUsd?.ToString("0.##", CultureInfo.InvariantCulture) ?? "", MaxLength = 14 };
        AutomationProperties.SetName(input, label);
        body.Children.Add(input);
        var presets = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var amount in new[] { 100, 200, 500 })
        {
            var preset = new Button { Content = $"Pro{amount}", Margin = new Thickness(0, 0, 6, 4) };
            preset.Click += (_, _) => input.Text = amount.ToString(CultureInfo.InvariantCulture);
            presets.Children.Add(preset);
        }
        body.Children.Add(presets);
        var invalid = DialogLayout.Text(L.F("请输入大于 0 的美元金额，最多两位小数。", "Enter a positive USD amount with up to two decimal places."), 11);
        invalid.SetResourceReference(TextBlock.ForegroundProperty, "Error");
        body.Children.Add(invalid);
        var save = new Button { Content = L.T("保存"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(save);
        decimal? selected = null;
        void Validate()
        {
            var blank = string.IsNullOrWhiteSpace(input.Text);
            var parsed = decimal.TryParse(input.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount);
            var valid = blank || parsed && ApiCreditReading.IsCycleAllowanceValid(amount);
            selected = blank || !valid ? null : amount;
            invalid.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            save.IsEnabled = valid;
        }
        input.TextChanged += (_, _) => Validate();
        Validate();
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() == true) SetApiCycleAllowance(account, selected);
    }

    private void SetApiCycleAllowance(AccountViewModel account, decimal? amount)
    {
        if (!account.Record.IsApi || !accounts.Contains(account) ||
            amount is not null && !ApiCreditReading.IsCycleAllowanceValid(amount) || account.Record.ApiCycleAllowanceUsd == amount) return;
        var previous = account.Record.ApiCycleAllowanceUsd;
        account.Record.ApiCycleAllowanceUsd = amount;
        try { if (!demo) store.SaveAccounts(accounts.Select(source => source.Record)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            account.Record.ApiCycleAllowanceUsd = previous;
            ShowNotice(L.F("周期额度上限未能保存，请重试。", "Could not save the cycle allowance. Try again."));
            return;
        }
        account.RefreshDerived();
        UpdateSummary();
    }

    private void AddDemoApiAccount()
    {
        var account = new AccountViewModel(new()
        {
            Name = L.F("个人项目", "Personal projects"), ApiOrganizationName = "Personal", ApiOrganizationId = "org-demo-api"
        });
        account.SetSnapshot(new ApiCreditReading(59, 100, DateOnly.FromDateTime(DateTime.Today.AddDays(8)), 59).ToSnapshot("org-demo-api"));
        accounts.Add(account);
    }
}
