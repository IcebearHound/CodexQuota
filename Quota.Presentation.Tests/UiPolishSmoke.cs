using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class UiPolishSmoke
{
    public static async Task Run(MainWindow main)
    {
        await CheckSettings(main);
        await CheckAccountLayouts(main);
    }

    private static async Task CheckSettings(MainWindow main)
    {
        Method("SettingsClick").Invoke(main, [main, new RoutedEventArgs()]);
        var dialog = (Window)Field("settingsWindow").GetValue(main)!;
        try
        {
            await Settle(dialog);
            var scroll = Descendants<ScrollViewer>(dialog).Single(v => v.Content is StackPanel);
            var chrome = WindowChrome.GetWindowChrome(dialog);
            var root = dialog.Content;
            if (SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines > 0)
            {
                var step = 20d * SystemParameters.WheelScrollLines;
                var offsets = new List<double>();
                scroll.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) offsets.Add(e.VerticalOffset); };
                var body = (StackPanel)scroll.Content;
                Wheel(body, -120); Wheel(body, -120); Wheel(body, -120);
                await Task.Delay(360);
                var expected = Math.Min(step * 3, scroll.ScrollableHeight);
                Check(Math.Abs(scroll.VerticalOffset - expected) < 1 && offsets.Count > 2 &&
                    offsets.Zip(offsets.Skip(1)).All(pair => pair.First <= pair.Second),
                    $"wheel bursts are coalesced and stepped through rendered offsets (actual {scroll.VerticalOffset:0.0}, expected {expected:0.0}, frames {offsets.Count})");
                Wheel(body, 120); await Task.Delay(360);
                Check(Math.Abs(scroll.VerticalOffset - Math.Max(0, expected - step)) < 1, "opposite wheel input reverses the scroll direction");
                var start = scroll.VerticalOffset;
                Wheel(body, -30); Wheel(body, -30);
                await Settle(dialog);
                Check(Math.Abs(scroll.VerticalOffset - Math.Min(start + step / 2, scroll.ScrollableHeight)) < 1, "high-resolution wheel deltas add without an extra animation");
                Wheel(body, -120);
                scroll.ScrollToTop(); await Task.Delay(220);
                Check(scroll.VerticalOffset == 0, "direct scroll requests can take over from wheel motion");
            }

            scroll.ScrollToEnd(); await Settle(dialog);
            var before = scroll.VerticalOffset;
            Method("RenderSettings").Invoke(main, null); await Settle(dialog);
            Check(ReferenceEquals(chrome, WindowChrome.GetWindowChrome(dialog)) && ReferenceEquals(root, dialog.Content) &&
                ReferenceEquals(scroll, Descendants<ScrollViewer>(dialog).Single(v => v.Content is StackPanel)) &&
                Math.Abs(scroll.VerticalOffset - before) < 1, "rebuilding a page keeps its offset, HWND chrome and outer surface");
            dialog.Width = 320; dialog.Height = 540; await Settle(dialog);
            var narrow = Descendants<ScrollViewer>(dialog).Single(v => v.Content is StackPanel);
            var pages = Descendants<RadioButton>(dialog).Where(button => button.GroupName == "settings-pages").ToArray();
            Check(pages.Length == 7 && pages.All(page => Bounds(page, dialog).Right <= dialog.ActualWidth + 1) &&
                narrow.ViewportHeight > 220 && narrow.ExtentWidth <= narrow.ViewportWidth + 1, "narrow settings navigation and content stay inside the viewport");
            pages.Single(page => AutomationProperties.GetName(page) == L.T("通知")).IsChecked = true;
            await Settle(dialog);
            Check(narrow.VerticalOffset == 0 && ReferenceEquals(root, dialog.Content), "new settings page starts at the top without replacing the window surface");
            Console.WriteLine("PASS frame-paced wheel bursts, precision deltas, narrow settings and stable navigation");
        }
        finally { dialog.Close(); }
    }

    private static async Task CheckAccountLayouts(MainWindow main)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var sources = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var original = (settings.Compact, settings.DetailedTaskbar, main.Width);
        var account = sources.First();
        var originalName = account.Name;
        account.Rename("very-long-account-name-for-testing-ellipsis@example.test");
        settings.Compact = true;
        settings.DetailedTaskbar = false;
        main.Width = main.MinWidth;
        Method("ApplySettings").Invoke(main, null);
        var widget = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(widget, main, groups)
        {
            Left = SystemParameters.WorkArea.Left + 30,
            Top = SystemParameters.WorkArea.Top + 30
        };
        try
        {
            await Settle(main);
            var compact = (ItemsControl)main.FindName("AccountList");
            foreach (var card in Cards(compact))
            {
                AssertBefore(card, "CompactMetric", "CompactReset");
                AssertBefore(card, "CompactPlan", "CompactStatus", horizontal: true);
            }

            flyout.Show(); await Settle(flyout);
            var cards = (ItemsControl)flyout.FindName("AccountCards");
            foreach (var card in Cards(cards))
            {
                AssertBefore(card, "RingAccountName", "RingSwitch", horizontal: true);
                AssertBefore(card, "RingSwitch", "RingMetric");
                AssertBefore(card, "RingMetric", "RingReset");
                AssertBefore(card, "RingReset", "RingStatus");
            }
            Check(Descendants<QuotaRing>(cards).Count() == groups.Count, "ring mode has one ring per card");
            Check(Bounds(Cards(cards)[0], cards).Top == Bounds(Cards(cards)[1], cards).Top, "ring cards form two columns");
            Method("SetTaskbarLayout").Invoke(main, [true]);
            typeof(TaskbarFlyout).GetMethod("SetAccountLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flyout, [true]);
            await Settle(flyout);
            Check(Descendants<ProgressBar>(cards).Count() == groups.Count && !Descendants<QuotaRing>(cards).Any(), "bar mode has one bar per account and no rings");
            var rows = Cards(cards);
            Check(Bounds(rows[0], cards).Bottom <= Bounds(rows[1], cards).Top, "bar accounts form a single column");
            var select = Descendants<Button>(rows[1]).Single(button => button.Name == "BarAccountName");
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ReferenceEquals(widget.DataContext, select.Tag), "bar mode keeps the account selection action");
            Console.WriteLine("PASS non-overlapping ring cards, long-name layout and single-column taskbar bars with account selection");
        }
        finally
        {
            flyout.Close(); widget.Close(); account.Rename(originalName);
            (settings.Compact, settings.DetailedTaskbar, main.Width) = original;
            Method("ApplySettings").Invoke(main, null);
        }
    }

    private static void Wheel(UIElement target, int delta) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
    private static FrameworkElement[] Cards(ItemsControl control) => Enumerable.Range(0, control.Items.Count)
        .Select(index => (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(index)).ToArray();
    private static void AssertBefore(FrameworkElement card, string first, string second, bool horizontal = false)
    {
        var a = Bounds(Descendants<FrameworkElement>(card).Single(element => element.Name == first), card);
        var b = Bounds(Descendants<FrameworkElement>(card).Single(element => element.Name == second), card);
        Check(horizontal ? a.Right <= b.Left + 0.1 : a.Bottom <= b.Top + 0.1, $"{first} and {second} occupy separate slots");
    }
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
