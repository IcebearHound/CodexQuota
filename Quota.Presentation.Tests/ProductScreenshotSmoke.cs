using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class ProductScreenshotSmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--preview-dir");
        if (outputIndex < 0 || outputIndex + 1 >= args.Length) throw new ArgumentException("Product screenshots require --preview-dir.");
        var output = Path.GetFullPath(args[outputIndex + 1]); Directory.CreateDirectory(output);
        var guide = Application.Current.Windows.OfType<GuideWindow>().Single();
        if (args.Contains("--guide-only"))
        {
            foreach (var language in new[] { "zh-CN", "en" })
            {
                Method("SetGuideLanguage").Invoke(main, [language]);
                foreach (var (page, name) in new[] { (0, "quota"), (1, "accounts"), (2, "status") })
                {
                    guide.ShowPage(page); await Settle(guide);
                    Capture(guide, output, "guide-" + name + (language == "en" ? "-en" : "") + ".png");
                }
            }
            Console.WriteLine("PASS bilingual guide screenshots use live quota connection and status chapters");
            return;
        }
        guide.Dismiss();
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        settings.Compact = true; settings.Width = 400; settings.Height = 464;
        Method("SetSimplifiedMode").Invoke(main, [false]);
        main.Width = 400; main.Height = 464;
        Method("ApplySettings").Invoke(main, null);
        await Settle(main); Capture(main, output, "main.png");
        Method("SetSimplifiedLayout").Invoke(main, ["ring"]);
        ((Button)main.FindName("SimplifiedModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(main); Capture(main, output, "compact-ring.png");
        Method("SetSimplifiedLayout").Invoke(main, ["bar"]);
        await Settle(main); Capture(main, output, "compact-bar.png");
        Method("SetSimplifiedMode").Invoke(main, [false]);
        Field("settingsPage").SetValue(main, "外观");
        Method("SettingsClick").Invoke(main, [main, new RoutedEventArgs()]);
        var dialog = (Window)Field("settingsWindow").GetValue(main)!;
        await Settle(dialog); Capture(dialog, output, "settings.png");
        Field("settingsPage").SetValue(main, "通用");
        Method("RenderSettings").Invoke(main, null);
        await Settle(dialog);
        var startup = Descendants<CheckBox>(dialog).Single(box => AutomationProperties.GetName(box) == L.T("开机启动"));
        startup.BringIntoView();
        await Settle(dialog);
        var section = VisualTreeHelper.GetParent(startup);
        while (section is not Border) section = VisualTreeHelper.GetParent(section);
        ((FrameworkElement)section).BringIntoView();
        await Settle(dialog);
        Capture((FrameworkElement)section, output, "startup.png", dialog);
        startup.IsChecked = true;
        await Settle(dialog);
        Check(startup.IsChecked == true && Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("仅供预览")), "demo startup toggle previews state through its normal UI handler");
        startup.IsChecked = false;
        dialog.Close();
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var widget = new TaskbarWidget(main, groups, null);
        try
        {
            settings.DetailedTaskbar = true;
            typeof(TaskbarWidget).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, null);
            var body = (Border)widget.Content;
            body.Background = new SolidColorBrush(Color.FromRgb(32, 35, 42));
            body.Measure(new Size(200, 48)); body.Arrange(new Rect(0, 0, 200, 48)); body.UpdateLayout();
            Capture(body, output, "taskbar-bar.png");
            var menu = (ContextMenu)Method("CreateWidgetMenu").Invoke(main, null)!;
            menu.PlacementTarget = main; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); menu.UpdateLayout();
            try { Capture(menu, output, "taskbar-menu.png"); }
            finally { menu.IsOpen = false; }
        }
        finally { widget.Close(); }
        Console.WriteLine("PASS product screenshots use isolated demo accounts, live layouts, startup preview and themed taskbar controls");
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Task.Delay(250); }
    internal static void Capture(FrameworkElement surface, string output, string name, FrameworkElement? root = null)
    {
        var width = (int)Math.Ceiling(surface.ActualWidth * 2); var height = (int)Math.Ceiling(surface.ActualHeight * 2);
        Check(width > 0 && height > 0, "screenshot surface has a rendered size");
        root ??= surface;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var origin = ReferenceEquals(root, surface) ? new Point() : surface.TranslatePoint(new Point(), root);
            var brush = new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(origin.X, origin.Y, surface.ActualWidth, surface.ActualHeight) };
            drawing.DrawRectangle(brush, null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(width / 2, height / 2, 1, 1), pixel, 4, 0);
        Check(pixel[3] > 0, "screenshot contains its rendered opaque surface");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
        Console.WriteLine($"SCREENSHOT {name} {width}x{height}");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
