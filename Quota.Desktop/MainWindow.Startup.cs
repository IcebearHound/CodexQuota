using System;
using System.Windows;
using System.Windows.Controls;

namespace Quota.Desktop;

public partial class MainWindow
{
    private string? startupNotice;

    private void RefreshStartupRegistration()
    {
        var args = Environment.GetCommandLineArgs();
        // Don't write Run while Explorer is enumerating it, or migrate a candidate that may roll back.
        if (demo || Array.Exists(args, arg => arg is "--data-dir" or "--background" or "--update-health")) return;
        try { StartupRegistration.ForCurrentProcess().RefreshRegisteredPath(); }
        catch (Exception error) when (StartupRegistration.IsAccessFailure(error))
        {
            startupNotice = L.F("未能更新登录启动任务。可以在设置中重试。", "Could not update the sign-in task. Retry in Settings.");
        }
    }

    internal void BuildStartupSetting(Panel parent)
    {
        var registration = StartupRegistration.ForCurrentProcess();
        var toggle = new CheckBox { Content = SettingsControls.Label("开机启动", "power"), Margin = new Thickness(0, 6, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(toggle, L.T("开机启动"));
        var description = L.F("登录 Windows 时直接启动到托盘，不打开主窗口。", "Start in the tray at Windows sign-in without opening the main window.");
        var hint = DialogLayout.Text(description, 11, true);
        hint.Margin = new Thickness(44, 0, 0, 10);
        void ReadState()
        {
            try { toggle.IsChecked = !demo && registration.IsEnabled; }
            catch (Exception error) when (StartupRegistration.IsAccessFailure(error))
            {
                toggle.IsEnabled = false;
                hint.Text = L.F("无法读取开机启动状态，请检查 Windows 任务计划程序。", "Could not read startup status. Check Windows Task Scheduler.");
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            }
        }
        ReadState();
        var restoring = false;
        void Change(bool enabled)
        {
            if (restoring) return;
            if (demo) { hint.Text = L.F("演示模式，开机启动设置仅供预览。", "Demo mode. Startup changes are previewed here."); return; }
            try
            {
                registration.SetEnabled(enabled);
                hint.Text = description;
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            catch (Exception error) when (StartupRegistration.IsAccessFailure(error))
            {
                hint.Text = L.F("未能保存开机启动设置，请检查 Windows 任务计划程序后重试。", "Could not save startup settings. Check Windows Task Scheduler and retry.");
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Error");
            }
            restoring = true;
            ReadState();
            restoring = false;
        }
        toggle.Checked += (_, _) => Change(true);
        toggle.Unchecked += (_, _) => Change(false);
        parent.Children.Add(toggle); parent.Children.Add(hint);
    }
}
