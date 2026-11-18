using System.Windows;
using System.Windows.Controls;

namespace Quota.Desktop;

public partial class MainWindow
{
    public TokenRateViewModel TokenRate { get; } = new();

    internal void BuildMonitoringOptions(Panel parent, bool inGuide = false)
    {
        var section = SettingsControls.Section(parent, L.F("可选监控", "Optional metrics"), "activity",
            L.F("开启后显示在主面板和小组件。任务栏空间不足时，展开面板仍可查看。", "Enabled metrics appear in the dashboard and widgets. Open the taskbar panel if space on the taskbar is limited."));
        var tokenHint = TokenRateViewModel.Explanation + (inGuide ? L.F(" 例如：12.5 token/s。", " Example: 12.5 tokens/s.") : "");
        var quotaHint = L.F("按同一窗口连续两次成功刷新的差值计算，单位是百分点/小时。重置或采样不足时显示「—」。",
            "Percentage points per hour, based on two consecutive successful readings of the same window. A dash means a reset or insufficient data.")
            + (inGuide ? L.F(" 例如：下降 2 个百分点/小时。", " Example: down 2 percentage points per hour.") : "");
        SettingsControls.Toggle(section, L.F("本机输出速率（TPS）", "Local output rate (TPS)"), "activity", settings.ShowTokenRate,
            on => SetMonitoringOption(true, on, inGuide), tokenHint);
        SettingsControls.Toggle(section, L.F("额度下降速度", "Quota decline rate"), "minus", settings.ShowQuotaPace,
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
        if (settings.SimplifiedMode)
        {
            ResizeSimplifiedPanel();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (!exiting && settings.SimplifiedMode && WindowPlacementGuard.EnsureVisible(this)) SavePlacement();
            });
        }
        taskbarWidget?.QueueAlignment();
        SaveSettings();
        if (tokenRate && enabled && !demo) _ = ReadActivityAsync();
        if (fromGuide && settingsPage == "额度监测") QueueSettingsRender();
        else if (!fromGuide) guideWindow?.RefreshMonitoringOptions();
    }

    private void ApplyMonitoringOptions()
    {
        foreach (var group in visibleAccounts) group.ShowQuotaPace = settings.ShowQuotaPace;
        UpdateTokenRate();
    }

    private void UpdateTokenRate() => TokenRate.Update(settings.ShowTokenRate, activity.OutputTokensPerSecond);
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
        var hint = Explanation + (rate is null ? L.F(" 正在等待可用的用量样本。", " Waiting for usable usage samples.") : "");
        if (Enabled && Label == label && Value == value && Hint == hint) return;
        (Enabled, Label, Value, CompactValue, Hint) = (true, label, value, compactValue, hint);
        Raise("");
    }
}
