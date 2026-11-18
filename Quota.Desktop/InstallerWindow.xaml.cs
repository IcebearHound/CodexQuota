using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using Microsoft.Win32;
using Quota.Core;

namespace Quota.Desktop;

internal partial class InstallerWindow : Window
{
    private enum Stage { Location, Installing, Ready }
    private readonly string engine;
    private readonly string? registeredLocation;
    private Stage stage;
    private string? destination, failure, log;

    internal InstallerWindow(string engine, string? registeredLocation)
    {
        this.engine = engine;
        this.registeredLocation = registeredLocation;
        InitializeComponent();
        MaxWidth = Math.Max(1, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 24);
        MinWidth = Math.Min(MinWidth, MaxWidth); MinHeight = Math.Min(MinHeight, MaxHeight);
        ParentBox.Text = registeredLocation ?? InstallLocation.DefaultParent;
        ParentBox.IsReadOnly = registeredLocation is not null;
        BrowseButton.IsEnabled = registeredLocation is null;
        Loaded += (_, _) => WindowPlacementGuard.EnsureVisible(this);
        Closing += (_, args) => args.Cancel = stage == Stage.Installing;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Render();
    }

    private void Render()
    {
        Title = L.F("安装 Codex Quota", "Install Codex Quota");
        LanguageButton.Content = L.IsEnglish ? "中文" : "English";
        CloseButton.ToolTip = L.T("关闭");
        SidebarLabel.Text = L.F("安装向导", "SETUP");
        BuildLabel.Text = $"Windows x64  ·  {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)}";
        LocationStep.Text = L.F("01    安装位置", "01    Destination");
        CopyStep.Text = L.F("02    安装文件", "02    Installation");
        ReadyStep.Text = L.F("03    开始使用", "03    Get started");
        foreach (var (step, index) in new[] { (LocationStep, Stage.Location), (CopyStep, Stage.Installing), (ReadyStep, Stage.Ready) })
        {
            step.FontWeight = stage == index ? FontWeights.SemiBold : FontWeights.Normal;
            step.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, stage == index ? "Text" : "Muted");
        }
        (Heading.Text, Description.Text) = stage switch
        {
            Stage.Installing => (L.F("正在安装 Codex Quota", "Installing Codex Quota"), L.F("正在写入程序文件和开始菜单快捷方式，请稍候。", "Copying program files and creating a Start menu shortcut.")),
            Stage.Ready => (L.F("安装完成", "Ready to go"), L.F("打开使用导览，连接账号并按需设置开机启动。", "Open the guide to connect your accounts and choose whether to start with Windows.")),
            _ => (L.F("选择安装位置", "Choose where to install"), registeredLocation is null
                ? L.F("选择上级目录。安装程序会自动创建 CodexQuota 子文件夹。", "Choose a parent folder. Setup adds a CodexQuota subfolder automatically.")
                : L.F("沿用已有安装位置。若要更换，请先卸载；账号和设置会保留。", "Setup will use the existing location. Uninstall before changing it; your accounts and settings will be kept."))
        };
        PathCard.Visibility = stage == Stage.Location ? Visibility.Visible : Visibility.Collapsed;
        ProgressCard.Visibility = stage == Stage.Installing ? Visibility.Visible : Visibility.Collapsed;
        ReadyCard.Visibility = stage == Stage.Ready ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.IsIndeterminate = stage == Stage.Installing;
        ParentLabel.Text = registeredLocation is null ? L.F("上级目录", "Parent folder") : L.F("已有安装", "Existing installation");
        AutomationProperties.SetName(ParentBox, ParentLabel.Text);
        BrowseButton.Content = L.F("浏览…", "Browse…");
        DestinationLabel.Text = L.F("最终安装位置", "Install location");
        InstallingPath.Text = destination;
        InstalledLocation.Text = destination;
        OpenGuide.Content = L.F("启动 Codex Quota 并打开使用导览", "Launch Codex Quota and open the guide");
        LogButton.Content = L.F("查看安装日志", "View setup log");
        CancelButton.Content = L.F("取消", "Cancel");
        CancelButton.Visibility = stage == Stage.Ready ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.IsEnabled = CancelButton.IsEnabled = stage != Stage.Installing;
        ContinueButton.Content = stage switch
        {
            Stage.Ready => L.F("完成", "Finish"), Stage.Installing => L.F("正在安装…", "Installing…"), _ => L.F("安装", "Install")
        };
        FooterNote.Text = L.F("账号和设置单独保存，不会被覆盖。", "Accounts and preferences are kept separately.");
        UpdateDestination();
    }

    private void UpdateDestination()
    {
        if (stage == Stage.Location)
        {
            destination = registeredLocation ?? InstallLocation.Resolve(ParentBox.Text);
            DestinationText.Text = destination ?? "—";
        }
        var missingDrive = destination is not null && !Directory.Exists(Path.GetPathRoot(destination));
        ContinueButton.IsEnabled = stage == Stage.Ready || stage == Stage.Location && destination is not null && !missingDrive;
        var message = failure is not null ? FailureText(failure) : stage != Stage.Location ? null : destination is null
            ? L.F("请输入完整的安装路径。", "Enter an absolute installation path.")
            : missingDrive ? L.F("此盘符或共享位置不可用，请选择其他目录。", "This drive or share is unavailable. Choose a different folder.") : null;
        Feedback.Text = message ?? "";
        Feedback.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        LogButton.Visibility = failure is not null && File.Exists(log) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ParentChanged(object sender, System.Windows.Controls.TextChangedEventArgs args)
    {
        if (!IsInitialized) return;
        failure = null; log = null; UpdateDestination();
    }

    private void Browse(object sender, RoutedEventArgs args)
    {
        var picker = new OpenFolderDialog { Title = L.F("选择程序安装的上级目录", "Choose the parent installation folder") };
        if (Directory.Exists(ParentBox.Text)) picker.InitialDirectory = ParentBox.Text;
        if (picker.ShowDialog(this) == true) ParentBox.Text = picker.FolderName;
    }

    private async void ContinueClick(object sender, RoutedEventArgs args)
    {
        if (stage == Stage.Installing || destination is not { } path) return;
        if (stage == Stage.Ready)
        {
            if (OpenGuide.IsChecked != true) { Close(); return; }
            try
            {
                var launch = new ProcessStartInfo(Path.Combine(path, "CodexQuota.exe")) { UseShellExecute = false, WorkingDirectory = path };
                launch.ArgumentList.Add("--guide");
                Process.Start(launch)?.Dispose(); Close();
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            { failure = "launch"; UpdateDestination(); }
            return;
        }
        DirectoryInfo? job = null;
        failure = null; log = null;
        stage = Stage.Installing; Render();
        try
        {
            job = Directory.CreateTempSubdirectory("CodexQuotaSetup-");
            log = Path.Combine(job.FullName, "setup.log");
            var outcome = Path.Combine(job.FullName, "result.txt");
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", L.IsEnglish ? "/LANG=en" : "/LANG=zh", $"/DIR={path}", $"/LOG={log}", $"/CQRESULT={outcome}" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && File.Exists(Path.Combine(path, "CodexQuota.exe")))
            {
                stage = Stage.Ready;
                try { File.Delete(log); File.Delete(outcome); job.Delete(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                log = null;
            }
            else failure = File.Exists(outcome) ? (await File.ReadAllTextAsync(outcome)).Trim() : "failed";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { failure = "failed"; }
        finally { if (stage == Stage.Installing) stage = Stage.Location; Render(); }
    }

    private static string FailureText(string error) => error switch
    {
        "running" => L.F("Codex Quota 正在运行。请从托盘菜单退出后重试。", "Codex Quota is running. Quit it from the tray, then try again."),
        "occupied" => L.F("目标目录非空。请选择新目录或空目录。", "The destination is not empty. Choose a new or empty folder."),
        "location" => L.F("已有安装位于其他目录。请关闭并重新打开安装程序。", "An installation already exists elsewhere. Close and reopen Setup."),
        "newer" => L.F("已安装更新版本，不会覆盖。", "A newer version is installed. Setup did not replace it."),
        "launch" => L.F("程序已安装，但无法自动打开。请从开始菜单启动 Codex Quota。", "Installed successfully, but could not open the app. Launch Codex Quota from Start."),
        _ => L.F("安装未完成。请检查目标目录的写入权限、剩余磁盘空间，或查看日志。", "Installation did not finish. Check write access and free space, or review the setup log.")
    };

    private void OpenLog(object sender, RoutedEventArgs args)
    {
        if (!File.Exists(log)) return;
        try { Process.Start(new ProcessStartInfo(log!) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { Feedback.Text += "\n" + log; }
    }

    private void SwitchLanguage(object sender, RoutedEventArgs args) { L.Apply(L.IsEnglish ? "zh-CN" : "en"); Render(); }
    private void CancelClick(object sender, RoutedEventArgs args) => Close();
    private void DragHeader(object sender, MouseButtonEventArgs args) { if (!args.Handled && args.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
