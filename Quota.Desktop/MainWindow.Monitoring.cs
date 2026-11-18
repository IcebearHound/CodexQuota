using System.Linq;
using System.Windows.Controls;

namespace Quota.Desktop;

public partial class MainWindow
{
    public HeaderMetricsViewModel HeaderMetrics { get; } = new();
    public TokenRateViewModel TokenRate => HeaderMetrics.TokenRate;

    internal void BuildMonitoringOptions(Panel parent, bool inGuide = false)
    {
        var section = SettingsControls.Section(parent, L.F("顶栏速率", "Header metrics"), "activity",
            L.F("两项默认开启，显示在顶栏状态心跳右侧。任务栏空间不足时，展开面板仍可查看。", "Both are on by default and appear beside the status heartbeat. Open the taskbar panel if the taskbar is crowded."));
        var tokenHint = TokenRateViewModel.Explanation + (inGuide ? L.F(" 例如：12.5 token/s。", " Example: 12.5 tokens/s.") : "");
        var quotaHint = L.F("仅统计本机当前登录账号。按同一额度窗口连续两次成功刷新的差值计算，重置或采样不足时显示「—」。",
            "Only for the active local account. Calculated from two successful readings of the same quota window. A dash indicates a reset or too few samples.")
            + (inGuide ? L.F(" 例如：2.00/h。", " Example: 2.00/h.") : "");
        SettingsControls.Toggle(section, L.F("本机输出速率（TPS）", "Local output rate (TPS)"), "activity", settings.ShowTokenRate,
            on => SetMonitoringOption(true, on, inGuide), tokenHint);
        SettingsControls.Toggle(section, L.F("额度消耗速率", "Quota consumption rate"), "minus", settings.ShowQuotaPace,
            on => SetMonitoringOption(false, on, inGuide), quotaHint);
    }

    private void SetMonitoringOption(bool tokenRate, bool enabled, bool fromGuide)
    {
        if (tokenRate)
        {
            settings.ShowTokenRate = enabled;
            if (!demo && !enabled) activity = activity with { OutputTokensPerSecond = null };
        }
        else settings.ShowQuotaPace = enabled;
        ApplyMonitoringOptions();
        taskbarWidget?.QueueAlignment();
        SaveSettings();
        if (tokenRate && enabled && !demo) _ = ReadActivityAsync();
        if (fromGuide && settingsPage == "额度监测") QueueSettingsRender();
        else if (!fromGuide) guideWindow?.RefreshMonitoringOptions();
    }

    private void ApplyMonitoringOptions()
    {
        UpdateTokenRate();
        UpdateActiveQuotaPace();
    }

    private void UpdateTokenRate() => TokenRate.Update(settings.ShowTokenRate, activity.OutputTokensPerSecond);
    private void UpdateActiveQuotaPace() => HeaderMetrics.QuotaPace.Update(settings.ShowQuotaPace,
        accounts.FirstOrDefault(account => account.Record.IsCurrent));
}

public sealed class HeaderMetricsViewModel
{
    public TokenRateViewModel TokenRate { get; } = new();
    public ActiveQuotaPaceViewModel QuotaPace { get; } = new();
}

public sealed class ActiveQuotaPaceViewModel : Observable
{
    public bool Enabled { get; private set; }
    public string CompactValue { get; private set; } = "—";
    public string Hint { get; private set; } = "";

    public void Update(bool enabled, AccountViewModel? active)
    {
        var window = active is { HasError: false, IsCached: false, Record.IsCurrent: true }
            ? active.FocusWindow : null;
        var value = window?.QuotaPaceText ?? "—";
        var title = L.F("当前活动账号 · 额度消耗速率", "Active account · quota consumption rate");
        var hint = window is null ? title + "\n" + L.F("等待当前账号的有效采样。", "Waiting for samples from the active account.")
            : $"{title}\n{active!.Name} · {window.Label} · {value}";
        if ((Enabled, CompactValue, Hint) == (enabled, value, hint)) return;
        (Enabled, CompactValue, Hint) = (enabled, value, hint);
        Raise("");
    }
}

public sealed class TokenRateViewModel : Observable
{
    internal static string Explanation => L.F("按本机会话日志统计最近 60 秒的平均输出 token/s。日志按批更新，不代表实时生成速度。",
        "Output tokens per second, averaged over the last 60 seconds of local Codex logs. Logs update in batches, not as tokens are generated.");
    public bool Enabled { get; private set; }
    public string Label { get; private set; } = "";
    public string Value { get; private set; } = "—";
    public string CompactValue { get; private set; } = "TPS —";
    public string Hint { get; private set; } = "";

    internal void Update(bool enabled, double? rate)
    {
        if (!enabled)
        {
            if (Enabled) { Enabled = false; Raise(nameof(Enabled)); }
            return;
        }
        var label = L.F("本机输出 TPS", "Local output TPS");
        var value = rate is { } measured ? L.F("{0:0.##} token/s", "{0:0.##} tokens/s", measured) : "—";
        var compactValue = rate is { } output ? $"TPS {output:0.##}" : "TPS —";
        var hint = rate is null ? Explanation + L.F(" 正在等待可用的用量样本。", " Waiting for usable usage samples.") : value + "\n" + Explanation;
        if (Enabled && Label == label && Value == value && Hint == hint) return;
        (Enabled, Label, Value, CompactValue, Hint) = (true, label, value, compactValue, hint);
        Raise("");
    }
}
