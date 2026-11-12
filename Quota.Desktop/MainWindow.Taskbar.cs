using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Quota.Desktop;

public partial class MainWindow
{
    private TaskbarWidget? taskbarWidget;
    internal bool DetailedTaskbar => settings.DetailedTaskbar;
    internal string TaskbarPosition => settings.TaskbarPlacement;
    private readonly DispatcherTimer taskbarMonitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool taskbarMonitoring;
    private bool taskbarFailureShown;
    private DateTime? taskbarHiddenSince;
    private string? lastTaskbarState;
    internal void OpenTaskbarSettings()
    {
        settingsPage = "小组件";
        SettingsClick(this, new RoutedEventArgs());
        QueueSettingsRender();
    }
    internal void SetTaskbarLayout(bool statusBar)
    {
        settings.DetailedTaskbar = statusBar;
        SaveSettings();
        taskbarWidget?.QueueAlignment();
        QueueSettingsRender();
    }
    internal void ShowTaskbarIssues(AccountGroupViewModel group) => ShowIssues([group]);
    private void ApplyTaskbarWidget()
    {
        if (!taskbarMonitoring)
        {
            taskbarMonitor.Tick += (_, _) => { MaintainDesktopWindows(); UpdateTaskbarWidget(); };
            Closed += (_, _) => { taskbarMonitor.Stop(); taskbarWidget?.Close(); };
            taskbarMonitoring = true; taskbarMonitor.Start();
        }
        UpdateTaskbarWidget();
    }
    private void UpdateTaskbarWidget()
    {
        if (exiting) return;
        if (!WidgetsAllowed || !settings.TaskbarWidgetEnabled) { taskbarWidget?.Close(); taskbarHiddenSince = null; taskbarFailureShown = false; return; }
        if (taskbarWidget is { NativeWindowAlive: false } stale)
        {
            taskbarWidget = null;
            try { stale.Close(); } catch (InvalidOperationException) { }
        }
        if (taskbarWidget is null)
        {
            var created = new TaskbarWidget(this, visibleAccounts, demo ? store.Root : null);
            taskbarWidget = created;
            created.Closed += (_, _) => { if (ReferenceEquals(taskbarWidget, created)) taskbarWidget = null; };
            created.Show();
            if (demo && Array.IndexOf(Environment.GetCommandLineArgs(), "--taskbar-flyout") >= 0) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => created.ShowAccounts());
        }
        else taskbarWidget.Align();
        if (taskbarWidget is { } widget)
        {
            var state = JsonSerializer.Serialize(widget.DockState);
            if (state != lastTaskbarState)
            {
                try { File.WriteAllText(Path.Combine(store.Root, "taskbar-state.json"), state); lastTaskbarState = state; }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
            }
        }
        if (taskbarWidget?.DockError is { } error && !taskbarFailureShown)
        {
            taskbarFailureShown = true;
            ShowNotice(L.F("任务栏停靠失败，系统错误码 {0}。可在设置中关闭任务栏额度条。", "Taskbar docking failed with system error {0}. You can disable the taskbar strip in Settings.", error));
        }
        if (taskbarWidget is { DockVisible: true } or { DockSuppressed: true }) { taskbarHiddenSince = null; taskbarFailureShown = false; }
        else
        {
            taskbarHiddenSince ??= DateTime.UtcNow;
            if (!taskbarFailureShown && DateTime.UtcNow - taskbarHiddenSince > TimeSpan.FromSeconds(15) && taskbarWidget is { } unavailable)
            {
                taskbarFailureShown = true;
                ShowNotice(unavailable.UnavailableMessage);
            }
        }
    }
}
