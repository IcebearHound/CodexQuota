using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Quota.Desktop;

public sealed class AccountGroupViewModel : Observable, IDisposable
{
    private readonly AccountViewModel[] sources;
    private AccountViewModel[] selected = [];
    private AccountViewModel primary;
    private bool disposed;
    private bool showUsed;
    public bool ShowUsed
    {
        get => showUsed;
        set { showUsed = value; foreach (var bucket in Buckets) foreach (var window in bucket.Windows) window.ShowUsed = value; Raise(nameof(FocusWindow)); Raise(nameof(FocusCaption)); Raise(nameof(MetricTooltip)); Raise(nameof(HasLowQuota)); }
    }
    public WindowViewModel? FocusWindow => Buckets.SelectMany(bucket => bucket.Windows).OrderBy(window => window.Remaining).FirstOrDefault();
    public string FocusCaption => FocusWindow is { } window ? window.Caption + " · " + window.Label : "";
    public string ResetCardSummary => selected.Length > 1 ? string.Join(Environment.NewLine, selected.Select((source, index) => L.F("工作区 {0} · {1}", "Workspace {0} · {1}", index + 1, source.ResetCardSummary))) : primary.ResetCardSummary;
    public bool HasMetric => FocusWindow is not null;
    public string MetricTooltip => Identity + Environment.NewLine + SourceSummary + Environment.NewLine + ResetCardSummary + Environment.NewLine + string.Join(Environment.NewLine, Buckets.SelectMany(bucket => bucket.Windows.Select(window => $"{bucket.Name} · {window.Label} · {window.Caption} {window.Percent} · {window.ResetText}"))) + (Error is { } error ? Environment.NewLine + error : "");

