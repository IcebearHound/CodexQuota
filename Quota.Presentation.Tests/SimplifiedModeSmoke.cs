using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class SimplifiedModeSmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var store = (AccountStore)Field("store").GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var sources = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var mode = Method("SetSimplifiedMode"); var layout = Method("SetSimplifiedLayout");
        var originalMode = settings.SimplifiedMode; var originalLayout = settings.SimplifiedLayout;
        var originalTaskbar = settings.DetailedTaskbar;
        mode.Invoke(main, [false]);
        await Settle(main);
        var width = main.ActualWidth; var height = main.ActualHeight;
        var added = new List<AccountViewModel>();
        var widget = new TaskbarWidget(main, groups, null);
        try
        {
            ((Button)main.FindName("SimplifiedModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(main);
            Check(settings.SimplifiedMode && main.ActualWidth == 252 && main.ActualHeight < height, "main action enters a smaller compact window");
            var full = (Grid)main.FindName("FullLayout");
            var compact = (ScrollViewer)main.FindName("SimplifiedPanel");
            var items = (ItemsControl)main.FindName("SimplifiedAccounts");
            Check(!full.IsVisible && compact.IsVisible && !Descendants<Button>(compact).Any(), "compact surface hides every operation button");
            Check(items.Items.Count == groups.Count, "compact surface shows every account group");
            settings.DetailedTaskbar = false;
            foreach (var style in new[] { "ring", "bar" })
            {
                layout.Invoke(main, [style]); await Settle(main);
                Check(settings.SimplifiedLayout == style && !settings.DetailedTaskbar, "main layout keeps taskbar choice independent");
                Check(style == "ring" ? Descendants<QuotaRing>(compact).Count() == groups.Count : Descendants<ProgressBar>(compact).Count() == groups.Count,
                    "each compact account uses the chosen renderer");
                Check(!Descendants<Button>(compact).Any() && main.ActualWidth == 252, "both layouts remain button-free and narrow");
                Method("SavePlacement").Invoke(main, null);
                var saved = store.LoadSettings();
                Check(saved.SimplifiedMode && saved.SimplifiedLayout == style && saved.Width == width && saved.Height == height,
                    "saving compact placement preserves full dimensions and startup mode");
                if (style == "bar")
                {
                    var reset = Descendants<TextBlock>(compact).First(text => text.Name == "InlineReset");
                    var percent = Descendants<TextBlock>(compact).First(text => text.Name == "InlinePercent");
                    Check(Math.Abs(reset.TranslatePoint(new Point(), compact).Y - percent.TranslatePoint(new Point(), compact).Y) < 4 && reset.TranslatePoint(new Point(reset.ActualWidth, 0), compact).X < percent.TranslatePoint(new Point(), compact).X,
                        "reset time shares the percentage line to its left");
                    Check(Descendants<AppIcon>(compact).Count(icon => icon.Kind == "rotate-cw") == groups.Count, "every inline reset time has its icon");
                }
                Capture(main, args, "main-compact-" + style + ".png");
            }
            layout.Invoke(main, ["ring"]); settings.DetailedTaskbar = true;
            Method("ApplySettings").Invoke(main, null); await Settle(main);
            Check(settings.SimplifiedLayout == "ring" && settings.DetailedTaskbar, "taskbar status-bar choice leaves main ring selected");
            var update = typeof(TaskbarWidget).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            update.Invoke(widget, null);
            var body = (Border)widget.Content;
            body.Measure(new Size(180, 48)); body.Arrange(new Rect(0, 0, 180, 48)); body.UpdateLayout();
            Check(((Grid)widget.FindName("FullPanel")).Visibility == Visibility.Visible && ((Grid)widget.FindName("CompactPanel")).Visibility == Visibility.Collapsed,
                "taskbar status-bar mode uses its own compact renderer");
            Check(Descendants<ProgressBar>((Grid)widget.FindName("FullPanel")).Count() == 1 && !Descendants<QuotaRing>((Grid)widget.FindName("FullPanel")).Any(),
                "taskbar status-bar mode renders a bar with essential text");
            Capture(body, args, "taskbar-status-bar.png");
            for (var index = 0; index < 12; index++)
            {
                var source = new AccountViewModel(new() { Id = Guid.NewGuid().ToString("N"), Name = "Compact fixture " + index });
                added.Add(source); sources.Add(source);
            }
            await Settle(main);
            Check(main.ActualHeight <= 450 && compact.ExtentHeight > compact.ViewportHeight && items.Items.Count == groups.Count,
                "many accounts stay accessible within a capped scrolling panel");
            compact.ScrollToEnd(); await Settle(main); Check(compact.VerticalOffset > 0, "compact account list scrolls to its last entry");
            foreach (var source in added) sources.Remove(source);
            added.Clear(); await Settle(main);
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(click, 2);
            compact.RaiseEvent(click); await Settle(main);
            Check(!settings.SimplifiedMode && click.Handled && main.ActualWidth == width && main.ActualHeight == height, "compact preview input restores full mode on double-click");
            mode.Invoke(main, [true]); await Settle(main);
            Method("SimplifiedMenuOpening").Invoke(main, [compact, null]);
            var restore = compact.ContextMenu.Items.OfType<MenuItem>().First();
            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Settle(main);
            Check(!settings.SimplifiedMode && full.IsVisible && !compact.IsVisible && main.ActualWidth == width && main.ActualHeight == height,
                "compact context menu restores full layout and original dimensions");
            Check(store.LoadSettings().SimplifiedLayout == "ring" && store.LoadSettings().DetailedTaskbar,
                "independent layout choices persist together");
            Console.WriteLine("PASS main compact ring and status-bar layouts, separate taskbar layout, hidden actions, persisted full size and scrolling");
        }
        finally
        {
            foreach (var source in added) sources.Remove(source);
            settings.DetailedTaskbar = originalTaskbar;
            layout.Invoke(main, [originalLayout]); mode.Invoke(main, [originalMode]);
            Method("ApplySettings").Invoke(main, null); Method("SaveSettings").Invoke(main, null);
            widget.Close();
        }
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Window main) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(FrameworkElement surface, string[] args, string filename)
    {
        var output = Array.IndexOf(args, "--preview-dir");
        if (output < 0 || args.Length <= output + 1) return;
        var image = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 2), (int)Math.Ceiling(surface.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(args[output + 1]);
        using var stream = File.Create(Path.Combine(args[output + 1], filename)); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
