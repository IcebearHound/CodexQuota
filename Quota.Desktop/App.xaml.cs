using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Quota.Core;

namespace Quota.Desktop;
public partial class App : System.Windows.Application
{
    public App() { WindowSurface.Register(); }
    private Mutex? instance;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wakeRegistration;
    private EventWaitHandle? refresh;
    private RegisteredWaitHandle? refreshRegistration;
    private DispatcherTimer? desktopRecovery;
    private bool shuttingDown;
    private bool showAfterDesktopRecovery;
    private void WakeWindow()
    {
        if (shuttingDown || Dispatcher.HasShutdownStarted) return;
        if (desktopRecovery is not null) { showAfterDesktopRecovery = true; return; }
        if (MainWindow is { IsLoaded: true } window)
        {
            window.Show(); window.WindowState = WindowState.Normal;
            WindowPlacementGuard.EnsureVisible(window); window.Activate();
        }
    }
    internal void RecoverDesktopWindow(MainWindow previous, bool demo, bool show)
    {
        if (shuttingDown || Dispatcher.HasShutdownStarted || desktopRecovery is not null) return;
        var recovery = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        desktopRecovery = recovery;
        recovery.Tick += (_, _) =>
        {
            recovery.Stop(); desktopRecovery = null;
            if (shuttingDown || Dispatcher.HasShutdownStarted || MainWindow is MainWindow current && current != previous && current.IsLoaded) return;
            var showReplacement = show || showAfterDesktopRecovery; showAfterDesktopRecovery = false;
            var replacement = new MainWindow(demo); MainWindow = replacement;
            if (!showReplacement) replacement.Loaded += (_, _) => replacement.Hide();
            replacement.Show();
        };
        recovery.Start();
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = Array.IndexOf(e.Args, "--demo") >= 0;
        var instanceName = demo ? "Local\\CodexQuotaWidget.Demo.v2" : "Local\\CodexQuotaWidget.v2";
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var dataDirectory = demo ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexQuotaDemo") : WidgetSnapshot.DefaultDirectory;
        var switchIndex = Array.IndexOf(e.Args, "--switch-account");
        if (switchIndex >= 0 && e.Args.Length > switchIndex + 1 && Guid.TryParseExact(e.Args[switchIndex + 1], "N", out _))
        {
            var requestDirectory = WidgetSnapshot.DefaultDirectory; System.IO.Directory.CreateDirectory(requestDirectory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(requestDirectory, "widget-switch-request.txt"), e.Args[switchIndex + 1]);
        }
        if (dataIndex >= 0 && e.Args.Length > dataIndex + 1)
        {
            dataDirectory = System.IO.Path.GetFullPath(e.Args[dataIndex + 1]);
            var directory = dataDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar).ToUpperInvariant();
            instanceName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..16];
        }
        instance = new Mutex(true, instanceName, out var created);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Wake");
        refresh = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Refresh");
        if (!created) { if (switchIndex >= 0 || Array.IndexOf(e.Args, "--background") >= 0) refresh.Set(); else wake.Set(); Shutdown(); return; }
        wakeRegistration = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(WakeWindow), null, Timeout.Infinite, false);
        refreshRegistration = ThreadPool.RegisterWaitForSingleObject(refresh, (_, _) => Dispatcher.BeginInvoke(async () => { if (MainWindow is MainWindow { IsLoaded: true } window) await window.RefreshFromWidgetAsync(); }), null, Timeout.Infinite, false);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            try { System.IO.Directory.CreateDirectory(dataDirectory); System.IO.File.WriteAllText(System.IO.Path.Combine(dataDirectory, "last-error.txt"), DateTimeOffset.Now.ToString("O") + "\n" + args.Exception.GetType().FullName + "\n" + args.Exception.StackTrace); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
            DialogLayout.Alert(MainWindow, "Codex Quota", L.T("应用遇到意外错误，需要重新启动。错误类型已保存在本机 last-error.txt。")); Shutdown(1);
        };
        try { MainWindow = new MainWindow(demo); if (Array.IndexOf(e.Args, "--background") >= 0) MainWindow.Loaded += (_, _) => MainWindow.Hide(); MainWindow.Show(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { DialogLayout.Alert(null, "Codex Quota", L.T("无法读取本地数据目录，请检查当前用户的文件访问权限。")); Shutdown(1); }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { base.OnSessionEnding(e); shuttingDown = !e.Cancel; if (shuttingDown) desktopRecovery?.Stop(); }
    protected override void OnExit(ExitEventArgs e) { shuttingDown = true; desktopRecovery?.Stop(); refreshRegistration?.Unregister(null); refresh?.Dispose(); wakeRegistration?.Unregister(null); wake?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
