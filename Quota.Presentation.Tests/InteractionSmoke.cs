using System.Reflection;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class InteractionSmoke
{
    public static async Task Run(MainWindow main)
    {
        var originalLanguage = L.Language;
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var applyTheme = typeof(MainWindow).Assembly.GetType("Quota.Desktop.ThemeService", true)!.GetMethod("Apply")!;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var icon = new Button { Content = new AppIcon { Kind = "settings-2" }, Style = (Style)Application.Current.FindResource("IconButton") };
        icon.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.设置");
        var radio = new RadioButton { Content = "Option", Style = (Style)Application.Current.FindResource("IntervalChoice") };
        var toggle = new CheckBox { Content = "Toggle" };
        var next = new Button { Content = "Next" };
        panel.Children.Add(icon); panel.Children.Add(radio); panel.Children.Add(toggle); panel.Children.Add(next);
        var window = new Window
        {
            Width = 360, Height = 300, Content = panel, Topmost = true, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.SetResourceReference(Window.BackgroundProperty, "Background");
        try
        {
            window.Show(); window.Activate(); await Settle(window);
            foreach (var theme in new[] { "light", "dark" })
            {
                applyTheme.Invoke(null, [theme]);
                icon.Focus(); await Settle(window);
                Check(icon.IsKeyboardFocused && icon.BorderThickness.Left >= 1, "icon keyboard focus has a visible outline");
                var border = (Border)icon.Template.FindName("bd", icon);
                var accent = ((SolidColorBrush)Application.Current.FindResource("Accent")).Color;
                var fill = ((SolidColorBrush)border.Background).Color;
                Check(((SolidColorBrush)border.BorderBrush).Color == accent && ContrastColors.Contrast(new(accent.R, accent.G, accent.B), new(fill.R, fill.G, fill.B)) >= 3, "icon outline keeps contrast in both themes");
                radio.Focus(); await Settle(window);
                var choice = (Border)radio.Template.FindName("choice", radio);
                Check(radio.IsKeyboardFocused && ((SolidColorBrush)choice.BorderBrush).Color == accent, "radio focus uses the theme accent");
                Check(icon.BorderThickness.Left == 0, "icon outline clears when keyboard focus leaves");
                toggle.Focus(); await Settle(window);
                var track = (Border)toggle.Template.FindName("toggle", toggle);
                Check(toggle.IsKeyboardFocused && ((SolidColorBrush)track.BorderBrush).Color == accent, "toggle focus uses the theme accent");
            }
            Console.WriteLine("PASS icon radio and toggle keyboard focus remains visible in light and dark themes");
            radio.IsEnabled = false; await Settle(window);
            Check(radio.Opacity <= 0.4, "disabled radio choices are visibly disabled");
            radio.IsEnabled = true;
            Console.WriteLine("PASS unavailable radio choices expose a disabled appearance");
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Apply(language); await Settle(window);
                var peer = UIElementAutomationPeer.CreatePeerForElement(icon)!;
                Check(AutomationProperties.GetName(icon) == L.T("设置") && peer.GetName() == L.T("设置"), "icon automation names follow translated tooltips");
            }
            Console.WriteLine("PASS icon automation names update with the interface language");
            await CheckAccountEditing(panel, next, window);
            window.Hide();
            await CheckNormalLayout(main);
            await CheckRefreshEditing(main);
            await CheckMergedSourceErrors(main);
            await CheckAccountIconLabels(main);
        }
        finally { window.Close(); applyTheme.Invoke(null, [settings.Theme]); L.Apply(originalLanguage); }
    }

    private static async Task CheckAccountIconLabels(MainWindow main)
    {
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var apply = typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var compact = settings.Compact;
        var language = L.Language;
        try
        {
            foreach (var layout in new[] { true, false })
            {
                settings.Compact = layout; apply.Invoke(main, null);
                foreach (var currentLanguage in new[] { "zh-CN", "en" })
                {
                    L.Apply(currentLanguage); await Settle(main);
                    var buttons = Descendants<Button>(main).Where(button => button.IsVisible && button.Tag is AccountGroupViewModel && button.Content is AppIcon).ToArray();
                    Check(buttons.Length >= 2, "account template renders its icon actions");
                    foreach (var button in buttons)
                    {
                        var expected = L.T(((AppIcon)button.Content).Kind == "arrow-right-left" ? "切换账号" : "账号操作");
                        Check(button.ToolTip is string tooltip && tooltip == expected, "account template resolves localized tooltip resources");
                        var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
                        Check(peer.GetName() == expected && AutomationProperties.GetName(button) == expected, "account icon accessible name follows its resolved tooltip");
                    }
                }
            }
            Console.WriteLine("PASS account template icon tooltips and accessible names update in both languages and layouts");
        }
        finally { settings.Compact = compact; L.Apply(language); apply.Invoke(main, null); await Settle(main); }
    }

    private static async Task CheckMergedSourceErrors(MainWindow main)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var current = accounts.Single(account => account.Record.IsCurrent);
        var original = current.Snapshot!;
        var healthy = new AccountViewModel(new() { Name = "Healthy alternate", Email = original.Email });
        healthy.SetSnapshot(JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(original))!);
        accounts.Add(healthy);
        try
        {
            current.SetError("Codex 连接中断，请重试。");
            typeof(MainWindow).GetMethod("UpdateSummary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
            await Settle(main);
            var group = groups.Single(group => group.Sources.Contains(current));
            Check(!group.HasError && group.HasMetric, "healthy alternate sources retain usable quota");
            var summary = (TextBlock)main.FindName("Summary");
            Check(summary.Text.Contains("sources need attention") && summary.ToolTip?.ToString()?.Contains(L.T("检查网络后刷新")) == true, "summary includes failed sources behind healthy quota");
            Exception? failure = null;
            _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.IsVisible && window.Title == L.T("需要处理的账号"));
                try
                {
                    Check(Descendants<TextBlock>(dialog).Any(text => text.Text == AccountGroupViewModel.SourceName(current)), "issue details identify the failing local source");
                    Check(Descendants<TextBlock>(dialog).Any(text => text.Text.StartsWith(L.T("当前登录未验证，以下为上次读取账号的额度。 ")) && text.Text.Contains("network")), "issue details retain the underlying connection error");
                }
                catch (Exception error) { failure = error; }
                finally { dialog.Close(); }
            });
            typeof(MainWindow).GetMethod("SummaryIssuesClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [main, new RoutedEventArgs()]);
            if (failure is not null) throw failure;
            Console.WriteLine("PASS merged healthy quota retains actionable details for a failed credential source");
        }
        finally
        {
            current.SetSnapshot(original); accounts.Remove(healthy);
            typeof(MainWindow).GetMethod("UpdateSummary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
            await Settle(main);
        }
    }

    private static async Task CheckRefreshEditing(MainWindow main)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var source = accounts.Single(account => account.Record.IsCurrent);
        var original = source.Snapshot!; var originalName = source.Record.Name; var originalCustom = source.Record.CustomName;
        var currentName = settings.CurrentAccountName;
        var group = groups.Single(group => group.Sources.Contains(source));
        var control = Descendants<AccountName>(main).Single(name => name.IsVisible && ReferenceEquals(name.DataContext, group));
        var editor = (TextBox)typeof(AccountName).GetField("editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
        var edit = (Button)typeof(AccountName).GetField("edit", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(control)!;
        try
        {
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(main);
            editor.Text = "Uncommitted refresh draft";
            var changed = JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(original))!;
            changed.FetchedAt = DateTimeOffset.UtcNow;
            foreach (var window in changed.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = Math.Min(100, window.UsedPercent + 1);
            source.SetSnapshot(changed); await Settle(main);
            Check(ReferenceEquals(groups.Single(group => group.Sources.Contains(source)), group), "same-identity refresh retains the existing account group");
            Check(control.IsLoaded && editor.IsVisible && editor.Text == "Uncommitted refresh draft", "quota refresh preserves an unfinished name edit");
            Check(group.FocusWindow?.Remaining == changed.Buckets.SelectMany(bucket => bucket.Windows).Min(window => window.Remaining), "retained groups still update their quota bindings");
            Console.WriteLine("PASS same-identity quota refresh preserves inline editing and updates usage");
            changed.Email = "changed-identity@example.com";
            source.SetSnapshot(changed); await Settle(main);
            Check(!ReferenceEquals(groups.Single(group => group.Sources.Contains(source)), group), "changed identity rebuilds its account group");
            typeof(MainWindow).GetMethod("RenameAccountGroup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [group, "Obsolete rename request"]);
            Check(source.Record.Name == originalName && settings.CurrentAccountName == currentName, "identity replacement cannot commit the previous editing draft");
            Console.WriteLine("PASS identity replacement discards the previous draft without renaming the new account");
        }
        finally
        {
            source.Record.Name = originalName; source.Record.CustomName = originalCustom; settings.CurrentAccountName = currentName;
            source.SetSnapshot(original); await Settle(main);
        }
    }

    private static async Task CheckNormalLayout(MainWindow main)
    {
        var preferences = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var apply = typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalAccounts = accounts.ToArray();
        var originalCompact = preferences.Compact; var originalWidth = main.Width; var originalHeight = main.Height;
        var notice = (Border)main.FindName("NoticeBorder"); var originalNotice = notice.Visibility;
        try
        {
            preferences.Compact = false; main.Width = 560; main.Height = 350; notice.Visibility = Visibility.Collapsed;
            apply.Invoke(main, null);
            foreach (var total in new[] { 1, 4, 12 })
            {
                accounts.Clear(); foreach (var account in originalAccounts.Take(total)) accounts.Add(account);
                for (var index = accounts.Count; index < total; index++)
                    accounts.Add(new(new() { Name = "Additional account " + index }));
                await Settle(main);
                var focus = (ContentControl)main.FindName("FocusAccount");
                var others = (ItemsControl)main.FindName("OtherAccountList");
                var scroll = (ScrollViewer)main.FindName("OtherAccountsScroll");
                Check(focus.ActualHeight <= Math.Max(focus.MinHeight, scroll.ViewportHeight) + 1, "current account height stays within the normal viewport");
                var ring = Descendants<QuotaRing>(focus).Single();
                var ringBounds = ring.TransformToAncestor(main).TransformBounds(new Rect(0, 0, ring.ActualWidth, ring.ActualHeight));
                var panel = (Grid)main.FindName("NormalPanel");
                var panelBounds = panel.TransformToAncestor(main).TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                Check(ringBounds.Top >= panelBounds.Top && ringBounds.Bottom <= panelBounds.Bottom + 1, "current ring remains fully visible");
                if (total == 4) Check(Math.Abs(focus.ActualHeight - others.ActualHeight) <= 1, "current panel matches the height of the three account rows");
                if (total == 12)
                {
                    Check(scroll.ScrollableHeight > 0, "additional accounts remain scrollable");
                    scroll.ScrollToEnd(); await Settle(main);
                    var after = ring.TransformToAncestor(main).TransformBounds(new Rect(0, 0, ring.ActualWidth, ring.ActualHeight));
                    Check(after == ringBounds && scroll.VerticalOffset > 0, "scrolling other accounts keeps the current ring fixed");
                }
                Console.WriteLine("PASS normal layout keeps the current ring visible with " + total + " accounts");
            }
        }
        finally
        {
            accounts.Clear(); foreach (var account in originalAccounts) accounts.Add(account);
            preferences.Compact = originalCompact; main.Width = originalWidth; main.Height = originalHeight; notice.Visibility = originalNotice;
            apply.Invoke(main, null); await Settle(main);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task CheckAccountEditing(Panel panel, Button next, Window window)
    {
        var firstSource = new AccountViewModel(new() { Name = "First account" });
        var secondSource = new AccountViewModel(new() { Name = "Second account" });
        using var first = new AccountGroupViewModel("first", [firstSource]);
        using var second = new AccountGroupViewModel("second", [secondSource]);
        var name = new AccountName { DataContext = first };
        panel.Children.Add(name); await Settle(window);
        var editor = (TextBox)typeof(AccountName).GetField("editor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(name)!;
        var edit = (Button)typeof(AccountName).GetField("edit", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(name)!;
        var label = (TextBlock)typeof(AccountName).GetField("label", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(name)!;
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(editor.IsVisible && editor.Text == first.EditableName, "inline editing starts with the selected account");
        editor.Text = "Uncommitted first-account draft"; name.DataContext = second; await Settle(window);
        Check(editor.Visibility == Visibility.Collapsed && label.Text == second.Name, "context replacement cancels the old editing draft");
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        Check(editor.Text == second.EditableName, "editing the replacement account starts with its own name");
        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
        await Settle(window);
        Check(editor.Visibility == Visibility.Collapsed && firstSource.Record.Name == "First account" && secondSource.Record.Name == "Second account", "Escape discards the current draft");
        Console.WriteLine("PASS inline names cancel obsolete drafts when an account context changes");
        edit.Focus(); await Settle(window);
        Check(edit.IsKeyboardFocused && edit.Opacity == 1, "keyboard focus reveals the inline edit icon");
        next.Focus(); await Settle(window);
        Check(edit.Opacity == (name.IsMouseOver || name.IsKeyboardFocusWithin ? 1 : 0), "edit icon follows the current hover and focus state");
        foreach (var language in new[] { "zh-CN", "en" })
        {
            L.Apply(language); await Settle(window);
            Check(Equals(edit.ToolTip, L.T("编辑名称")) && AutomationProperties.GetName(edit) == L.T("编辑名称"), "edit icon name follows the interface language");
            Check(AutomationProperties.GetName(editor) == L.T("账号名称"), "inline input has a localized accessible name");
        }
        Console.WriteLine("PASS inline edit icons track focus and expose localized accessible labels");
        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(window);
        panel.Children.Remove(name); await Settle(window);
        panel.Children.Add(name); await Settle(window);
        Check(editor.Visibility == Visibility.Collapsed && label.Text == second.Name, "unloaded name editors discard unfinished drafts");
        Console.WriteLine("PASS unloaded inline editors return with a clean display state");
    }
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
