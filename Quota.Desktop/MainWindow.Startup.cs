using System;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;

namespace Quota.Desktop;

public partial class MainWindow
{
    private string? startupNotice;

    private void RefreshStartupRegistration()
    {
        if (demo || Array.IndexOf(Environment.GetCommandLineArgs(), "--data-dir") >= 0) return;
        try { StartupRegistration.ForCurrentProcess().RefreshRegisteredPath(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            startupNotice = L.F("开机启动路径未能更新，请在设置中重新开启开机启动。", "The startup path could not be updated. Turn startup off and on again in Settings.");
        }
    }

    private void BuildStartupSetting(Panel parent)
    {
        var registration = StartupRegistration.ForCurrentProcess();
        var toggle = new CheckBox { Content = SettingsControls.Label("开机启动", "power"), Margin = new Thickness(0, 6, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(toggle, L.T("开机启动"));
        var hint = DialogLayout.Text(L.F("登录 Windows 后自动启动并收起到托盘。", "Start automatically in the tray when you sign in to Windows."), 11, true);
        hint.Margin = new Thickness(44, 0, 0, 10);
        void ReadState()
        {
            try { toggle.IsChecked = !demo && registration.IsEnabled; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                toggle.IsEnabled = false;
                hint.Text = L.F("开机启动状态读取失败，请检查当前用户的注册表权限。", "Could not read startup status. Check registry access for the current user.");
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
                hint.Text = L.F("登录 Windows 后自动启动并收起到托盘。", "Start automatically in the tray when you sign in to Windows.");
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                hint.Text = L.F("开机启动设置未能保存，请检查当前用户的注册表权限后重试。", "Could not save startup settings. Check registry access for the current user and retry.");
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
