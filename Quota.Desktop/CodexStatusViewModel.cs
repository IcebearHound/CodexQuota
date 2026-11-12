using Quota.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Quota.Desktop;

public enum CodexSignal { Unknown, Offline, Paused, Error, Exhausted, Low, Completed, NeedsInput, Working, Idle }
public enum CodexLampAnimation { Steady, Breathing, Flashing }
public sealed class CodexStatusViewModel : Observable
{
    private string language = L.Language;
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
        CodexSignal.Error or CodexSignal.Exhausted or CodexSignal.Paused => "#DB5267",
        CodexSignal.Low or CodexSignal.NeedsInput => "#D5A13E",
        CodexSignal.Completed or CodexSignal.Working or CodexSignal.Idle => "#35B582",
        _ => "#82938D"
    };
    public string Message => Signal switch
    {
        CodexSignal.Error => L.F("Codex 出现错误，请查看账号处理详情或 Codex 会话。", "Codex needs attention. Check account details or your Codex chat."),
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
    public string InputMessage => L.F("Codex 需要你的输入，请返回 Codex 查看并继续。", "Codex needs your input. Return to Codex to review and continue.");
    public string Tooltip => Message + (CanOpenInterruptedTask ? Environment.NewLine + L.F("单击查看 {0} 个异常会话。", "Click to view {0} session issues.", UnreadSessionCount) : "");

    public void Update(CodexActivitySnapshot activity, bool exhausted = false, bool quotaError = false, bool low = false)
    {
        var previousSignal = Signal; var previousNeedsInput = NeedsInput; var previousReadIssue = ReadIssue;
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
        if (Signal == previousSignal && NeedsInput == previousNeedsInput && ReadIssue == previousReadIssue && !changedTasks && language == L.Language) return;
        language = L.Language;
        Raise(nameof(Signal)); Raise(nameof(ReadIssue)); Raise(nameof(Animation)); Raise(nameof(Breathing)); Raise(nameof(Flashing)); Raise(nameof(Color)); Raise(nameof(Message)); Raise(nameof(NeedsInput)); Raise(nameof(InputMessage));
        Raise(nameof(SessionIssues)); Raise(nameof(UnreadSessionCount)); Raise(nameof(InterruptedTasks)); Raise(nameof(CanOpenInterruptedTask)); Raise(nameof(Tooltip));
    }
}
