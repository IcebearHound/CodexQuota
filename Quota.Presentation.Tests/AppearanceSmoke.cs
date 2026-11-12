using System.Reflection;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
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
        try
        {
            await Settle();
            Check(Descendants<RadioButton>(dialog).Count(button => button.GroupName == "palette-presets") == 4, "appearance settings exposes four palette swatches");
            foreach (var theme in new[] { "dark", "light" })
                foreach (var palette in new[] { "mint", "ocean", "violet", "amber" })
                {
                    settings.Theme = theme; settings.Palette = palette; settings.Material = "solid";
                    Method("ApplySettings").Invoke(main, null); await Settle();
                    var surface = ((SolidColorBrush)Application.Current.FindResource("Surface")).Color;
                    var foreground = ((SolidColorBrush)Application.Current.FindResource("Text")).Color;
                    var muted = ((SolidColorBrush)Application.Current.FindResource("Muted")).Color;
                    Check(ContrastColors.Contrast(Rgb(surface), Rgb(foreground)) >= 4.5 && ContrastColors.Contrast(Rgb(surface), Rgb(muted)) >= 4.5,
                        "palette body and secondary text keep accessible contrast in light and dark modes");
                    if (theme == "dark") accents.Add(Application.Current.FindResource("Accent").ToString()!);
                }
            Check(accents.Count == 4, "each preset supplies a distinct accent");
            settings.Theme = "dark"; settings.Palette = "ocean";
            foreach (var (theme, material) in new[] { ("dark", "glass"), ("dark", "solid"), ("dark", "glass"), ("light", "glass"), ("light", "solid"), ("light", "glass") })
            {
                settings.Theme = theme; settings.Material = material; Method("ApplySettings").Invoke(main, null); await Settle();
                foreach (var window in new Window[] { main, dialog, guide })
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    Check(DwmGetWindowAttribute(handle, 38, out var backdrop, sizeof(int)) >= 0 && backdrop == (material == "glass" ? 3 : 1),
                        "open main settings and guide windows switch native backdrop live");
                    Check(DwmGetWindowAttribute(handle, 20, out var nativeDark, sizeof(int)) >= 0 && nativeDark == (theme == "dark" ? 1 : 0),
                        "native backdrop luminance follows the selected light or dark theme");
                    Check(WindowChrome.GetWindowChrome(window).GlassFrameThickness == new Thickness(material == "glass" ? -1 : 0), "glass frame follows material selection");
                    var source = HwndSource.FromHwnd(handle)!;
                    Check(source.CompositionTarget.BackgroundColor.A == (material == "glass" ? 0 : 255), "native composition uses a transparent glass canvas and opaque solid canvas");
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
                Check(DwmGetWindowAttribute(new WindowInteropHelper(flyout).Handle, 38, out var flyoutBackdrop, sizeof(int)) >= 0 && flyoutBackdrop == 3,
                    "hidden prewarmed flyouts receive the new native glass backdrop before reopening");
                typeof(TaskbarFlyout).GetMethod("HideAnimated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(flyout, null);
                await Task.Delay(220); Check(!flyout.IsVisible, "glass flyout closes through its animated entry point");
            }
            finally { flyout.Close(); strip.Close(); }
            settings.DesktopOnly = true; Method("ApplySettings").Invoke(main, null); await Settle();
            Check(HwndSource.FromHwnd(new WindowInteropHelper(main).Handle)!.CompositionTarget.BackgroundColor.A == 255,
                "desktop-embedded windows use an opaque fallback for unsupported system backdrops");
            settings.DesktopOnly = false; Method("ApplySettings").Invoke(main, null); await Settle();
            Check(DwmGetWindowAttribute(new WindowInteropHelper(main).Handle, 38, out var restoredBackdrop, sizeof(int)) >= 0 && restoredBackdrop == 3,
                "returning to a top-level window restores glass");
            Method("SaveSettings").Invoke(main, null);
            var saved = store.LoadSettings(); Check(saved.Palette == "ocean" && saved.Material == "glass", "palette and glass mode survive settings reload");
            settings.Material = "solid"; Method("ApplySettings").Invoke(main, null);
            settings.Palette = "violet"; Method("ApplySettings").Invoke(main, null); await Settle();
            var output = Array.IndexOf(args, "--preview-dir");
            if (output >= 0 && output + 1 < args.Length) ProductScreenshotSmoke.Capture(main, args[output + 1], "palette-violet.png");
            Console.WriteLine("PASS four palette contrasts, settings swatches, live native glass in main settings and guide, reversible material and persistence");
        }
        finally
        {
            settings.DesktopOnly = false;
            (settings.Theme, settings.Palette, settings.Material) = original;
            Method("ApplySettings").Invoke(main, null); Method("SaveSettings").Invoke(main, null); dialog.Close();
        }
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
