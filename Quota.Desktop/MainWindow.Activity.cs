using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Diagnostics;
using Quota.Core;

namespace Quota.Desktop;

public partial class MainWindow
{
    public CodexStatusViewModel CodexStatus { get; } = new();
    private CodexActivityMonitor? activityMonitor;
    private readonly DispatcherTimer activityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CodexActivitySnapshot activity = new(CodexActivityKind.Unknown);
    private CodexSessionIssues? sessionIssues;
    private SessionIssuesWindow? sessionIssuesWindow;
    private bool activityStarted;
    private bool activityReading;
    private string lastAttention = "";
    private string? activityPersistenceNotice;
    private Action<Uri> openThread = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    private void ApplyActivityMonitor()
    {
        if (!activityStarted)
        {
            activityStarted = true;
            RuntimeLamp.Status = CodexStatus;
            CodexStatus.OpenInterruptedTaskRequested += OpenInterruptedTask;
            activityTimer.Tick += async (_, _) => await ReadActivityAsync();
            Closed += (_, _) => activityTimer.Stop();
            if (demo)
            {
                var arguments = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(arguments, "--demo-status");
                var kind = index >= 0 && index + 1 < arguments.Length && Enum.TryParse<CodexActivityKind>(arguments[index + 1], true, out var parsed) ? parsed : CodexActivityKind.Working;
                activity = new(kind, kind == CodexActivityKind.Working ? 1 : 0, kind == CodexActivityKind.NeedsInput ? 1 : 0, 1, "demo-input");
            }
            else { activityTimer.Start(); _ = ReadActivityAsync(); }
        }
        UpdateCodexStatus();
    }
    private void OpenInterruptedTask(object? sender, EventArgs args)
    {
        ShowSessionIssues();
    }
    private void SessionIssuesClick(object sender, System.Windows.RoutedEventArgs args) => ShowSessionIssues();
    internal void ShowSessionIssues()
    {
        if (!demo) _ = ReadActivityAsync();
        if (sessionIssuesWindow is { } existing) { existing.Show(); existing.Activate(); return; }
        var dialog = new SessionIssuesWindow(this, OpenSessionThread, AcknowledgeSessionIssue, IgnoreAllSessionIssues);
        sessionIssuesWindow = dialog;
        dialog.Closed += (_, _) => sessionIssuesWindow = null;
        dialog.SetIssues(CodexStatus.SessionIssues); dialog.Show();
    }
    private void AcknowledgeSessionIssue(CodexSessionIssue issue, CodexIssueDisposition disposition) => SaveSessionAcknowledgements([issue], disposition);
    private void IgnoreAllSessionIssues() => SaveSessionAcknowledgements(CodexStatus.SessionIssues.Where(issue => !issue.IsRead).ToArray(), CodexIssueDisposition.Ignored);
    private void SaveSessionAcknowledgements(CodexSessionIssue[] issues, CodexIssueDisposition disposition)
    {
        try
        {
            (sessionIssues ??= new(store)).Acknowledge(issues, disposition); UpdateCodexStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DialogLayout.Alert(sessionIssuesWindow ?? AccountDialogOwner, L.F("未能保存处理状态", "Could not save issue status"),
                L.F("请检查本地数据目录的写入权限后重试。", "Check write access to the local data folder and try again."));
        }
    }
    private void OpenSessionThread(CodexSessionIssue target)
    {
        if (CodexThreadLink.Create(target.ThreadId) is not { } link)
        {
            DialogLayout.Alert(AccountDialogOwner, L.F("无法定位中断任务", "Interrupted task unavailable"),
                L.F("尚未读取到原会话编号，请进入 Codex 手动打开中断的会话。", "The original chat ID is unavailable. Open the interrupted chat manually in Codex.")); return;
        }
        try { openThread(link); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DialogLayout.Alert(AccountDialogOwner, L.F("无法打开中断任务", "Could not open interrupted task"),
                L.F("请确认已安装 Codex 桌面程序，并进入原会话手动继续。", "Check that the Codex desktop app is installed, then open the original chat to continue."));
        }
    }
    private async Task ReadActivityAsync()
    {
        if (activityReading || exiting) return;
        activityReading = true;
        try { var monitor = activityMonitor ??= new(persistence: store); activity = await Task.Run(() => monitor.Read(lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException) { activity = new(CodexActivityKind.Unknown, ReadIssue: CodexActivityReadIssue.ReadFailed); }
        finally { activityReading = false; }
        if (exiting) return;
        if (activityMonitor is { } currentMonitor)
            activityPersistenceNotice = UpdateRecoveryNotice(currentMonitor.PersistenceHealthy,
                L.F("中断状态未能保存，请检查本地数据目录的写入权限。", "Interruption state could not be saved. Check write access to the local data folder."), activityPersistenceNotice);
        UpdateCodexStatus();
        if (activity.Waiting > 0 && activity.AttentionToken != lastAttention)
            taskbarWidget?.ShowInputAttention();
        lastAttention = activity.Waiting > 0 ? activity.AttentionToken : "";
    }
    private void UpdateCodexStatus()
    {
        var current = accounts.FirstOrDefault(source => source.Record.IsCurrent);
        var remaining = current?.Snapshot?.Buckets.SelectMany(bucket => bucket.Windows).Select(window => (double?)window.Remaining).Min();
        var error = current?.HasError == true;
        var display = (sessionIssues ??= new(store)).Apply(activity);
        CodexStatus.Update(display, remaining <= 0, error, remaining <= settings.LowQuotaThreshold);
        sessionIssuesWindow?.SetIssues(CodexStatus.SessionIssues);
    }
}
