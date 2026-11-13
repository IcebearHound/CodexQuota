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
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        settings.Theme = "light"; settings.Material = "solid";
        Method("ApplySettings").Invoke(main, null);
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
        var languages = args.Contains("--bilingual") ? new[] { "zh-CN", "en" } : new[] { L.Language };
        foreach (var language in languages)
        {
            Method("SetGuideLanguage").Invoke(main, [language]);
            var accounts = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
            var names = L.IsEnglish ? new[] { "Daily work", "Personal project", "Backup account", "Research" } : new[] { "日常工作", "个人项目", "备用账号", "研究任务" };
            for (var index = 0; index < accounts.Count; index++) accounts[index].Rename(names[index]);
            await CaptureProducts(main, output, language == "en" ? "-en" : "");
        }
        Console.WriteLine("PASS light product screenshots use isolated bilingual demo accounts and live issue-list controls");
    }

    private static async Task CaptureProducts(MainWindow main, string output, string suffix)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        settings.Compact = true; settings.Width = 400; settings.Height = 464;
        Method("SetSimplifiedMode").Invoke(main, [false]);
        main.Width = 400; main.Height = 464;
        Method("ApplySettings").Invoke(main, null);
        await Settle(main); Capture(main, output, "main" + suffix + ".png");
        Method("SetSimplifiedLayout").Invoke(main, ["ring"]);
        ((Button)main.FindName("SimplifiedModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(main); Capture(main, output, "compact-ring" + suffix + ".png");
        Method("SetSimplifiedLayout").Invoke(main, ["bar"]);
        await Settle(main); Capture(main, output, "compact-bar" + suffix + ".png");
        Method("SetSimplifiedMode").Invoke(main, [false]);
        await CaptureIssues(main, output, suffix);
        Field("settingsPage").SetValue(main, "外观");
        Method("SettingsClick").Invoke(main, [main, new RoutedEventArgs()]);
        var dialog = (Window)Field("settingsWindow").GetValue(main)!;
        await Settle(dialog); Capture(dialog, output, "settings" + suffix + ".png");
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
        Capture((FrameworkElement)section, output, "startup" + suffix + ".png", dialog);
        startup.IsChecked = true;
        await Settle(dialog);
        Check(startup.IsChecked == true && Descendants<TextBlock>(dialog).Any(text => text.Text == L.F("演示模式，开机启动设置仅供预览。", "Demo mode. Startup changes are previewed here.")), "demo startup toggle previews state through its normal UI handler");
        startup.IsChecked = false;
        dialog.Close();
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var widget = new TaskbarWidget(main, groups, null);
        try
        {
            settings.DetailedTaskbar = true;
            var host = typeof(TaskbarWidget).GetField("host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;
            host.GetType().GetField("<BackgroundColor>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, new RgbColor(239, 243, 247));
            typeof(TaskbarWidget).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(widget, null);
            var body = (Border)widget.Content;
            body.Background = new SolidColorBrush(Color.FromRgb(239, 243, 247));
            body.Measure(new Size(200, 48)); body.Arrange(new Rect(0, 0, 200, 48)); body.UpdateLayout();
            Capture(body, output, "taskbar-bar" + suffix + ".png");
            var flyout = new TaskbarFlyout(widget, main, groups) { Topmost = false };
            try
            {
                flyout.Show();
                await Settle(flyout);
                CaptureTaskbarPreview((Border)flyout.FindName("FlyoutBody"), body, output, suffix);
            }
            finally { flyout.Close(); }
            var menu = (ContextMenu)Method("CreateWidgetMenu").Invoke(main, null)!;
            menu.PlacementTarget = main; menu.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); menu.UpdateLayout();
            try { Capture(menu, output, "taskbar-menu" + suffix + ".png"); }
            finally { menu.IsOpen = false; }
        }
        finally { widget.Close(); }
    }

    private static void CaptureTaskbarPreview(Border flyout, Border widget, string output, string suffix)
    {
        const double width = 380;
        var taskbarTop = 24 + flyout.ActualHeight + 20;
        var height = taskbarTop + 48;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 249, 251)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(239, 243, 247)), null, new Rect(0, taskbarTop, width, 48));
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(220, 228, 233)), 1), new Point(0, taskbarTop), new Point(width, taskbarTop));
            drawing.DrawRectangle(new VisualBrush(flyout), null, new Rect(24, 24, flyout.ActualWidth, flyout.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(widget), null, new Rect((width - widget.ActualWidth) / 2, taskbarTop + 4, widget.ActualWidth, widget.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var name = "taskbar-preview" + suffix + ".png";
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
        Console.WriteLine($"SCREENSHOT {name} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
    }

    private static async Task CaptureIssues(MainWindow main, string output, string suffix)
    {
        var store = (AccountStore)Field("store").GetValue(main)!;
        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = Path.Combine(store.Root, "screenshot-codex-home"); Directory.CreateDirectory(home);
        var originalActivity = Field("activity").GetValue(main);
        SessionIssuesWindow? dialog = null;
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", home);
            var time = new DateTimeOffset(2026, 11, 13, 10, 24, 0, TimeSpan.FromHours(8));
            var titles = L.IsEnglish ? new[] { "Fix API integration", "Verify test results", "Prepare release notes" } : new[] { "修复接口集成问题", "核对测试结果", "整理发布说明" };
            var issues = titles.Select((title, index) =>
            {
                var id = Guid.NewGuid().ToString("D");
                return new CodexSessionIssue(id, id, index == 0 ? CodexActivityKind.Error : CodexActivityKind.Paused, time.AddMinutes(-index * 12), "demo", Title: title);
            }).ToArray();
            Field("activity").SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Error, SessionIssues: issues));
            Method("UpdateCodexStatus").Invoke(main, null);
            ((Button)main.FindName("SessionIssuesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = Application.Current.Windows.OfType<SessionIssuesWindow>().Single();
            await Settle(dialog);
            Check(Descendants<TextBlock>(dialog).Any(text => text.Text == titles[0]), "issue screenshot renders its synthetic session titles");
            Capture(dialog, output, "session-issues" + suffix + ".png");
        }
        finally
        {
            dialog?.Close();
            Field("activity").SetValue(main, originalActivity);
            Method("UpdateCodexStatus").Invoke(main, null);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalHome);
        }
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
