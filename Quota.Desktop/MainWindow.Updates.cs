using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Quota.Core;

namespace Quota.Desktop;

internal sealed class AppUpdateState : Observable
{
    internal string Phase = "idle";
    internal string Version = "";
    private double percent;
    public double Percent { get => percent; set { percent = value; Raise(); } }
    public bool Busy => Phase is "checking" or "downloading" or "installing";
    public bool CanCheck => !Busy;
    public bool CanInstall => Phase == "available";
    public bool CanCancel => Phase == "downloading";
    public string Message => Phase switch
    {
        "checking" => L.F("正在检查 GitHub 正式版…", "Checking stable GitHub releases…"),
        "available" => L.F("发现新版本 {0}。确认后后台下载，短暂重启完成更新。", "Version {0} is available. Download in the background, then restart briefly to update.", Version),
        "downloading" => L.F("正在下载并校验更新包…", "Downloading and verifying the update…"),
        "installing" => L.F("正在交接更新，应用即将重启…", "Handing off the update. The app will restart…"),
        "current" => L.F("当前已是最新正式版。", "You are running the latest stable release."),
        "error" => L.F("更新未完成，请查看更新记录后重试；校验失败不会关闭原程序。", "Update incomplete. Check update history before retrying; failed verification does not close the app."),
        "failed" => L.F("更新未能完成，当前版本仍可使用；请查看更新记录。", "The update did not complete. The current version remains usable; check update history."),
        "rolled-back" => L.F("已恢复上一版本。详情请查看更新记录。", "The previous version was restored. See update history for details."),
        "recovery-needed" => L.F("更新未完成，请打开更新记录，按恢复说明处理。", "Update incomplete. Open update history and follow the recovery instructions."),
        "success" => L.F("更新完成，应用已重新加载。", "Update complete. The application has reloaded."),
        _ => L.F("仅检查正式版；下载和重启前会征求确认。", "Stable releases only. Downloading and restarting require confirmation.")
    };
    internal void Set(string phase, string? version = null)
    {
        Phase = phase; if (version is not null) Version = version;
        foreach (var name in new[] { nameof(Message), nameof(Busy), nameof(CanCheck), nameof(CanInstall), nameof(CanCancel) }) Raise(name);
    }
}

