using System;
using System.Collections.Generic;
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
    private readonly HashSet<string> lastSessionAttention = [];
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
        if (!CodexStatus.SessionIssues.Any(issue => issue.Key == target.Key && issue.Occurrence == target.Occurrence)) return;
        try
        {
            if (CodexThreadLink.CreateCurrent(target.ThreadId) is not { } link)
            {
                DialogLayout.Alert(AccountDialogOwner, L.F("无法定位中断任务", "Interrupted task unavailable"),
                    L.F("原会话已归档或无法定位，请返回 Codex 查看当前会话。", "The original chat is archived or unavailable. Return to Codex to view the current chat.")); return;
            }
            openThread(link);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
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
    }
    private void UpdateCodexStatus()
    {
        var current = accounts.FirstOrDefault(source => source.Record.IsCurrent);
        var remaining = current?.Snapshot?.Buckets.SelectMany(bucket => bucket.Windows).Select(window => (double?)window.Remaining).Min();
        var error = current?.HasError == true;
        var display = (sessionIssues ??= new(store)).Apply(activity);
        CodexStatus.Update(display, remaining <= 0, error, remaining <= settings.LowQuotaThreshold);
        sessionIssuesWindow?.SetIssues(CodexStatus.SessionIssues);
        UpdateActivityNotification(display);
    }

    private void UpdateActivityNotification(CodexActivitySnapshot display)
    {
        var inputKey = CodexStatus.NeedsInput ? "input/" + display.AttentionToken : "";
        var unread = CodexStatus.SessionIssues.Where(issue => !issue.IsRead).ToArray();
        var fresh = unread.Where(issue => !lastSessionAttention.Contains(issue.Key + "/" + issue.Occurrence)).ToArray();
        if (inputKey.Length > 0 && inputKey != lastAttention)
            ShowAttentionNotification(inputKey, L.F("需要你的输入", "Your input is needed"), CodexStatus.InputMessage, "bell", CodexStatusViewModel.WarningColor);
        else if (fresh.Length > 0 && display.Kind != CodexActivityKind.Offline)
        {
            var issue = fresh[0];
            ShowAttentionNotification("session/" + issue.Key + "/" + issue.Occurrence,
                issue.Kind == CodexActivityKind.Error ? L.F("会话发生错误", "Chat error") : L.F("会话已中断", "Chat interrupted"),
                L.F("{0} 个会话需要处理，可从状态灯或任务栏菜单打开异常列表。", unread.Length == 1
                    ? "1 chat needs attention. Open session issues from the status light or taskbar menu."
                    : "{0} chats need attention. Open session issues from the status light or taskbar menu.", unread.Length), "activity", CodexStatusViewModel.ErrorColor);
        }
        else if (attentionNotification is { IsVisible: true } notification
            && (notification.NotificationKey.StartsWith("input/", StringComparison.Ordinal) && inputKey.Length == 0
                || notification.NotificationKey.StartsWith("session/", StringComparison.Ordinal)
                    && (display.Kind == CodexActivityKind.Offline || !unread.Any(issue => notification.NotificationKey == "session/" + issue.Key + "/" + issue.Occurrence))))
            notification.Dismiss();
        lastAttention = inputKey;
        lastSessionAttention.Clear();
        lastSessionAttention.UnionWith(unread.Select(issue => issue.Key + "/" + issue.Occurrence));
    }
}
