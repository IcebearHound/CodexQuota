using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Quota.Desktop;

public partial class MainWindow
{
    private Window? settingsWindow;
    private string settingsPage = "外观";
    private bool narrowSettings;
    private bool settingsRenderPending;
    private AccountConnectionResult? settingsConnectionResult;
    private Window AccountDialogOwner => settingsWindow is { IsVisible: true } preferences ? preferences : this;

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is { } existing) { existing.Activate(); return; }
        var dialog = DialogLayout.Create(null, L.T("设置"), 860);
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.Height = Math.Min(760, SystemParameters.WorkArea.Height - 32);
        dialog.ResizeMode = ResizeMode.CanResize;
        dialog.MinWidth = Math.Min(320, dialog.Width);
        dialog.MinHeight = Math.Min(380, dialog.Height);
        dialog.ShowInTaskbar = true;
        dialog.Topmost = Topmost;
        settingsWindow = dialog;
        dialog.Closed += (_, _) => { settingsWindow = null; L.LanguageChanged -= SettingsLanguageChanged; };
        L.LanguageChanged += SettingsLanguageChanged;
        dialog.SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && dialog.IsLoaded && (dialog.ActualWidth < 640) != narrowSettings) QueueSettingsRender();
        };
        dialog.Activated += (_, _) =>
        {
            if (settingsPage == "小组件") { QueueSettingsRender(); taskbarWidget?.RefreshPlacement(); }
            else if (settingsPage == "通用") QueueSettingsRender();
        };
        RenderSettings(); dialog.Show();
    }

    private void SettingsLanguageChanged(object? sender, EventArgs args) => QueueSettingsRender();
    private void QueueSettingsRender()
    {
        if (settingsRenderPending || settingsWindow is null) return;
        settingsRenderPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            settingsRenderPending = false;
            if (settingsWindow is not null) RenderSettings();
        });
    }
    private void CommitSettings() { ApplySettings(); SaveSettings(); }
    private void OpenSettingsPage(string page) { settingsPage = page; RenderSettings(); }

    private void RenderSettings()
    {
        if (settingsWindow is not { } dialog) return;
        dialog.Title = L.T("设置");
        narrowSettings = (dialog.IsLoaded ? dialog.ActualWidth : dialog.Width) < 640;
        var tabs = new UniformGrid { Columns = narrowSettings ? 2 : 1, VerticalAlignment = VerticalAlignment.Top };
        foreach (var (page, icon) in new[] { ("外观", "palette"), ("小组件", "layout-grid"), ("账号", "log-in"), ("通知", "bell"), ("状态检测", "activity"), ("额度监测", "rotate-cw"), ("通用", "settings-2") })
        {
            var tab = new RadioButton { Content = SettingsControls.Label(page, icon), IsChecked = settingsPage == page, GroupName = "settings-pages", Style = (Style)FindResource("SettingsNavigation") };
            AutomationProperties.SetName(tab, L.T(page));
            tab.Checked += (_, _) => OpenSettingsPage(page); tabs.Children.Add(tab);
        }
        var body = new StackPanel();
        var title = DialogLayout.Text(settingsPage, 25); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(title);
        var description = settingsPage switch
        {
            "外观" => L.F("调整主题、显示方式与窗口层级。", "Choose your theme, display and window behavior."),
            "小组件" => L.F("任务栏小组件的开关、位置和精简布局。", "Taskbar widget visibility, position and simplified layout."),
            "账号" => L.F("连接账号，管理认证来源与隐私。", "Connect accounts and manage authentication and privacy."),
            "通知" => L.F("设置额度与重置卡提醒。", "Configure quota and reset-credit alerts."),
            "状态检测" => L.F("选择状态查看灯色、动画与含义。", "Select a state to see its light, animation and meaning."),
            "额度监测" => L.F("通过示例了解额度口径与独立重置窗口。", "Explore quota display and independent reset windows with examples."),
            _ => L.F("语言、刷新频率与 Codex 程序。", "Language, refresh frequency and the Codex executable.")
        };
        var subtitle = DialogLayout.Text(description, 12, true); subtitle.Margin = new Thickness(0, 0, 0, 22); body.Children.Add(subtitle);
        switch (settingsPage)
        {
            case "外观": BuildAppearanceSettings(body); break;
            case "小组件": BuildWidgetSettings(body); break;
            case "账号": BuildAccountSettings(body); break;
            case "通知": BuildNotificationSettings(body); break;
            case "状态检测":
                body.Children.Add(SettingsControls.Action(L.F("异常会话", "Session issues"), "activity", ShowSessionIssues));
                body.Children.Add(new StatusPreviewPanel()); break;
            case "额度监测":
                body.Children.Add(new QuotaPreviewPanel(settings.ShowUsed));
                body.Children.Add(SettingsControls.Action("额度口径", "settings-2", () => OpenSettingsPage("外观")));
                break;
            default: BuildGeneralSettings(body); break;
        }
        SettingsFrame(dialog, body, tabs, narrowSettings);
    }

    private void SettingChoices(Panel parent, string title, string icon, string[] labels, string[] values, string[] icons, string selected, Action<string> change, string description = "")
    {
        var section = SettingsControls.Section(parent, title, icon, description);
        SettingsControls.Choices(section, title, labels, values, icons, selected, value => { change(value); CommitSettings(); });
    }

    private void BuildAppearanceSettings(Panel body)
    {
        SettingChoices(body, "主题", "palette", ["跟随系统", "深色", "浅色"], ["system", "dark", "light"], ["monitor", "moon", "sun"], settings.Theme, value => settings.Theme = value);
        SettingChoices(body, "显示模式", "layout-grid", ["紧凑圆环", "圆环 + 账号条"], ["compact", "normal"], ["layout-grid", "activity"], settings.Compact ? "compact" : "normal", value =>
        {
            settings.Compact = value == "compact";
            settings.Width = settings.Compact ? 400 : 560; settings.Height = settings.Compact ? 464 : 350;
            if (!settings.SimplifiedMode) { Width = settings.Width; Height = settings.Height; }
        });
        SettingChoices(body, "主面板精简布局", "layout-grid", ["圆环模式", "状态条模式"], ["ring", "bar"], ["rotate-cw", "activity"], settings.SimplifiedLayout, value => settings.SimplifiedLayout = value);
        body.Children.Add(SettingsControls.Action(settings.SimplifiedMode ? "返回完整界面" : "精简模式", "minimize-2", () => { SetSimplifiedMode(!settings.SimplifiedMode); QueueSettingsRender(); }));
        SettingChoices(body, "额度口径", "rotate-cw", ["剩余用量", "已用量"], ["remaining", "used"], ["minus", "plus"], settings.ShowUsed ? "used" : "remaining", value => settings.ShowUsed = value == "used");
        SettingChoices(body, "窗口位置", "pin", ["始终置顶", "普通窗口", "仅桌面"], ["top", "normal", "desktop"], ["pin", "layout-grid", "monitor"], settings.DesktopOnly ? "desktop" : settings.Topmost ? "top" : "normal", value =>
        {
            settings.DesktopOnly = value == "desktop"; settings.Topmost = value == "top";
        });
        body.Children.Add(SettingsControls.Action("设置小组件位置", "monitor", () => OpenSettingsPage("小组件")));
    }

    private void BuildWidgetSettings(Panel body)
    {
        var placement = SettingsControls.Section(body, L.F("任务栏小组件", "Taskbar widget"), "monitor", L.F("位置与布局实时生效，独立于主面板精简布局。", "Position and layout apply immediately, independently of the main compact layout."));
        placement.Children.Add(new WidgetPlacementPanel(this));
        body.Children.Add(SettingsControls.Action("展开任务栏账号面板", "chevron-right", () => taskbarWidget?.ShowAccounts()));
        var system = SettingsControls.Section(body, "Windows 小组件（天气）", "sun");
        var state = WindowsTaskbarSettings.WidgetsVisible;
        system.Children.Add(DialogLayout.Text(state == true ? L.F("系统入口已开启", "The Windows Widgets entry is on") : state == false ? L.F("系统入口已关闭", "The Windows Widgets entry is off") : L.F("系统入口状态暂不可读", "The Windows Widgets state is unavailable"), 12));
        system.Children.Add(DialogLayout.Text(L.F("在 Windows 任务栏设置中调整天气入口，返回后自动更新状态与圆环位置。", "Change the weather entry in Windows Taskbar settings. Its status and the ring position update when you return."), 11, true));
        system.Children.Add(SettingsControls.Action("打开 Windows 任务栏设置", "monitor", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { ShowNotice(L.T("无法打开 Windows 任务栏设置，请从 Windows 设置中打开个性化和任务栏。")); }
        }));
    }

    private void BuildAccountSettings(Panel body)
    {
        var connections = SettingsControls.Section(body, L.F("连接账号", "Connect accounts"), "log-in");
        connections.Children.Add(SettingsControls.ConnectionCard("download", L.F("从 CC Switch 导入", "Import from CC Switch"), L.F("同步已有认证和原始名称，并保留手动编辑的名称。", "Sync existing credentials and original names, preserving your custom names."), async () => await SettingsConnectionAsync(true)));
        connections.Children.Add(SettingsControls.ConnectionCard("log-in", L.F("原生登录", "Sign in with Codex"), L.F("通过官方浏览器页面登录，为每个账号独立保存认证。", "Sign in through the official browser flow. Each account has its own credentials."), async () => await SettingsConnectionAsync(false)));
        var actions = new WrapPanel();
        actions.Children.Add(SettingsControls.Action("选择认证 JSON 文件", "folder-search", BrowseCcSwitch));
        actions.Children.Add(SettingsControls.Action("删除同步账号", "x", () => { RemoveSyncedAccounts(); QueueSettingsRender(); })); connections.Children.Add(actions);
        if (settingsConnectionResult is { } result) connections.Children.Add(DialogLayout.Text(result.Description, 12, true));
        var management = SettingsControls.Section(body, "管理账号", "log-in");
        foreach (var group in visibleAccounts.ToArray())
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 10) };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var details = new StackPanel();
            var name = DialogLayout.Text(group.Name, 12); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 4); details.Children.Add(name);
            var status = DialogLayout.Text(group.Plan + " · " + group.Status, 11, true); status.Margin = new Thickness(0); details.Children.Add(status); row.Children.Add(details);
            var manage = new Button { Content = new AppIcon { Kind = "ellipsis" }, Style = (Style)FindResource("IconButton"), Tag = group, ToolTip = L.T("账号操作") };
            AutomationProperties.SetName(manage, L.F("管理 {0}", "Manage {0}", group.Name));
            manage.Click += AccountMenu; Grid.SetColumn(manage, 1); row.Children.Add(manage); management.Children.Add(row);
        }
        SettingsControls.Toggle(management, "隐藏账号邮箱", "eye-off", settings.Privacy, value => { settings.Privacy = value; CommitSettings(); QueueSettingsRender(); });
    }

    private async Task SettingsConnectionAsync(bool import)
    {
        var dialog = settingsWindow;
        if (dialog is null) return;
        settingsConnectionResult = await ConnectFromGuideAsync(import);
        if (dialog == settingsWindow) QueueSettingsRender();
    }

    private void BuildNotificationSettings(Panel body)
    {
        var quota = SettingsControls.Section(body, L.F("额度提醒", "Quota alerts"), "bell");
        SettingsControls.Toggle(quota, "低额度提醒", "bell", settings.NotifyLowQuota, value => { settings.NotifyLowQuota = value; CommitSettings(); });
        SettingsControls.Input(quota, "低额度阈值 (%)", settings.LowQuotaThreshold.ToString(CultureInfo.InvariantCulture), value =>
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 1 || number > 50) return false;
            settings.LowQuotaThreshold = number; CommitSettings(); return true;
        }, L.T("可设置 1–50，达到阈值后提醒。"), L.F("请输入 1–50 之间的数字。", "Enter a number from 1 to 50."));
        SettingsControls.Toggle(quota, "额度用尽时提醒", "minus", settings.NotifyExhausted, value => { settings.NotifyExhausted = value; CommitSettings(); });
        SettingsControls.Toggle(quota, "额度重置时提醒", "rotate-cw", settings.NotifyReset, value => { settings.NotifyReset = value; CommitSettings(); });
        var cards = SettingsControls.Section(body, "账号重置卡", "refresh-cw", L.T("自动从账号读取可用重置卡和真实到期时间。"));
        SettingsControls.Toggle(cards, "重置卡到期提醒", "bell", settings.NotifyCardExpiry, value => { settings.NotifyCardExpiry = value; CommitSettings(); });
        SettingsControls.Input(cards, "提前提醒天数", settings.CardReminderDays, value =>
        {
            var days = value.Split(',');
            if (days.Any(day => !int.TryParse(day.Trim(), out var number) || number < 0 || number > 365)) return false;
            settings.CardReminderDays = value; CommitSettings(); return true;
        }, L.T("用英文逗号分隔，例如 3,1。到期当天也会提醒。"), L.F("请输入 0–365 的整天数，用英文逗号分隔。", "Enter whole days from 0 to 365, separated by commas."));
        cards.Children.Add(SettingsControls.Action("刷新重置卡", "refresh-cw", async () => { await RefreshAllAsync(); QueueSettingsRender(); }));
        foreach (var group in visibleAccounts.ToArray())
        {
            cards.Children.Add(DialogLayout.Text(group.Name, 12));
            cards.Children.Add(DialogLayout.Text(group.ResetCardSummary, 11, true));
        }
    }

    private void BuildGeneralSettings(Panel body)
    {
        SettingChoices(body, "语言", "languages", ["简体中文", "English"], ["zh-CN", "en"], ["languages", "languages"], settings.Language, value => SetGuideLanguage(value));
        SettingChoices(body, "自动刷新", "refresh-cw", ["1 分钟", "5 分钟", "15 分钟", "30 分钟"], ["1", "5", "15", "30"], ["refresh-cw", "refresh-cw", "refresh-cw", "refresh-cw"], settings.RefreshMinutes.ToString(), value =>
        {
            settings.RefreshMinutes = int.Parse(value, CultureInfo.InvariantCulture); nextRefresh = DateTimeOffset.UtcNow.AddMinutes(settings.RefreshMinutes);
        });
        var behavior = SettingsControls.Section(body, L.F("窗口行为", "Window behavior"), "layout-grid");
        SettingsControls.Toggle(behavior, "关闭窗口时收起到托盘", "minus", settings.CloseToTray, value => { settings.CloseToTray = value; CommitSettings(); });
        BuildStartupSetting(behavior);
        var cli = SettingsControls.Section(body, "Codex 程序", "folder-search");
        cli.Children.Add(DialogLayout.Text(Quota.Core.CliLocator.Find(settings.CodexPath) ?? L.T("未找到程序，请手动选择。"), 11, true));
        var actions = new WrapPanel();
        actions.Children.Add(SettingsControls.Action("选择程序", "folder-search", async () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Codex (codex.exe)|codex.exe", CheckFileExists = true };
            if (picker.ShowDialog(settingsWindow) != true) return;
            settings.CodexPath = picker.FileName; CommitSettings(); QueueSettingsRender(); await RefreshAllAsync();
        }));
        actions.Children.Add(SettingsControls.Action("自动查找", "refresh-cw", async () =>
        {
            await Task.Run(() => Quota.Core.CliLocator.Find()); settings.CodexPath = null; CommitSettings(); QueueSettingsRender(); await RefreshAllAsync();
        })); cli.Children.Add(actions);
        var help = SettingsControls.Section(body, L.F("帮助", "Help"), "info");
        help.Children.Add(SettingsControls.Action("使用导览与说明书", "info", () => ShowUserGuide()));
    }

    private void SettingsFrame(Window dialog, StackPanel content, UIElement navigation, bool narrow)
    {
        System.Windows.Shell.WindowChrome.SetWindowChrome(dialog, new() { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14), UseAeroCaptionButtons = false });
        var frame = new Grid(); frame.RowDefinitions.Add(new() { Height = GridLength.Auto }); frame.RowDefinitions.Add(new());
        var header = new Grid { Height = 54, Margin = new Thickness(20, 0, 12, 0), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = SettingsControls.Label("设置", "settings-2", 14); title.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(title);
        var close = new Button { Content = new AppIcon { Kind = "x" }, Style = (Style)FindResource("IconButton"), ToolTip = L.T("关闭"), Width = 30, Height = 30 };
        close.Click += (_, _) => dialog.Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is TextBlock || args.OriginalSource == header) dialog.DragMove(); };
        frame.Children.Add(header);
        var layout = new Grid(); Grid.SetRow(layout, 1); frame.Children.Add(layout);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = narrow ? new Thickness(16, 12, 12, 18) : new Thickness(28, 20, 20, 24) };
        if (narrow)
        {
            layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new());
            var nav = new Border { Child = navigation, Padding = new Thickness(12, 0, 12, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
            nav.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); layout.Children.Add(nav); Grid.SetRow(scroll, 1);
        }
        else
        {
            layout.ColumnDefinitions.Add(new() { Width = new GridLength(176) }); layout.ColumnDefinitions.Add(new());
            var sidebar = new DockPanel();
            var saved = SettingsControls.Label(L.F("自动保存", "Auto-save"), "check", 11); saved.Margin = new Thickness(10, 16, 0, 4);
            DockPanel.SetDock(saved, Dock.Bottom); sidebar.Children.Add(saved); sidebar.Children.Add(navigation);
            var nav = new Border { Child = sidebar, Padding = new Thickness(12, 14, 10, 16), BorderThickness = new Thickness(0, 0, 1, 0) };
            nav.SetResourceReference(Border.BackgroundProperty, "Surface"); nav.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); layout.Children.Add(nav); Grid.SetColumn(scroll, 1);
        }
        layout.Children.Add(scroll);
        var outer = new Border { Child = frame, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); dialog.Content = outer;
        dialog.PreviewKeyDown -= SettingsEscape; dialog.PreviewKeyDown += SettingsEscape;
    }

    private static void SettingsEscape(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == System.Windows.Input.Key.Escape && sender is Window window) { args.Handled = true; window.Close(); }
    }
}
