using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Quota.Desktop;

internal static class FlyoutMotionSmoke
{
    public static async Task Run(MainWindow main, string[] arguments)
    {
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var widget = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(widget, main, groups);
        var show = typeof(TaskbarFlyout).GetMethod("ShowAnimated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hide = typeof(TaskbarFlyout).GetMethod("HideAnimated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hideNow = typeof(TaskbarFlyout).GetMethod("HideImmediately", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Show(double offset = 8) => show.Invoke(flyout, [(Action)(() => { flyout.Left = SystemParameters.WorkArea.Left + 30; flyout.Top = SystemParameters.WorkArea.Top + 60; }), offset]);
        bool Transitioning() => (bool)typeof(TaskbarFlyout).GetProperty("IsTransitioning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(flyout)!;
        try
        {
            Show();
            Check(flyout.IsVisible && (SystemParameters.ClientAreaAnimation ? Transitioning() : flyout.Opacity == 1), "entrance follows the current animation preference");
            await Task.Delay(240);
            Check(flyout.IsVisible && flyout.Opacity == 1 && !Transitioning(), $"entrance settles at full opacity with no animation clock (visible {flyout.IsVisible}, opacity {flyout.Opacity}, transitioning {Transitioning()})");
            var top = flyout.Top;
            var visibleOpacityResets = 0; var lastOpacity = flyout.Opacity;
            var descriptor = DependencyPropertyDescriptor.FromProperty(Window.OpacityProperty, typeof(TaskbarFlyout));
            EventHandler observeOpacity = (_, _) =>
            {
                var opacity = flyout.Opacity;
                if (opacity > lastOpacity + 0.05 && IsWindowVisible(new WindowInteropHelper(flyout).Handle)) visibleOpacityResets++;
                lastOpacity = opacity;
            };
            descriptor.AddValueChanged(flyout, observeOpacity);
            try { hide.Invoke(flyout, null); await Task.Delay(240); }
            finally { descriptor.RemoveValueChanged(flyout, observeOpacity); }
            Check(visibleOpacityResets == 0, "exit never resets opacity while the native window is still visible");
            Check(!flyout.IsVisible && flyout.Opacity == 1 && flyout.Top == top && !Transitioning(), "exit hides the native window and restores reusable properties");
            Show(-8); await Task.Delay(20); hide.Invoke(flyout, null); await Task.Delay(20); Show(-8);
            await Task.Delay(260);
            Check(flyout.IsVisible && flyout.Opacity == 1 && !Transitioning(), "rapid reopen reverses dismissal without a stale hide callback");
            hide.Invoke(flyout, null); hideNow.Invoke(flyout, null); Show(); await Task.Delay(260);
            Check(flyout.IsVisible && flyout.Opacity == 1, "native suppression cancels an old exit before a new entrance");
            hide.Invoke(flyout, null); flyout.Close(); await Task.Delay(200);
            Check(!Transitioning() && !flyout.HasAnimatedProperties, "closing releases all transition clocks");
            Console.WriteLine("PASS flyout entrance exit rapid reversal suppression and closed-window animation cleanup");

            var body = (Border)widget.Content;
            var time = (TextBlock)widget.FindName("ResetText"); var percent = (TextBlock)widget.FindName("PercentText");
            time.Text = "6d20h"; percent.Text = "100%";
            foreach (var name in new[] { "PercentText", "ResetText", "FullPercentText", "FullNameText", "FullResetText" })
            {
                var text = (TextBlock)widget.FindName(name);
                Check(text.FontFamily.Source == "Segoe UI Variable Text, Segoe UI" && text.FontWeight == FontWeights.Normal,
                    "every taskbar label uses the Segoe UI taskbar family and regular weight");
            }
            foreach (var height in new[] { 40d, 48d })
            {
                body.Measure(new Size(44, height)); body.Arrange(new Rect(0, 0, 44, height)); body.UpdateLayout();
                var ring = (QuotaRing)widget.FindName("ProgressRing");
                Check(time.FontSize == 12 && time.FontFamily.Source == ((FontFamily)widget.FindResource("TaskbarFont")).Source && time.FontWeight == FontWeights.Normal && percent.FontWeight == FontWeights.Normal && time.TranslatePoint(new Point(), body).Y >= ring.TranslatePoint(new Point(0, ring.ActualHeight), body).Y - 0.5,
                    "compact countdown uses readable type outside the ring at both taskbar heights");
                Check(time.ActualWidth >= time.DesiredSize.Width && time.TranslatePoint(new Point(0, time.ActualHeight), body).Y <= height - 3,
                    "countdown fits without clipping the sampling gutter");
            }
            var output = Array.IndexOf(arguments, "--preview-dir");
            if (output >= 0 && arguments.Length > output + 1)
            {
                var bitmap = new RenderTargetBitmap(88, 96, 192, 192, PixelFormats.Pbgra32); bitmap.Render(body);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(arguments[output + 1], "compact-readable.png")); encoder.Save(stream);
            }
            Console.WriteLine("PASS compact ring separates percentage and a larger countdown across taskbar heights");
        }
        finally { if (flyout.IsLoaded) flyout.Close(); widget.Close(); }
    }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
