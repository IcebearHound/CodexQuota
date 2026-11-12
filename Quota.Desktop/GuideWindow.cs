using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Forms = System.Windows.Forms;

namespace Quota.Desktop;

public sealed class GuideWindow : Window
{
    private readonly MainWindow main;
    private readonly Grid frame = new();
    private readonly QuotaPreviewPanel quotaPreview;
    private readonly StatusPreviewPanel statusPreview = new();
    private readonly StackPanel body = new();
    private readonly ScrollViewer scroll;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string[] chapterIcons = ["info", "log-in", "sun", "monitor"];
    private int page;
    private bool busy, closed;
    private AccountConnectionResult? connectionResult;
    internal bool WasDismissed { get; private set; }
    private bool Narrow => (IsLoaded ? ActualWidth : Width) < 700;
    private bool Short => (IsLoaded ? ActualHeight : Height) < 480;

    public GuideWindow(MainWindow main)
    {
        this.main = main;
        quotaPreview = new(main.GuideSettings.ShowUsed);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(NativeWindowMessage);
        Width = Math.Min(820, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 32);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = main.Topmost;
        if (main.IsVisible && main.ShowInTaskbar) Owner = main;
        FontFamily = main.FontFamily;
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Text");
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new()
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(WindowSurface.CornerRadius), UseAeroCaptionButtons = false
        });
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new());
        frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
        scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(28, 0, 28, 0) };
        Grid.SetRow(scroll, 2);
        var outer = Card(frame, 0);
        outer.CornerRadius = new CornerRadius(WindowSurface.CornerRadius);
        outer.SetResourceReference(Border.BackgroundProperty, "Background");
        Content = outer;
        Loaded += (_, _) => WindowPlacementGuard.EnsureVisible(this);
        SizeChanged += (_, _) => { if (IsLoaded) Render(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        L.LanguageChanged += LanguageChanged;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Dismiss(); }
        };
        Closed += (_, _) =>
        {
            closed = true; lifetime.Cancel(); lifetime.Dispose();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            L.LanguageChanged -= LanguageChanged;
        };
        ShowPage(0);
    }
    // Owner destruction can raise Closing, so explicit UI dismissal completes onboarding.
    public void Dismiss() { WasDismissed = true; Close(); }
    private nint NativeWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0112 && ((long)wParam & 0xFFF0) == 0xF060) WasDismissed = true;
        return 0;
    }

    public void ShowPage(int index)
    {
        if (busy || closed) return;
        page = Math.Clamp(index, 0, 3);
        Render();
        scroll.ScrollToTop();
        if (SystemParameters.ClientAreaAnimation)
        {
            body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            var slide = new TranslateTransform(); body.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (page == 3)
        {
            main.BeginGuidePlacement();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (closed || page != 3) return;

                Render();
            });
        }
    }

    private string[] Chapters => [L.F("额度监测", "Quota monitoring"), L.F("连接账号", "Connect accounts"), L.F("状态灯", "Status lights"), L.F("放置小组件", "Widget placement")];

    private void Render()
    {
        Title = L.F("Codex Quota · 使用导览", "Codex Quota · Guide");
        frame.Children.Clear(); body.Children.Clear();
        var header = new Grid { Margin = Short ? new Thickness(28, 12, 20, 10) : new Thickness(28, 18, 20, 16), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new BrandIcon { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(Text("Codex Quota", 15, false, 0)); header.Children.Add(brand);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        var language = Button(L.IsEnglish ? "中文" : "English", () => main.SetGuideLanguage(L.IsEnglish ? "zh-CN" : "en"), true);
        language.IsEnabled = !busy; tools.Children.Add(language);
        var close = Button("", Dismiss, true); close.Content = Glyph("x"); close.ToolTip = L.T("关闭");
        close.Style = (Style)FindResource("IconButton"); close.Padding = new Thickness(0); close.Width = 32; close.Height = 32; tools.Children.Add(close);
        Grid.SetColumn(tools, 1); header.Children.Add(tools);
        header.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is TextBlock || args.OriginalSource == header) DragMove(); };
        frame.Children.Add(header);
        var navigationColumns = Narrow && !(Short && (IsLoaded ? ActualWidth : Width) >= 540) ? 2 : 4;
        var nav = new UniformGrid { Columns = navigationColumns, Margin = new Thickness(28, 0, 21, Short ? 12 : 24) };
        for (var i = 0; i < Chapters.Length; i++)
        {
            var destination = i;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Glyph(chapterIcons[i]));
            content.Children.Add(new TextBlock
            {
                Text = Chapters[i], FontSize = 11, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Max(50, ((IsLoaded ? ActualWidth : Width) - 56) / nav.Columns - 60)
            });
            var choice = new RadioButton { Content = content, IsChecked = page == i, GroupName = "guide-chapters", Style = (Style)FindResource("IntervalChoice"), IsEnabled = !busy };
            if (navigationColumns == 2) choice.Margin = new Thickness(0, 0, 7, 6);
            AutomationProperties.SetName(choice, Chapters[i]);
            choice.Checked += (_, _) => ShowPage(destination); nav.Children.Add(choice);
        }
        Grid.SetRow(nav, 1); frame.Children.Add(nav); frame.Children.Add(scroll);
        switch (page)
        {
            case 0: QuotaPage(); break;
            case 1: ConnectionPage(); break;
            case 2: StatusPage(); break;
            case 3: PlacementPage(); break;
        }
        var footer = new Grid { Margin = Short ? new Thickness(28, 10, 28, 12) : new Thickness(28, 18, 28, 22) };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var skip = Button(L.F("稍后设置", "Set up later"), Dismiss, true); footer.Children.Add(skip); skip.HorizontalAlignment = HorizontalAlignment.Left;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (page > 0) { var previous = Button(L.F("上一步", "Back"), () => ShowPage(page - 1), true); previous.IsEnabled = !busy; actions.Children.Add(previous); }
        var next = Button(page == 3 ? L.F("完成", "Finish") : L.F("下一步", "Next"), () => { if (page == 3) Dismiss(); else ShowPage(page + 1); });
        next.SetResourceReference(Control.BackgroundProperty, "SelectedFill"); next.SetResourceReference(Control.BorderBrushProperty, "Accent"); next.MinWidth = 94; next.IsEnabled = !busy;
        actions.Children.Add(next); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); frame.Children.Add(footer);
        if ((IsLoaded ? ActualWidth : Width) < 320)
        {
            footer.RowDefinitions.Add(new() { Height = GridLength.Auto }); footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(actions, 1); Grid.SetColumn(actions, 0); Grid.SetColumnSpan(actions, 2);
            actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 6, 0, 0);
        }
    }

    private void QuotaPage()
    {
        Heading(L.F("每个账号的额度，一眼掌握", "Every account, at a glance"), L.F("同时查看多个账号的剩余额度与下次重置时间。", "Track quota and the next reset across your accounts."));
        quotaPreview.RefreshContent(); body.Children.Add(quotaPreview);
    }
    private void StatusPage()
    {
        Heading(L.F("用一盏灯，了解任务进展", "Follow your work with one light"), L.F("点击任意状态查看演示。任务栏小组件与主面板使用相同状态灯。", "Select a state to preview it. The taskbar widget and main panel share these lights."));
        statusPreview.RefreshContent(); body.Children.Add(statusPreview);
    }

    private void ConnectionPage()
    {
        Heading(L.F("把你的账号连接进来", "Connect your accounts"), L.F("已在使用 CC Switch？可以直接同步已有的 Codex 账号。也可以通过官方页面登录。", "Sync existing Codex accounts from CC Switch, or sign in through the official browser flow."));
        ConnectionCard("download", L.F("从 CC Switch 导入", "Import from CC Switch"), L.F("扫描本机 CC Switch，确认后同步认证信息与原账号名称。手动修改过的名称会保留。", "Scan local CC Switch accounts, then confirm to sync credentials and original names. Your custom names are preserved."), true);
        ConnectionCard("log-in", L.F("原生登录", "Sign in with Codex"), L.F("打开 Codex 官方登录页面，为每个账号保存独立认证。可重复添加多个账号。", "Open the official Codex sign-in page. Each account has its own saved credentials. Repeat to add more accounts."), false);
        body.Children.Add(Text(busy ? L.F("正在等待额度同步完成，可关闭导览后稍后连接。", "Waiting for quota sync. You can close the guide and connect later.") : connectionResult?.Description ?? L.F("可以现在连接账号，也可以稍后在设置中连接。", "Connect now, or add accounts later in Settings."), 12, true));
        body.Children.Add(Text(L.F("当前有 {0} 个账号可查看额度。", "Quota is available for {0} accounts.", main.GuideAccountCount), 11, true));
        body.Children.Add(Text(L.F("CC Switch 同步、重新登录与删除同步账号都可在设置的账号页管理。", "Manage CC Switch sync, sign-in and imported-account removal in Settings → Accounts."), 11, true));
    }

    private void ConnectionCard(string icon, string title, string description, bool import)
    {
        body.Children.Add(SettingsControls.ConnectionCard(icon, title, description, async () => await ConnectAsync(import), !busy));
    }

    private async Task ConnectAsync(bool import)
    {
        if (busy) return;
        if (main.GuideDemo) { connectionResult = new(AccountConnectionOutcome.Demo); Render(); return; }
        busy = true; Render();
        try
        {
            await main.PrepareGuideConnectionAsync(lifetime.Token);
            if (closed) return;
            Hide();
            connectionResult = await main.ConnectFromGuideAsync(import);
        }
        catch (Exception error) when (error is CodexException or System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            connectionResult = error is OperationCanceledException ? new(AccountConnectionOutcome.Canceled) : new(AccountConnectionOutcome.Failed, error.Message);
        }
        finally { busy = false; if (!closed) { Show(); Render(); Activate(); } }
    }

    private void DisplaysChanged(object? sender, EventArgs args)
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            WindowPlacementGuard.EnsureVisible(this);
            Render();
        });
    }
    private void LanguageChanged(object? sender, EventArgs args) { if (!closed) Render(); }

    private void PlacementPage()
    {
        Heading(L.F("让信息出现在顺手的位置", "Keep your widgets within reach"), L.F("主面板支持精简模式，双击可返回完整界面。主面板和任务栏小组件可分别选择圆环或状态条。", "Use compact mode in the main window and double-click to restore it. Choose ring or status-bar layouts independently for the main window and taskbar widget."));
        body.Children.Add(new WidgetPlacementPanel(main));
        body.Children.Add(Text(L.F("任务栏小组件自动避开天气、应用按钮与系统托盘。", "The taskbar widget avoids weather, app buttons and the system tray."), 11, true));
    }

    private void Heading(string title, string description)
    {
        var heading = Text(title, Short ? 21 : Narrow ? 23 : 27); heading.FontWeight = FontWeights.SemiBold; body.Children.Add(heading);
        var detail = Text(description, 13, true, Short ? 14 : 22); detail.LineHeight = Short ? 18 : 21; body.Children.Add(detail);
    }
    private static AppIcon Glyph(string kind) => new() { Kind = kind, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Text(string text, int size = 13, bool muted = false, double bottom = 10)
    {
        var block = DialogLayout.Text(text, size, muted); block.Margin = new Thickness(0, 0, 0, bottom); return block;
    }
    private Border Card(UIElement content, double padding)
    {
        var card = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(WindowSurface.CornerRadius), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return card;
    }
    private Button Button(string label, Action action, bool quiet = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(14, 9, 14, 9) };
        if (quiet) button.Style = (Style)FindResource("QuietButton");
        button.Click += (_, _) => action(); return button;
    }
}
