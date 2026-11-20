using Quota.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Quota.Desktop;

public enum CodexSignal { Unknown, Offline, Paused, Error, Exhausted, Low, Completed, NeedsInput, Working, Idle }
public enum CodexLampAnimation { Steady, Breathing, Flashing }
public sealed class CodexStatusViewModel : Observable
{
    internal const string ErrorColor = "#F04438";
    internal const string WarningColor = "#F2B600";
    internal const string SuccessColor = "#00C46A";
    private string language = L.Language;
    private (bool Quota, bool Session, string? QuotaDetail) errors;
    public CodexSignal Signal { get; private set; }
    public CodexActivityReadIssue ReadIssue { get; private set; }
    public IReadOnlyList<CodexInterruptedTask> InterruptedTasks { get; private set; } = Array.Empty<CodexInterruptedTask>();
    public IReadOnlyList<CodexSessionIssue> SessionIssues { get; private set; } = Array.Empty<CodexSessionIssue>();
    public int UnreadSessionCount => SessionIssues.Count(issue => !issue.IsRead);
    public bool CanOpenInterruptedTask => Signal == CodexSignal.Paused || InterruptedTasks.Count > 0 || SessionIssues.Count > 0;
    public event EventHandler? OpenInterruptedTaskRequested;
    public void OpenInterruptedTask() { if (CanOpenInterruptedTask) OpenInterruptedTaskRequested?.Invoke(this, EventArgs.Empty); }
    public CodexLampAnimation Animation => Signal switch
    {
        CodexSignal.Error or CodexSignal.NeedsInput or CodexSignal.Paused => CodexLampAnimation.Flashing,
        CodexSignal.Working => CodexLampAnimation.Breathing,
        _ => CodexLampAnimation.Steady
    };
    public bool Breathing => Animation == CodexLampAnimation.Breathing;
    public bool Flashing => Animation == CodexLampAnimation.Flashing;
    public bool NeedsInput { get; private set; }
    public string Color => Signal switch
    {
        CodexSignal.Error or CodexSignal.Exhausted or CodexSignal.Paused => ErrorColor,
        CodexSignal.Low or CodexSignal.NeedsInput => WarningColor,
        CodexSignal.Completed or CodexSignal.Working or CodexSignal.Idle => SuccessColor,
        _ => "#82938D"
    };
    public string Message => Signal switch
    {
        CodexSignal.Error => DescribeErrors(),
        CodexSignal.Exhausted => L.F("当前 Codex 账号额度已用尽。", "The current Codex account has exhausted its quota."),
        CodexSignal.Low => L.F("当前 Codex 账号额度偏低。", "The current Codex account is running low on quota."),
        CodexSignal.NeedsInput => InputMessage,
        CodexSignal.Working => L.F("Codex 正在执行任务。", "Codex is working."),
        CodexSignal.Idle => L.F("暂无运行中的 Codex 任务。", "No Codex tasks are running."),
        CodexSignal.Completed => L.F("Codex 任务已全部完成。", "All observed Codex tasks are complete."),
        CodexSignal.Paused => L.F("有 Codex 任务已中断，请进入原会话手动继续。", "Codex work was interrupted. Open the original chat to continue."),
        CodexSignal.Offline => L.F("Codex 未运行。", "Codex is not running."),
        _ => ReadIssue switch
        {
            CodexActivityReadIssue.ProcessMetadataUnavailable => L.F("Windows 暂时无法读取 Codex 进程信息，正在自动重试。", "Windows could not read Codex process information. Retrying automatically."),
            CodexActivityReadIssue.SessionsMissing => L.F("尚未找到本机 Codex 会话记录。请打开 Codex 并开始任务。", "Local Codex session records were not found. Open Codex and start a task."),
            CodexActivityReadIssue.NoSessionEvents => L.F("Codex 已运行，正在等待会话状态事件。", "Codex is running. Waiting for session status events."),
            CodexActivityReadIssue.SessionsInaccessible => L.F("暂时无法读取本机 Codex 会话文件，正在自动重试。", "Local Codex session files are temporarily unreadable. Retrying automatically."),
            CodexActivityReadIssue.ReadFailed => L.F("Codex 状态检测暂时失败，正在自动重试。", "Codex status detection temporarily failed. Retrying automatically."),
            _ => L.F("Codex 状态暂不可读。", "Codex status is unavailable.")
        }
    };
    private string DescribeErrors()
    {
        var session = "";
        if (errors.Session)
        {
            var count = SessionIssues.Count(issue => !issue.IsRead && issue.Kind == CodexActivityKind.Error);
            session = count > 1
                ? L.F("{0} 个 Codex 会话出错。", "{0} Codex chats encountered errors.", count)
                : L.F("Codex 会话出错。", "A Codex chat encountered an error.");
        }
        if (!errors.Quota) return session;
        var quota = L.F("当前账号额度读取异常", "Current account quota read error");
        if (errors.QuotaDetail is { Length: > 0 } detail) quota += Environment.NewLine + detail;
        quota += Environment.NewLine + L.F("点击主面板的「来源需处理」查看详情。", "Open the account summary in the main panel for details.");
        return session.Length > 0 ? quota + Environment.NewLine + Environment.NewLine + session : quota;
    }
    public string InputMessage => L.F("Codex 需要你的输入，请返回 Codex 查看并继续。", "Codex needs your input. Return to Codex to review and continue.");
    public string Tooltip
    {
        get
        {
            var count = UnreadSessionCount;
            return count == 0 ? Message : Message + Environment.NewLine
                + L.F("单击查看 {0} 个异常会话。", count == 1 ? "Click to view {0} session issue." : "Click to view {0} session issues.", count);
        }
    }

