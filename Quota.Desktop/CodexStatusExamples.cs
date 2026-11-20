using Quota.Core;

namespace Quota.Desktop;

public static class CodexStatusExamples
{
    internal const int Count = 9;
    public static (string Name, string Pattern, string Detail, CodexActivityKind Kind, bool Exhausted, bool QuotaError, bool Low) Get(int index) => index switch
    {
        0 => (L.F("正在执行", "Working"), L.F("绿灯 · 呼吸", "Green · breathing"), L.F("检测到 Codex 正在进行任务。可继续使用电脑，状态灯会持续更新。", "Codex is actively working. The light updates as work progresses."), CodexActivityKind.Working, false, false, false),
        1 => (L.F("全部完成", "Completed"), L.F("绿灯 · 常亮", "Green · steady"), L.F("已观察到的 Codex 任务均已完成，可以返回会话查看结果。", "All observed Codex tasks have completed. Open the chat to review the result."), CodexActivityKind.Completed, false, false, false),
        2 => (L.F("需要输入", "Input needed"), L.F("黄灯 · 快速闪烁", "Yellow · fast flashing"), L.F("Codex 正在等待回答或确认。任务栏小组件会展开提示，返回会话处理后即可继续。", "Codex is waiting for an answer or confirmation. The taskbar widget shows a prompt. Return to the chat to continue."), CodexActivityKind.NeedsInput, false, false, false),
        3 => (L.F("额度偏低", "Low quota"), L.F("黄灯 · 常亮", "Yellow · steady"), L.F("当前账号剩余额度达到预警阈值。默认 5%，可在设置中调整。", "The current account has reached the low-quota threshold. The default is 5% and can be changed in Settings."), CodexActivityKind.Working, false, false, true),
        4 => (L.F("额度耗尽", "Quota exhausted"), L.F("红灯 · 常亮", "Red · steady"), L.F("当前账号额度已用尽。等待重置，或切换到有可用额度的账号。", "The current account has no quota available. Wait for a reset or switch to an account with available quota."), CodexActivityKind.Working, true, false, false),
        5 => (L.F("额度读取异常", "Quota read error"), L.F("红灯 · 快速闪烁", "Red · fast flashing"), L.F("当前账号的额度暂时无法读取。悬停状态灯查看原因，或从主面板的「来源需处理」打开详情。", "The current account's quota is temporarily unavailable. Hover over the light for the reason or open the account summary in the main panel."), CodexActivityKind.Working, false, true, false),
        6 => (L.F("会话出错", "Chat error"), L.F("红灯 · 快速闪烁", "Red · fast flashing"), L.F("Codex 会话出现错误。单击状态灯打开「异常会话」，再进入原会话查看。", "A Codex chat encountered an error. Click the light to open Session issues and return to the original chat."), CodexActivityKind.Error, false, false, false),
        7 => (L.F("任务中断", "Interrupted"), L.F("红灯 · 快速闪烁", "Red · fast flashing"), L.F("任务被取消或运行中退出后，会保留中断记录。单击状态灯查看异常会话，可打开会话、忽略本次异常。再次发生中断时会重新提醒。", "Canceled work or an exit during a task leaves an interruption record. Click the light to open the issue list, visit a chat, ignore the occurrence. A new interruption alerts again."), CodexActivityKind.Paused, false, false, false),
        _ => (L.F("未运行 / 状态未知", "Offline / unknown"), L.F("灰灯 · 常亮", "Gray · steady"), L.F("未检测到 Codex 运行时显示灰灯。Codex 已运行但尚无可读取的任务状态时也显示灰灯。", "Gray means Codex is not running, or it is running without readable task activity yet."), CodexActivityKind.Offline, false, false, false)
    };
}
