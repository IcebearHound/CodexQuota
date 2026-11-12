using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class TaskbarLifecycleSmoke
{
    public static int Run(string[] args)
    {
        var stateIndex = Array.IndexOf(args, "--taskbar-state");
        if (stateIndex < 0 || args.Length <= stateIndex + 1 || !File.Exists(args[stateIndex + 1]))
            throw new ArgumentException("Choose the state file of a running taskbar widget.");
        var statePath = Path.GetFullPath(args[stateIndex + 1]);
        var result = 1;
        var thread = new Thread(() =>
        {
            var app = new Application();
            var window = new Window
            {
                Title = "Taskbar lifecycle fixture", Width = 480, Height = 320, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(24, 31, 37)),
                Content = new TextBlock { Text = "Checking taskbar fullscreen recovery", Foreground = Brushes.White,
                    FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
            timeout.Tick += (_, _) => { Console.Error.WriteLine("FAIL taskbar lifecycle timed out"); app.Shutdown(1); };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var handle = new WindowInteropHelper(window).Handle;
                    var screen = Forms.Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary display available.");
                    var ordinary = screen.WorkingArea;
                    Place(handle, ordinary.Left + 40, ordinary.Top + 40, 480, 320);
                    window.Activate();
                    await WaitFor(() => GetForegroundWindow() == handle, "fixture becomes foreground");
                    await WaitFor(() => Matches(statePath, visible: true, suppressed: false), "ordinary foreground keeps the taskbar widget visible");
                    Console.WriteLine("PASS real taskbar widget stays visible with an ordinary foreground window");
                    var full = screen.Bounds;
                    Place(handle, full.Left, full.Top, full.Width, full.Height);
                    await WaitFor(() => Matches(statePath, visible: false, suppressed: true), "fullscreen foreground suppresses the taskbar widget");
                    Console.WriteLine("PASS real taskbar widget hides when foreground covers its display");
                    Place(handle, ordinary.Left + 40, ordinary.Top + 40, 480, 320);
                    await WaitFor(() => Matches(statePath, visible: true, suppressed: false), "leaving fullscreen restores the taskbar widget");
                    Console.WriteLine("PASS real taskbar widget recovers after fullscreen exits without a foreground change");
                    result = 0;
                }
                catch (Exception error) { Console.Error.WriteLine("FAIL taskbar lifecycle " + error); }
                finally { timeout.Stop(); app.Shutdown(result); }
            };
            timeout.Start(); app.Run(window);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) { Console.Error.WriteLine("FAIL taskbar lifecycle thread did not finish"); return 1; }
        return result;
    }

    private static void Place(nint handle, int left, int top, int width, int height)
    {
        if (!SetWindowPos(handle, -1, left, top, width, height, 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static bool Matches(string statePath, bool visible, bool suppressed)
    {
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            return state.RootElement.GetProperty("Visible").GetBoolean() == visible
                && state.RootElement.GetProperty("Suppressed").GetBoolean() == suppressed;
        }
        catch (Exception error) when (error is IOException or JsonException) { return false; }
    }

    private static async Task WaitFor(Func<bool> ready, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
        if (!ready()) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int left, int top, int width, int height, uint flags);
}