    public void Update(CodexActivitySnapshot activity, bool exhausted = false, bool quotaError = false, bool low = false, string? quotaErrorDetail = null)
    {
        var previousSignal = Signal; var previousNeedsInput = NeedsInput; var previousReadIssue = ReadIssue;
        var previousErrors = errors;
        ReadIssue = activity.ReadIssue;
        var interrupted = (activity.InterruptedTasks ?? Array.Empty<CodexInterruptedTask>()).Where(task => CodexThreadLink.NormalizeId(task.ThreadId) is not null).OrderByDescending(task => task.InterruptedAt).ToArray();
        var issues = activity.SessionIssues ?? Array.Empty<CodexSessionIssue>();
        var changedTasks = !InterruptedTasks.SequenceEqual(interrupted) || !SessionIssues.SequenceEqual(issues);
        SessionIssues = issues;
        InterruptedTasks = interrupted;
        NeedsInput = activity.Kind != CodexActivityKind.Offline && (activity.Waiting > 0 || activity.Kind == CodexActivityKind.NeedsInput);
        Signal = activity.Kind == CodexActivityKind.Offline ? CodexSignal.Offline
            : quotaError || activity.Kind == CodexActivityKind.Error ? CodexSignal.Error
            : exhausted ? CodexSignal.Exhausted
            : activity.Kind == CodexActivityKind.Paused ? CodexSignal.Paused
            : NeedsInput ? CodexSignal.NeedsInput
            : low ? CodexSignal.Low
            : activity.Kind switch
            {
                CodexActivityKind.Idle => CodexSignal.Idle,
                CodexActivityKind.Working => CodexSignal.Working,
                CodexActivityKind.Completed => CodexSignal.Completed,
                _ => CodexSignal.Unknown
            };
        errors = Signal == CodexSignal.Error ? (quotaError, activity.Kind == CodexActivityKind.Error, quotaError ? quotaErrorDetail : null) : default;
        if (Signal == previousSignal && NeedsInput == previousNeedsInput && ReadIssue == previousReadIssue && errors == previousErrors && !changedTasks && language == L.Language) return;
        language = L.Language;
        Raise(nameof(Signal)); Raise(nameof(ReadIssue)); Raise(nameof(Animation)); Raise(nameof(Breathing)); Raise(nameof(Flashing)); Raise(nameof(Color)); Raise(nameof(Message)); Raise(nameof(NeedsInput)); Raise(nameof(InputMessage));
        Raise(nameof(SessionIssues)); Raise(nameof(UnreadSessionCount)); Raise(nameof(InterruptedTasks)); Raise(nameof(CanOpenInterruptedTask)); Raise(nameof(Tooltip));
    }
}
