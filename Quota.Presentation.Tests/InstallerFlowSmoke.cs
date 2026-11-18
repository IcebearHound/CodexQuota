using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class InstallerFlowSmoke
{
    public static int Run()
    {
        var fixture = Path.GetFullPath(Environment.GetEnvironmentVariable("QUOTA_SETUP_TEST_ROOT") ?? throw new ArgumentException("Set an isolated setup fixture path."));
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("QUOTA_SETUP_TEST_PREVIEW") ?? throw new ArgumentException("Set a screenshot output folder."));
        if (Directory.Exists(fixture)) throw new IOException("Choose a fresh installer fixture directory.");
        Directory.CreateDirectory(fixture);
        var result = 1;
        var thread = new Thread(() =>
        {
            var app = new App(); app.InitializeComponent();
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
            {
                try
                {
                    var window = app.MainWindow;
                    Check(window.GetType().Name == "InstallerWindow" && app.Windows.Count == 1, "installer starts without initializing the normal app or profile");
                    T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var theme = typeof(App).Assembly.GetType("Quota.Desktop.ThemeService", true)!.GetMethod("ApplyAppearance")!;
                    if (L.IsEnglish) Click("LanguageButton");
                    theme.Invoke(null, ["dark", "monochrome", "solid"]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var parent = Find<TextBox>("ParentBox"); var path = Find<TextBlock>("DestinationText");
                    Check(parent.Text == InstallLocation.DefaultParent && path.Text == @"D:\Program Files\CodexQuota", "setup displays the requested default and final path");
                    ProductScreenshotSmoke.Capture(window, output, "setup-destination-dark.png");
                    theme.Invoke(null, ["light", "monochrome", "solid"]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ProductScreenshotSmoke.Capture(window, output, "setup-destination-light.png");
                    parent.Text = "relative";
                    Check(!Find<Button>("ContinueButton").IsEnabled, "relative locations cannot be installed");

                    var occupied = Path.Combine(fixture, "非空 目录");
                    var owned = Path.Combine(occupied, InstallLocation.FolderName);
                    Directory.CreateDirectory(owned);
                    var untouched = Path.Combine(owned, "user-file.txt"); File.WriteAllText(untouched, "keep");
                    parent.Text = occupied;
                    Check(path.Text == owned, "the preview appends one CodexQuota folder");
                    Click("ContinueButton");
                    await WaitFor(() => Find<TextBlock>("Feedback").IsVisible && Find<TextBlock>("Feedback").Text.Contains("非空"));
                    Check(File.ReadAllText(untouched) == "keep", "blocked install preserves existing files");

                    var chosen = Path.Combine(fixture, "自定义 安装路径", InstallLocation.FolderName);
                    parent.Text = chosen;
                    Check(path.Text == chosen, "an existing CodexQuota suffix is not duplicated");
                    Click("ContinueButton");
                    await WaitFor(() => Find<Border>("ReadyCard").IsVisible, () => Find<TextBlock>("Feedback").Text);
                    Check(File.Exists(Path.Combine(chosen, "install-files.json")) && File.Exists(Path.Combine(chosen, "CodexQuota.exe")), "custom UI installed the selected path via the silent backend");
                    Check(Find<CheckBox>("OpenGuide").IsChecked == true, "finish defaults to opening the guide");
                    ProductScreenshotSmoke.Capture(window, output, "setup-finish-light.png");
                    Find<CheckBox>("OpenGuide").IsChecked = false;
                    Click("ContinueButton");
                    Check(!window.IsVisible, "finishing without the guide closes the installer");
                    result = 0;
                    Console.WriteLine("PASS branded custom path preview, safe retry, real backend install, and finish defaults");
                }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally { if (!app.Dispatcher.HasShutdownStarted) app.Shutdown(result); }
            });
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return thread.Join(TimeSpan.FromSeconds(65)) ? result : 1;
    }

    private static async Task WaitFor(Func<bool> predicate, Func<string>? details = null)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Installer did not reach the expected state. " + details?.Invoke());
    }

    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }

}