public partial class MainWindow
{
    private readonly AppUpdateState appUpdateState = new();
    private AppUpdate? availableUpdate;
    private CancellationTokenSource? updateDownload;
    private static Version CurrentVersion
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version!; return new(v.Major, v.Minor, Math.Max(0, v.Build)); }
    }
    private static string UpdateJobs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaUpdater", "jobs");
    private async Task InitializeAppUpdatesAsync()
    {
        if (demo) return;
        ReadUpdateOutcome();
        try
        {
            // A healthy candidate renders before the external helper commits success.
            await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token);
            if (appUpdateState.Phase == "idle") ReadUpdateOutcome();
            await Task.Delay(TimeSpan.FromSeconds(12), lifetime.Token);
            if (settings.AutoCheckUpdates && (settings.LastUpdateCheckUtc is null || DateTimeOffset.UtcNow - settings.LastUpdateCheckUtc >= TimeSpan.FromDays(1)))
                await CheckAppUpdateAsync();
        }
        catch (OperationCanceledException) { }
    }
    private void ReadUpdateOutcome()
    {
        if (RecoveryJob() is { } job && File.Exists(Path.Combine(job, "result.json")))
        {
            try
            {
                var result = UpdateInstaller.Read<UpdateResult>(Path.Combine(job, "result.json"));
                appUpdateState.Set(result.Status); if (result.Status != "success") ShowNotice(appUpdateState.Message);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { appUpdateState.Set("recovery-needed"); }
        }
    }
    private void BuildUpdateSettings(Panel parent)
    {
        var section = SettingsControls.Section(parent, L.F("版本与更新", "Version and updates"), "refresh-cw",
            L.F("当前版本 {0} · Windows x64", "Current version {0} · Windows x64", CurrentVersion));
        SettingsControls.Toggle(section, L.F("自动检查更新", "Check for updates automatically"), "refresh-cw", settings.AutoCheckUpdates,
            value => { settings.AutoCheckUpdates = value; SaveSettings(); });
        appUpdateState.Set(appUpdateState.Phase);
        var status = DialogLayout.Text(appUpdateState.Message, 12, true);
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(AppUpdateState.Message)) { Source = appUpdateState }); section.Children.Add(status);
        var progress = new ProgressBar { Maximum = 100, Height = 4, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(progress, L.F("下载进度", "Download progress"));
        progress.SetBinding(ProgressBar.ValueProperty, new Binding(nameof(AppUpdateState.Percent)) { Source = appUpdateState });
        progress.SetBinding(VisibilityProperty, new Binding(nameof(AppUpdateState.CanCancel)) { Source = appUpdateState, Converter = new BoolVisibility() }); section.Children.Add(progress);
        var actions = new WrapPanel();
        var check = SettingsControls.Action(L.F("检查更新", "Check for updates"), "refresh-cw", async () => await CheckAppUpdateAsync());
        check.SetBinding(IsEnabledProperty, new Binding(nameof(AppUpdateState.CanCheck)) { Source = appUpdateState }); actions.Children.Add(check);
        var install = SettingsControls.Action(L.F("下载并更新", "Download and update"), "rotate-cw", async () => await InstallAppUpdateAsync());
        install.SetBinding(VisibilityProperty, new Binding(nameof(AppUpdateState.CanInstall)) { Source = appUpdateState, Converter = new BoolVisibility() }); actions.Children.Add(install);
        var cancel = SettingsControls.Action(L.F("取消下载", "Cancel download"), "x", () => updateDownload?.Cancel());
        cancel.SetBinding(VisibilityProperty, new Binding(nameof(AppUpdateState.CanCancel)) { Source = appUpdateState, Converter = new BoolVisibility() }); actions.Children.Add(cancel);
        section.Children.Add(actions);
        if (RecoveryJob() is { } job) section.Children.Add(SettingsControls.Action(L.F("更新记录与恢复", "Update history and recovery"), "folder-search",
            () => Process.Start(new ProcessStartInfo(job) { UseShellExecute = true })));
        AutomationProperties.SetName(section, L.F("版本与更新", "Version and updates"));
    }
    private static HttpClient CreateUpdateHttp() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private async Task CheckAppUpdateAsync()
    {
        if (appUpdateState.Busy) return;
        if (demo) { appUpdateState.Set("current"); return; }
        appUpdateState.Set("checking");
        settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow; SaveSettings();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var http = CreateUpdateHttp();
            availableUpdate = await new AppUpdateClient(http).CheckAsync(CurrentVersion, timeout.Token);
            appUpdateState.Set(availableUpdate is null ? "current" : "available", availableUpdate?.Version.ToString());
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { if (!exiting) appUpdateState.Set("error"); }
    }
    private async Task InstallAppUpdateAsync()
    {
        if (demo || availableUpdate is not { } update || appUpdateState.Busy) return;
        var executable = Environment.ProcessPath!;
        var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(executable).Equals("CodexQuota.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(root, "install-files.json")))
        {
            DialogLayout.Alert(AccountDialogOwner, L.F("原地更新不可用", "In-place update unavailable"),
                L.F("请使用 2.3 或之后版本的完整便携版目录。源码运行或旧版 Windows 小组件包不支持原地更新。", "Use the complete portable folder from version 2.3 or later. Source builds and old Windows Widgets packages do not support in-place updates.")); return;
        }
        if (loggingIn || switching || refreshing)
        {
            DialogLayout.Alert(AccountDialogOwner, L.F("请稍后更新", "Update when ready"), L.F("请等待账号连接、切换或额度刷新完成后再更新。", "Wait for account connection, switching or quota refresh to finish before updating.")); return;
        }
        if (!DialogLayout.Confirm(AccountDialogOwner, L.F("更新到 {0}", "Update to {0}", update.Version),
            L.F("后台下载并校验后，应用会短暂退出、原地替换并重新打开。账号与设置保留，启动失败自动恢复上一版。", "After downloading and verification, the app will briefly quit, replace its files and reopen. Accounts and settings are retained; startup failure rolls back to the previous version."),
            L.F("下载并重启", "Download and restart"), L.F("取消", "Cancel"))) return;
        var job = Path.Combine(UpdateJobs, Guid.NewGuid().ToString("N"));
        updateDownload = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            Directory.CreateDirectory(job); appUpdateState.Percent = 0; appUpdateState.Set("downloading");
            settings.LastUpdateJob = job; SaveSettings(); QueueSettingsRender();
            using var http = CreateUpdateHttp();
            var progress = new Progress<double>(value => appUpdateState.Percent = value);
            var cancellation = updateDownload.Token;
            await Task.Run(() => new AppUpdateClient(http).DownloadAsync(update, Path.Combine(job, "download.zip"), progress, cancellation));
            await Task.Run(() => UpdatePackage.ExtractAsync(Path.Combine(job, "download.zip"), Path.Combine(job, "stage"), update.Version, updateDownload.Token));
            var product = FileVersionInfo.GetVersionInfo(Path.Combine(job, "stage", "CodexQuota.exe")).ProductVersion?.Split('+')[0];
            if (product != update.Version.ToString()) throw new InvalidDataException("The application version differs from release metadata.");
            if (loggingIn || switching || refreshing) throw new IOException("Wait for the current account operation to finish and retry.");
            appUpdateState.Set("installing");
            IsEnabled = false; if (settingsWindow is { } preferences) preferences.IsEnabled = false;
            if (taskbarWidget is { } strip) strip.IsEnabled = false;
            if (guideWindow is { } guide) guide.IsEnabled = false;
            var restart = new System.Collections.Generic.List<string>();
            if (!IsVisible) restart.Add("--background");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--data-dir") >= 0) { restart.Add("--data-dir"); restart.Add(store.Root); }
            using var parent = Process.GetCurrentProcess();
            var plan = new UpdatePlan(root, job, update.Version.ToString(), parent.Id, parent.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"), restart.ToArray());
            await Task.Run(() => { File.Copy(executable, Path.Combine(job, "Updater.exe")); UpdateInstaller.Write(Path.Combine(job, "plan.json"), plan); _ = UpdateInstaller.Validate(plan); });
            File.WriteAllText(Path.Combine(job, "RECOVERY.txt"), "Exit CodexQuota before recovery. / 恢复前请退出 CodexQuota。\r\n& \"" + Path.Combine(job, "Updater.exe") + "\" --recover-update \"" + Path.Combine(job, "plan.json") + "\"\r\n");
            settings.LastUpdateJob = job; SaveSettings();
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateRunner.EventName(plan.Nonce, "Ready"));
            var start = new ProcessStartInfo(Path.Combine(job, "Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(Path.Combine(job, "plan.json"));
            using var helper = Process.Start(start) ?? throw new IOException("The updater did not start.");
            var readiness = Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20)));
            await Task.WhenAny(readiness, helper.WaitForExitAsync());
            if (!ready.WaitOne(0) || helper.HasExited) throw new IOException("The updater could not prepare the installation.");
            Exit();
        }
        catch (OperationCanceledException) { if (!exiting) appUpdateState.Set("available"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            appUpdateState.Set("error");
            try { UpdateInstaller.Write(Path.Combine(job, "download-error.json"), new UpdateResult("failed", update.Version.ToString(), error.GetType().Name + ": " + error.Message)); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            updateDownload?.Dispose(); updateDownload = null;
            if (!exiting) { IsEnabled = true; if (settingsWindow is { } preferences) preferences.IsEnabled = true; if (taskbarWidget is { } strip) strip.IsEnabled = true; if (guideWindow is { } guide) guide.IsEnabled = true; }
        }
    }
    private string? RecoveryJob()
    {
        if (settings.LastUpdateJob is not { } job) return null;
        try
        {
            var path = Path.GetFullPath(job);
            return Path.GetDirectoryName(path)?.Equals(Path.GetFullPath(UpdateJobs), StringComparison.OrdinalIgnoreCase) == true && Guid.TryParseExact(Path.GetFileName(path), "N", out _) && Directory.Exists(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }
}
