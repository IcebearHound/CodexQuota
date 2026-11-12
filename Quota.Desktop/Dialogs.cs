using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Quota.Desktop;
public static class DialogLayout
{
    public static Window Create(Window? owner, string title, double width = 390)
    {
        var window = new Window(); Configure(window, owner, title, width);
        return window;
    }
    internal static void Configure(Window window, Window? owner, string title, double width)
    {
        window.Title = L.T(title);
        window.Width = Math.Min(width, Math.Max(1, SystemParameters.WorkArea.Width - 32));
        window.SizeToContent = SizeToContent.Height; window.ResizeMode = ResizeMode.NoResize;
        window.WindowStyle = WindowStyle.None; window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.FontFamily = owner?.FontFamily ?? new FontFamily("Segoe UI"); window.ShowInTaskbar = false; window.Topmost = true;
        if (owner?.IsVisible == true && owner.ShowInTaskbar) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        window.SetResourceReference(Window.BackgroundProperty, "Background");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        window.Loaded += (_, _) => WindowPlacementGuard.EnsureVisible(window);
    }
    public static void Frame(Window window, StackPanel content, UIElement? navigation = null)
    {
        window.WindowStyle = WindowStyle.None;
        window.MaxHeight = Math.Min(640, Math.Max(1, SystemParameters.WorkArea.Height - 32));
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, new() { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false });
        var outer = new DockPanel();
        var heading = new Grid { Height = 38, Background = Brushes.Transparent, Margin = new Thickness(14, 2, 8, 0) };
        var title = Text(window.Title, 11, true); title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(0); heading.Children.Add(title);
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Window.Title)) { Source = window });
        var close = new Button { Content = new AppIcon { Kind = "x", Width = 15, Height = 15 }, Width = 28, Height = 28, Padding = new Thickness(0), Style = (Style)window.FindResource("IconButton"), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = L.T("关闭") };
        close.Click += (_, _) => window.Close(); heading.Children.Add(close);
        heading.MouseLeftButtonDown += (_, args) => { if (args.OriginalSource is TextBlock || args.OriginalSource == heading) window.DragMove(); };
        DockPanel.SetDock(heading, Dock.Top); outer.Children.Add(heading);
        if (navigation is not null) { DockPanel.SetDock(navigation, Dock.Top); outer.Children.Add(navigation); }
        outer.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = outer };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        window.Content = border;
        window.PreviewKeyDown -= CloseOnEscape;
        window.PreviewKeyDown += CloseOnEscape;
    }
    private static void CloseOnEscape(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key != System.Windows.Input.Key.Escape || sender is not Window window) return;
        args.Handled = true; window.Close();
    }
    public static TextBlock Text(string text, int size = 13, bool muted = false)
    {
        var block = new TextBlock { Text = L.T(text), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Text");
        return block;
    }
    public static string? AskName(Window owner, string title, string value)
    {
        var dialog = Create(owner, title);
        var root = new StackPanel { Margin = new Thickness(24, 10, 24, 24) }; Frame(dialog, root);
        root.Children.Add(Text(title, 20));
        var input = new TextBox { Text = value, MaxLength = 40, Margin = new Thickness(0, 0, 0, 20) }; root.Children.Add(input);
        var ok = new Button { Content = L.T("保存"), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        root.Children.Add(ok); dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    public static bool Confirm(Window? owner, string title, string description, string accept = "删除", string cancel = "取消")
    {
        var dialog = Create(owner, title, 420);
        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) }; Frame(dialog, root);
        root.Children.Add(Text(title, 20)); root.Children.Add(Text(description, 12, true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var back = new Button { Content = L.T(cancel), Margin = new Thickness(0, 0, 8, 0), IsCancel = true }; back.Click += (_, _) => dialog.Close();
        var ok = new Button { Content = L.T(accept), IsDefault = true }; ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(back); buttons.Children.Add(ok); root.Children.Add(buttons);
        return dialog.ShowDialog() == true;
    }
    public static void Alert(Window? owner, string title, string description)
    {
        var dialog = Create(owner, title, 420);
        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) }; Frame(dialog, root);
        root.Children.Add(Text(description, 13));
        var close = new Button { Content = L.T("关闭"), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true }; close.Click += (_, _) => dialog.Close(); root.Children.Add(close);
        dialog.ShowDialog();
    }
}
public sealed class LoginDialog : Window
{
    private readonly TextBox input;
    private readonly TextBlock status;
    private readonly Button login;
    private readonly CancellationTokenSource cancel;
    private readonly Func<Action<string>, CancellationToken, Task<string?>> action;
    private bool completed, working, closingRequested;
    public string AccountName => input.Text.Trim();
    public string? Email { get; private set; }
    public LoginDialog(Window owner, string name, bool relogin, CancellationToken lifetime, Func<Action<string>, CancellationToken, Task<string?>> action)
    {
        this.action = action; cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        DialogLayout.Configure(this, owner, relogin ? L.T("重新登录") : L.T("添加账号"), 420);
        var root = new StackPanel { Margin = new Thickness(26, 10, 26, 26) }; DialogLayout.Frame(this, root);
        root.Children.Add(DialogLayout.Text(relogin ? L.T("重新连接账号") : L.T("连接一个新账号"), 23));
        root.Children.Add(DialogLayout.Text(L.T("通过 Codex 官方页面登录。每个账号独立保存，登录完成后即可同时查看额度。"), 12, true));
        root.Children.Add(DialogLayout.Text(L.T("账号名称"), 12));
        input = new TextBox { Text = name, MaxLength = 40, Margin = new Thickness(0, 0, 0, 18) }; root.Children.Add(input);
        status = DialogLayout.Text(L.T("浏览器中请选择你要添加的账号。"), 12, true); root.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var back = new Button { Content = L.T("取消"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true }; back.Click += (_, _) => Close();
        login = new Button { Content = L.T("在浏览器中登录"), IsDefault = true }; login.Click += LoginClick;
        buttons.Children.Add(back); buttons.Children.Add(login); root.Children.Add(buttons);
        Closing += (_, e) => { if (!completed) cancel.Cancel(); if (working) { e.Cancel = true; closingRequested = true; status.Text = L.T("正在取消登录…"); } };
        Closed += (_, _) => { cancel.Cancel(); cancel.Dispose(); };
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(input.Text)) { status.Text = L.T("请先输入账号名称。"); input.Focus(); return; }
        working = true; login.IsEnabled = false; input.IsEnabled = false; status.Text = L.T("正在连接 Codex…");
        try
        {
            Email = await action(message => Dispatcher.Invoke(() => status.Text = L.T(message)), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            working = false; completed = true; DialogResult = true;
        }
        catch (OperationCanceledException) { status.Text = L.T("登录已取消，可以重新尝试。"); login.Content = L.T("重新尝试"); }
        catch (Exception ex) when (ex is Quota.Core.CodexException or System.IO.IOException or UnauthorizedAccessException) { status.Text = ex is Quota.Core.CodexException ? L.Error(ex.Message) : L.T("无法保存登录，请检查本地文件权限。"); login.Content = L.T("重新尝试"); login.IsEnabled = true; input.IsEnabled = true; }
        finally
        {
            working = false;
            if (closingRequested) Close();
            else if (!completed && !cancel.IsCancellationRequested) { login.IsEnabled = true; input.IsEnabled = true; }
        }
    }
}
