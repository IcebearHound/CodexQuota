using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using Quota.Core;

namespace Quota.Desktop;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class BoolVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo culture) => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo culture) => value is Visibility.Visible;
}
public sealed class WindowViewModel(QuotaWindow window) : Observable
{
    public static double AlertThreshold { get; set; } = 5;
    private bool showUsed;
    public bool ShowUsed
    {
        get => showUsed;
        set { showUsed = value; Raise(nameof(Value)); Raise(nameof(Percent)); Raise(nameof(Caption)); Raise(nameof(Color)); }
    }
    public string Label => !L.IsEnglish ? window.Label : window.DurationMinutes == 10080 ? "Weekly" : window.DurationMinutes is > 0 ? L.F(L.T("{0} 小时"), "{0:0.#} hours", window.DurationMinutes.Value / 60d) : "Usage window";
    public double Remaining => window.Remaining;
    public double Value => ShowUsed ? Math.Clamp(window.UsedPercent, 0, 100) : Remaining;
    public string Caption => ShowUsed ? L.T("已用") : L.T("剩余");
    public string Percent => $"{Value:0}%";
    public string Color => Remaining <= AlertThreshold ? "#DB6268" : Remaining <= 20 ? "#CDA862" : "#64C9B0";
    public string ResetText
    {
        get
        {
            if (window.ResetsAt is not { } epoch) return L.T("重置时间未提供");
            DateTimeOffset reset; try { reset = DateTimeOffset.FromUnixTimeSeconds(epoch); } catch (ArgumentOutOfRangeException) { return L.T("重置时间未提供"); }
            var delta = reset - DateTimeOffset.UtcNow;
            if (delta <= TimeSpan.Zero) return L.T("已到重置时间 · 等待刷新");
            if (L.IsEnglish) return delta.TotalDays >= 1 ? $"Resets in {(int)delta.TotalDays}d {delta.Hours}h" : delta.TotalHours >= 1 ? $"Resets in {(int)delta.TotalHours}h {delta.Minutes}m" : $"Resets in {Math.Max(1, (int)delta.TotalMinutes)}m";
            var span = delta.TotalDays >= 1 ? $"{(int)delta.TotalDays}天 {delta.Hours}小时" : delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}小时 {delta.Minutes}分" : $"{Math.Max(1, (int)delta.TotalMinutes)}分钟";
            return $"{span}后重置";
        }
    }
    public string ResetTooltip => window.ResetsAt is { } epoch && epoch >= 0 && epoch < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") : L.T("以服务端返回时间为准");
    public string CompactResetText
    {
        get
        {
            if (window.ResetsAt is not { } epoch || epoch < 0 || epoch >= 253402300799) return "—";
            var span = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
            if (span <= TimeSpan.Zero) return "0m";
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d{span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h{span.Minutes}m";
            return $"{Math.Max(1, (int)span.TotalMinutes)}m";
        }
    }
    public void UpdateClock() { Raise(nameof(ResetText)); Raise(nameof(CompactResetText)); }
}
public sealed class BucketViewModel(QuotaBucket bucket, string? displayName = null)
{
    public string Name => displayName ?? (bucket.Name.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "CODEX" : bucket.Name);
    public ObservableCollection<WindowViewModel> Windows { get; } = new(bucket.Windows.Select(x => new WindowViewModel(x)));
    public bool NoWindows => Windows.Count == 0;
    public bool HasCredits => bucket.UnlimitedCredits || bucket.CreditBalance is not null;
    public string Credits => bucket.UnlimitedCredits ? L.T("额外额度：不限量") : L.T("额外额度余额：") + bucket.CreditBalance;
    public void UpdateClock() { foreach (var window in Windows) window.UpdateClock(); }
}
public sealed class AccountViewModel(AccountRecord record) : Observable
{
    public AccountRecord Record { get; } = record;
    public UsageSnapshot? Snapshot { get; private set; }
    public string Name => privacy && Record.Name.Contains('@') ? MaskEmail(Record.Name) : Record.Name;
    public static string MaskEmail(string name)
    {
        var separator = name.IndexOf('@');
        return separator > 0 ? StringInfo.GetNextTextElement(name) + "•••" + name[separator..] : name;
    }
    public bool Managed => !Record.IsCurrent;
    public string ResetCardSummary
    {
        get
        {
            if (Snapshot?.ResetCreditAvailableCount is not { } count) return L.T("重置卡信息未提供");
            if (count == 0) return L.T("暂无可用重置卡");
            var next = Snapshot.ResetCredits.Where(credit => credit.ExpiryKnown && credit.ExpiresAt is not null).OrderBy(credit => credit.ExpiresAt).FirstOrDefault();
            if (next?.ExpiresAt is { } expiry) return L.F("重置卡 {0} 张 · 最早 {1:MM-dd HH:mm} 到期", "{0} reset cards · earliest expiry {1:MMM d HH:mm}", count, DateTimeOffset.FromUnixTimeSeconds(expiry).ToLocalTime());
            if (Snapshot.ResetCreditDetailsKnown && Snapshot.ResetCredits.Count >= count && Snapshot.ResetCredits.All(credit => credit.ExpiryKnown && credit.ExpiresAt is null)) return L.F("重置卡 {0} 张 · 不过期", "{0} reset cards · no expiry", count);
            return L.F("重置卡 {0} 张 · 到期明细暂不可用", "{0} reset cards · expiry details unavailable", count);
        }
    }
    public string Initial => Name.Length > 0 ? StringInfo.GetNextTextElement(Name).ToUpperInvariant() : "C";
    private bool privacy = true, compact, refreshing, loadedFromCache;
    private string? error;
    public bool Privacy { set { privacy = value; Raise(nameof(Name)); Raise(nameof(Identity)); } }
    public bool Compact { set { compact = value; Raise(nameof(ShowDetails)); Raise(nameof(ShowCompact)); } }
    public bool ShowDetails => !compact;
    public bool ShowCompact => compact;
    public bool IsCached => loadedFromCache;
    public string Identity
    {
        get
        {
            if (Record.IsCurrent && error is not null && Snapshot is not null)
                return privacy || string.IsNullOrEmpty(Snapshot.Email) ? L.T("上次读取账号 · 当前登录未验证") : L.F("上次账号 {0} · 当前登录未验证", "Previous account {0} · current sign-in unverified", Snapshot.Email);
            if (Record.IsCurrent) return privacy ? L.T("跟随本机 Codex 登录") : Snapshot?.Email ?? Record.Email ?? L.T("跟随本机 Codex 登录");
            return privacy ? L.T("独立登录 · 邮箱已隐藏") : Snapshot?.Email ?? Record.Email ?? L.T("独立登录账号");
        }
    }
    public string Plan
    {
        get
        {
            var plan = Snapshot?.Buckets.Select(x => x.Plan).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim().ToLowerInvariant();
            if (plan is null or "unknown") return "—";
            if (plan is "prolite" or "pro100") return "Pro100";
            if (plan is "pro" or "pro200") return "Pro200";
            if (plan is "promax" or "pro500") return "Pro500";
            if (plan.Contains("business", StringComparison.OrdinalIgnoreCase) || plan == "team") return "BUSINESS";
            if (plan.Contains("enterprise", StringComparison.OrdinalIgnoreCase) || plan == "ent26") return "ENTERPRISE";
            return plan.ToUpperInvariant().Replace('_', ' ');
        }
    }
    public bool IsRefreshing { get => refreshing; set { refreshing = value; Raise(); Raise(nameof(Status)); Raise(nameof(StatusColor)); } }
    public string StatusColor => IsRefreshing ? "#84E1BF" : error is not null ? "#F0AAA8" : "#A3B5BB";
    public string Status => IsRefreshing ? L.T("更新中") : error is not null ? (Snapshot is null ? L.T("需处理") : L.T("已过期")) : Snapshot is null ? L.T("等待更新") : loadedFromCache || (DateTimeOffset.UtcNow - Snapshot.FetchedAt).TotalMinutes > 15 ? L.T("缓存") : L.T("已同步");
    public bool HasError => error is not null;
    public string? Error => error is null ? null : (Record.IsCurrent && Snapshot is not null ? L.T("当前登录未验证，以下为上次读取账号的额度。 ") : "") + L.Error(error);
    public string ActionHint => error is null ? L.T("已同步") : error.Contains("Codex") && error.Contains("找到") ? L.T("打开设置选择 Codex") : error.Contains("登录") || error.Contains("认证") || error.Contains("目录已丢失") ? L.T("重新登录以恢复认证") : error.Contains("网络") || error.Contains("连接") || error.Contains("超时") ? L.T("检查网络后刷新") : L.T("刷新或重新登录");
    public bool NoSnapshot => Snapshot is null || Snapshot.Buckets.Count == 0;
    public bool Blocked => Snapshot?.OrdinaryUsageAllowed == false;
    public ObservableCollection<BucketViewModel> Buckets { get; } = [];
    public string Updated
    {
        get
        {
            if (Snapshot is null) return L.T("尚无额度数据");
            var local = Snapshot.FetchedAt.ToLocalTime(); var now = DateTimeOffset.Now;
            var format = local.Year != now.Year ? "yyyy-MM-dd HH:mm" : local.Date != now.Date ? "MM-dd HH:mm" : "HH:mm";
            return L.T("更新于 ") + local.ToString(format) + (error is not null ? L.T(" · 显示上次数据") : loadedFromCache ? L.T(" · 缓存") : "");
        }
    }
    public string UpdatedTooltip => Snapshot is null ? L.T("尚无成功读取记录") : L.F("最后成功读取 {0:yyyy-MM-dd HH:mm:ss zzz}", "Last successful update {0:yyyy-MM-dd HH:mm:ss zzz}", Snapshot.FetchedAt.ToLocalTime());
    public void SetSnapshot(UsageSnapshot snapshot, bool fromCache = false)
    {
        Snapshot = snapshot;
        loadedFromCache = fromCache;
        error = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Email)) Record.Email = snapshot.Email;
        RefreshDerived();
    }
    public void SetError(string message) { error = message; RefreshDerived(); }
    public void ClearSnapshot() { Snapshot = null; loadedFromCache = false; error = null; RefreshDerived(); }
    public void Rename(string name) { Record.Name = AccountStore.CleanName(name); Raise(nameof(Name)); Raise(nameof(Initial)); }
    public void RefreshDerived()
    {
        Buckets.Clear(); if (Snapshot is not null) foreach (var b in Snapshot.Buckets) Buckets.Add(new(b));
        foreach (var name in new[] { nameof(Identity), nameof(Plan), nameof(Status), nameof(StatusColor), nameof(HasError), nameof(Error), nameof(NoSnapshot), nameof(Blocked), nameof(Updated), nameof(UpdatedTooltip), nameof(ActionHint), nameof(ResetCardSummary) }) Raise(name);
    }
    public void UpdateClock() { foreach (var bucket in Buckets) bucket.UpdateClock(); Raise(nameof(Status)); Raise(nameof(Updated)); }
}
