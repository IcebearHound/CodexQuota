using System.Reflection;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Drawing = System.Drawing;
using Quota.Core;
using Quota.Desktop;

internal static class AppearanceSmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var store = (AccountStore)Field("store").GetValue(main)!;
        var original = (settings.Theme, settings.Palette, settings.Material);
        var guide = Application.Current.Windows.OfType<GuideWindow>().Single();
        Method("SettingsClick").Invoke(main, [main, new RoutedEventArgs()]);
        var dialog = (Window)Field("settingsWindow").GetValue(main)!;
        var accents = new HashSet<string>();
        var outputIndex = Array.IndexOf(args, "--preview-dir");
        var output = outputIndex >= 0 ? args[outputIndex + 1] : null;
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            await Settle();
            Check(Descendants<RadioButton>(dialog).Count(button => button.GroupName == "palette-presets") == 5, "appearance settings exposes five palette swatches");
            foreach (var theme in new[] { "dark", "light" })
                foreach (var palette in new[] { "mint", "ocean", "violet", "amber", "monochrome" })
                {
                    settings.Theme = theme; settings.Palette = palette; settings.Material = "solid";
                    Method("ApplySettings").Invoke(main, null); await Settle();
                    var surface = ((SolidColorBrush)Application.Current.FindResource("Surface")).Color;
                    var foreground = ((SolidColorBrush)Application.Current.FindResource("Text")).Color;
                    var muted = ((SolidColorBrush)Application.Current.FindResource("Muted")).Color;
                    Check(ContrastColors.Contrast(Rgb(surface), Rgb(foreground)) >= 4.5 && ContrastColors.Contrast(Rgb(surface), Rgb(muted)) >= 4.5,
                        "palette body and secondary text keep accessible contrast in light and dark modes");
                    if (theme == "dark") accents.Add(Application.Current.FindResource("Accent").ToString()!);
                    var paletteGroups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
                    Check(paletteGroups.SelectMany(group => group.Buckets).SelectMany(bucket => bucket.Windows).Where(window => window.Remaining > 20).All(window => window.Color == Application.Current.FindResource("Accent").ToString()!.Replace("#FF", "#")), "healthy quota rings follow the active palette");
                    if (output is not null) ProductScreenshotSmoke.Capture(main, output, "theme-" + palette + "-" + theme + ".png");
                }
            Check(accents.Count == 5, "each preset supplies a distinct accent");
            var mono = Descendants<RadioButton>(dialog).Single(button => Equals(button.Tag, "monochrome"));
            mono.IsChecked = true; await Settle();
            Check(settings.Palette == "monochrome" && settings.Theme == "system", "selecting Mono enables automatic system light and dark switching");
            var savedMono = store.LoadSettings();
            Check(savedMono.Palette == "monochrome" && savedMono.Theme == "system", "the system-following Mono choice persists");
            settings.Theme = "dark"; settings.Palette = "ocean";
            foreach (var (theme, material) in new[] { ("dark", "glass"), ("dark", "solid"), ("dark", "glass"), ("light", "glass"), ("light", "solid"), ("light", "glass") })
            {
                settings.Theme = theme; settings.Material = material; Method("ApplySettings").Invoke(main, null); await Settle();
                foreach (var window in new Window[] { main, dialog, guide })
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    Check(DwmGetWindowAttribute(handle, 38, out var backdrop, sizeof(int)) >= 0 && (material == "glass" ? GlassEnabled(window) && backdrop is 1 or 3 : !GlassEnabled(window) && backdrop == 1),
                        "open main settings and guide windows switch native backdrop live");
                    Check(DwmGetWindowAttribute(handle, 20, out var nativeDark, sizeof(int)) >= 0 && nativeDark == (theme == "dark" ? 1 : 0),
                        "native backdrop luminance follows the selected light or dark theme");
                    Check(WindowChrome.GetWindowChrome(window).GlassFrameThickness == new Thickness(-1), "material switches retain a stable transparent native frame");
                    var source = HwndSource.FromHwnd(handle)!;
                    Check(source.CompositionTarget.BackgroundColor.A == 0, "the composition canvas stays transparent while the root controls material opacity");
                }
                var menu = (ContextMenu)Method("CreateWidgetMenu").Invoke(main, null)!;
                menu.PlacementTarget = main; menu.IsOpen = true; await Settle();
                Check(menu.Background is SolidColorBrush { Color.A: 255 }, "popup menus retain a readable opaque surface over glass");
                menu.IsOpen = false;
            }
            settings.Material = "solid"; Method("ApplySettings").Invoke(main, null);
            var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
            var strip = new TaskbarWidget(main, groups, null);
            var flyout = new TaskbarFlyout(strip, main, groups) { ShowActivated = false };
            try
            {
                flyout.Show(); await Settle();
                typeof(TaskbarFlyout).GetMethod("HideImmediately", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flyout, null);
                settings.Material = "glass"; Method("ApplySettings").Invoke(main, null);
                flyout.Show(); await Settle();
                Check(GlassEnabled(flyout),
                    "hidden prewarmed flyouts receive the new native glass backdrop before reopening");
                typeof(TaskbarFlyout).GetMethod("HideAnimated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flyout, null);
                await Task.Delay(220); Check(!flyout.IsVisible, "glass flyout closes through its animated entry point");
            }
            finally { flyout.Close(); strip.Close(); }
            settings.DesktopOnly = true; Method("ApplySettings").Invoke(main, null); await Settle();
            Check(HwndSource.FromHwnd(new WindowInteropHelper(main).Handle)!.CompositionTarget.BackgroundColor.A == 255,
                "desktop-embedded windows use an opaque fallback for unsupported system backdrops");
            settings.DesktopOnly = false; Method("ApplySettings").Invoke(main, null); await Settle();
            Check(GlassEnabled(main),
                "returning to a top-level window restores glass");
            Method("SaveSettings").Invoke(main, null);
            var saved = store.LoadSettings(); Check(saved.Palette == "ocean" && saved.Material == "glass", "palette and glass mode survive settings reload");
            dialog.Hide(); guide.Hide();
            await CheckVisibleGlass(main, settings, output);
            Console.WriteLine("PASS five palette contrasts, system-following Mono, settings swatches, live native glass and persistence");
        }
        finally
        {
            settings.DesktopOnly = false;
            (settings.Theme, settings.Palette, settings.Material) = original;
            Method("ApplySettings").Invoke(main, null); Method("SaveSettings").Invoke(main, null); dialog.Close();
        }
    }
    private static bool GlassEnabled(Window window) => (bool)typeof(MainWindow).Assembly.GetType("Quota.Desktop.WindowSurface", true)!.GetMethod("IsGlassEnabled", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window])!;
    private static async Task CheckVisibleGlass(MainWindow main, WidgetSettings settings, string? output)
    {
        var scene = new Canvas();
        var background = new Window
        {
            Width = Math.Min(900, SystemParameters.WorkArea.Width - 40), Height = Math.Min(620, SystemParameters.WorkArea.Height - 40),
            Left = SystemParameters.WorkArea.Left + 20, Top = SystemParameters.WorkArea.Top + 20,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Content = scene
        };
        var original = (main.Left, main.Top, main.Topmost);
        var originalTopmostSetting = settings.Topmost;
        TaskbarWidget? strip = null; TaskbarFlyout? flyout = null;
        try
        {
            background.Show(); main.Left = background.Left + 50; main.Top = background.Top + 70; main.Topmost = true; main.Activate();
            settings.Theme = "light"; settings.Palette = "ocean"; settings.Topmost = true;
            var distance = 0;
            foreach (var material in new[] { "solid", "glass", "solid", "glass", "solid", "glass" })
            {
                settings.Material = material; Method("ApplySettings").Invoke(main, null);
                scene.Background = new SolidColorBrush(Color.FromRgb(238, 64, 128)); await Task.Delay(240);
                var first = Sample(main, new Point(6, main.ActualHeight / 2));
                scene.Background = new SolidColorBrush(Color.FromRgb(34, 140, 248)); await Task.Delay(240);
                var second = Sample(main, new Point(6, main.ActualHeight / 2));
                distance = Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
                if (output is not null && material == "glass" && distance <= 90) CaptureNative(background, Path.Combine(output, "failed-glass.png"));
                Check(material == "glass" ? distance > 90 : distance == 0, $"{material} mode keeps its intended background opacity after live switches, difference {distance}");
            }
            var width = main.Width;
            main.Width = width + 1; await Task.Delay(120);
            main.Width = width; await Task.Delay(120);
            Check(main.ActualWidth == width, "square quota cards settle after a one-pixel resize without alternating scrollbar layouts");

            scene.Background = new LinearGradientBrush(Color.FromRgb(73, 164, 255), Color.FromRgb(250, 98, 160), 35);
            AddOrb(scene, "#F34A77", 320, 40, 210); AddOrb(scene, "#8C3CFF", 440, 300, -60);
            AddOrb(scene, "#16E5D1", 320, -90, -80); AddOrb(scene, "#FFC342", 340, 590, 340);
            await Task.Delay(160);
            var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
            strip = new TaskbarWidget(main, groups, null);
            flyout = new TaskbarFlyout(strip, main, groups) { Left = background.Left + 500, Top = background.Top + 150, Topmost = true };
            flyout.Show(); await Task.Delay(200);
            if (output is not null) CaptureNative(background, Path.Combine(output, "glass-light.png"));
            settings.Theme = "dark"; settings.Palette = "violet"; Method("ApplySettings").Invoke(main, null); await Task.Delay(200);
            if (output is not null) CaptureNative(background, Path.Combine(output, "glass-dark.png"));
            Console.WriteLine($"PASS compositor glass transmits background colors ({distance}), solid blocks them, full-window light and dark renders");
        }
        finally { flyout?.Close(); strip?.Close(); background.Close(); settings.Topmost = originalTopmostSetting; (main.Left, main.Top, main.Topmost) = original; }
    }
    private static void AddOrb(Canvas scene, string color, double size, double left, double top)
    {
        var orb = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) };
        Canvas.SetLeft(orb, left); Canvas.SetTop(orb, top); scene.Children.Add(orb);
    }
    private static Drawing.Color Sample(Window window, Point point)
    {
        var location = window.PointToScreen(point);
        using var bitmap = new Drawing.Bitmap(1, 1);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)location.X, (int)location.Y, 0, 0, new Drawing.Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }
    private static void CaptureNative(Window window, string path)
    {
        var location = window.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        using var bitmap = new Drawing.Bitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY));
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)location.X, (int)location.Y, 0, 0, bitmap.Size);
        bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
    }
    private static RgbColor Rgb(Color color) => new(color.R, color.G, color.B);
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(60); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
