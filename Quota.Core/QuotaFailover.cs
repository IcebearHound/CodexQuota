namespace Quota.Core;

public static class QuotaFailover
{
    private static bool IsFresh(UsageSnapshot snapshot, DateTimeOffset now, TimeSpan maxAge)
        => snapshot.FetchedAt <= now && now - snapshot.FetchedAt <= maxAge;

    private static bool IsLive(QuotaWindow window, DateTimeOffset now)
        => double.IsFinite(window.UsedPercent) && window.UsedPercent >= 0
            && (window.ResetsAt is null || window.ResetsAt > now.ToUnixTimeSeconds());

    private static QuotaWindow[] IncludedWindows(UsageSnapshot snapshot) => snapshot.Buckets
        .Where(bucket => string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase))
        .SelectMany(bucket => bucket.Windows).ToArray();

    public static bool IsExhausted(UsageSnapshot snapshot, DateTimeOffset now, TimeSpan maxAge)
        => IsFresh(snapshot, now, maxAge) && IncludedWindows(snapshot).Any(window => IsLive(window, now) && window.UsedPercent >= 100);

    public static double? Available(UsageSnapshot current, UsageSnapshot alternative, DateTimeOffset now, TimeSpan maxAge)
    {
        if (!IsFresh(alternative, now, maxAge) || alternative.OrdinaryUsageAllowed == false) return null;
        // Multiple credential sources can represent one account. Require evidence of a different identity.
        var idsKnown = !string.IsNullOrWhiteSpace(current.AccountId) && !string.IsNullOrWhiteSpace(alternative.AccountId);
        var emailsKnown = !string.IsNullOrWhiteSpace(current.Email) && !string.IsNullOrWhiteSpace(alternative.Email);
        if ((!idsKnown && !emailsKnown) || (idsKnown && current.AccountId == alternative.AccountId)
            || (emailsKnown && string.Equals(current.Email!.Trim(), alternative.Email!.Trim(), StringComparison.OrdinalIgnoreCase))) return null;
        var windows = IncludedWindows(alternative);
        return windows.Length > 0 && windows.All(window => IsLive(window, now) && window.UsedPercent < 100)
            ? windows.Min(window => window.Remaining) : null;
    }
}