    public string Key { get; }
    public IReadOnlyList<AccountViewModel> Sources => sources;
    public ObservableCollection<BucketViewModel> Buckets { get; } = [];
    public string Name => sources.FirstOrDefault(source => source.Record.CustomName)?.Name ?? sources.FirstOrDefault(source => source.Managed)?.Name ?? primary.Name;
    public string EditableName => sources.FirstOrDefault(source => source.Record.CustomName)?.Record.Name ?? sources.FirstOrDefault(source => source.Managed)?.Record.Name ?? primary.Record.Name;
    public string Initial => Name.Length > 0 ? StringInfo.GetNextTextElement(Name).ToUpperInvariant() : "C";
    public bool ShowDetails => primary.ShowDetails;
    public bool ShowCompact => primary.ShowCompact;
    public string Identity => primary.Identity;
    public bool HasMultipleSources => sources.Length > 1;
    public bool IsCurrent => sources.Any(source => source.Record.IsCurrent && !source.HasError && source.Snapshot is not null);
    public bool CanSwitch => !IsCurrent && sources.Any(source => source.Managed);
    public string ActionHint => HasError ? sources.FirstOrDefault(source => source.HasError)?.ActionHint ?? primary.ActionHint : L.T("已同步");
    public string SourceSummary
    {
        get
        {
            var labels = sources.GroupBy(SourceName).Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key);
            var failed = sources.Count(source => source.HasError);
            return string.Join(" + ", labels) + (failed > 0 ? L.F(" · {0} 个来源需处理", " · {0} sources need attention", failed) : "");
        }
    }
    public string SourceTooltip => string.Join(Environment.NewLine, sources.Select(source => $"{SourceName(source)} · {source.Name} · {source.Status}"));
    public string Plan
    {
        get
        {
            var plans = selected.Select(source => source.Plan).Where(plan => plan != "—").Distinct().ToArray();
            return plans.Length > 1 ? L.T("多个套餐") : plans.FirstOrDefault() ?? primary.Plan;
        }
    }
    public bool IsRefreshing => sources.Any(source => source.IsRefreshing);
    public bool HasError => selected.Length > 0 ? selected.Any(source => source.HasError) : sources.All(source => source.HasError);
    public string? Error => HasError ? string.Join(" ", (selected.Length > 0 ? selected : sources).Where(source => source.HasError).Select(source => source.Error).Distinct()) : null;
    public bool NoSnapshot => Buckets.Count == 0;
    public bool Blocked => selected.Any(source => source.Blocked);
    public string BlockMessage => selected.Length > 1 ? L.T("服务端提示：部分工作区包含用量暂不可用。") : L.T("服务端提示：当前包含用量暂不可用。");
    public bool HasLowQuota => Buckets.SelectMany(bucket => bucket.Windows).Any(window => window.Remaining <= WindowViewModel.AlertThreshold);
    public string Updated => primary.Updated;
    public string UpdatedTooltip => string.Join(Environment.NewLine, selected.Select(source => $"{SourceName(source)} · {source.UpdatedTooltip}"));
    public string StatusColor => IsRefreshing ? "#84E1BF" : HasError ? "#F0AAA8" : "#A3B5BB";
    public string Status
    {
        get
        {
            if (IsRefreshing) return L.T("更新中");
            if (HasError) return selected.Length > 1 ? L.T("部分过期") : primary.Status;
            if (selected.Length == 0) return primary.Status;
            return selected.Any(source => source.Status == L.T("缓存")) ? L.T("缓存") : IsCurrent ? L.T("使用中") : L.T("已同步");
        }
    }

    public AccountGroupViewModel(string key, IEnumerable<AccountViewModel> sources)
    {
        Key = key;
        this.sources = sources.ToArray();
        if (this.sources.Length == 0) throw new ArgumentException("An account group requires at least one source.", nameof(sources));
        primary = this.sources[0];
        foreach (var source in this.sources) source.PropertyChanged += SourceChanged;
        Refresh();
    }

    public static string KeyFor(AccountViewModel source)
    {
        var email = source.Snapshot?.Email;
        if (string.IsNullOrWhiteSpace(email) && !source.Record.IsCurrent) email = source.Record.Email;
        return string.IsNullOrWhiteSpace(email) ? "source/" + source.Record.Id : "email/" + email.Trim().ToUpperInvariant();
    }

    public static List<AccountGroupViewModel> Build(IEnumerable<AccountViewModel> accounts)
        => accounts.GroupBy(KeyFor, StringComparer.Ordinal).Select(group => new AccountGroupViewModel(group.Key, group)).ToList();

    public static string SourceName(AccountViewModel source)
        => source.Record.IsCurrent ? L.T("本机 Codex") : source.Record.ImportKey is not null ? "CC Switch" : L.T("浏览器登录");

    private void SourceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (disposed) return;
        if (args.PropertyName == nameof(AccountViewModel.Updated))
        {
            foreach (var bucket in Buckets) bucket.UpdateClock();
            Raise(nameof(Updated)); Raise(nameof(Status)); Raise(nameof(UpdatedTooltip)); Raise(nameof(MetricTooltip));
            return;
        }
        if (args.PropertyName is nameof(AccountViewModel.Identity) or nameof(AccountViewModel.Status) or nameof(AccountViewModel.Name) or nameof(AccountViewModel.ShowDetails)) Refresh();
    }

    private void Refresh()
    {
        selected = sources.Where(source => source.Snapshot is not null)
            .GroupBy(source => string.IsNullOrWhiteSpace(source.Snapshot!.AccountId) ? "source/" + source.Record.Id : "workspace/" + source.Snapshot.AccountId)
            .Select(group => Best(group)).ToArray();
        primary = Best(selected.Length > 0 ? selected : sources);
        Buckets.Clear();
        for (var index = 0; index < selected.Length; index++)
        {
            foreach (var bucket in selected[index].Snapshot!.Buckets)
            {
                var label = selected.Length > 1 ? L.F("{0} · 工作区 {1}", "{0} · workspace {1}", bucket.Name, index + 1) : null;
                Buckets.Add(new BucketViewModel(bucket, label));
            }
        }
        foreach (var bucket in Buckets) foreach (var window in bucket.Windows) window.ShowUsed = showUsed;
        foreach (var property in new[]
        {
            nameof(Name), nameof(Initial), nameof(ShowDetails), nameof(ShowCompact), nameof(Identity),
            nameof(SourceSummary), nameof(SourceTooltip), nameof(Plan), nameof(IsRefreshing), nameof(HasError),
            nameof(Error), nameof(NoSnapshot), nameof(Blocked), nameof(BlockMessage), nameof(HasLowQuota), nameof(IsCurrent), nameof(CanSwitch),
            nameof(Updated), nameof(UpdatedTooltip), nameof(StatusColor), nameof(Status), nameof(FocusWindow), nameof(FocusCaption), nameof(HasMetric), nameof(MetricTooltip), nameof(ActionHint), nameof(ResetCardSummary)
        }) Raise(property);
    }

    private static AccountViewModel Best(IEnumerable<AccountViewModel> candidates)
        => candidates.OrderBy(source => source.HasError).ThenBy(source => source.IsCached)
            .ThenByDescending(source => source.Snapshot?.FetchedAt ?? DateTimeOffset.MinValue).First();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var source in sources) source.PropertyChanged -= SourceChanged;
    }
}
