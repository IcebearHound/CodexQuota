namespace Quota.Core;

public static class QuotaPace
{
    /// <summary>Remaining-quota loss in percentage points per hour, by current window.</summary>
    public static Dictionary<QuotaWindow, double> Measure(UsageSnapshot? previous, UsageSnapshot current)
    {
        var rates = new Dictionary<QuotaWindow, double>();
        if (previous is null || current.FetchedAt <= previous.FetchedAt || !SameAccount(previous, current)) return rates;
        var hours = (current.FetchedAt - previous.FetchedAt).TotalHours;
        var now = current.FetchedAt.ToUnixTimeSeconds();
        foreach (var bucket in current.Buckets)
        {
            // Match the server's limit ID, not a display name that can be shared by several buckets.
            var candidates = previous.Buckets.Where(old => BucketKey(old) == BucketKey(bucket) && old.Plan == bucket.Plan).Take(2).ToArray();
            if (candidates.Length != 1) continue;
            foreach (var window in bucket.Windows)
            {
                if (window.ResetsAt is not (> 0 and < 253402300800) || window.ResetsAt <= now) continue;
                var matches = candidates[0].Windows.Where(old => old.Kind == window.Kind && old.DurationMinutes == window.DurationMinutes && old.ResetsAt == window.ResetsAt).Take(2).ToArray();
                if (matches.Length != 1 || !double.IsFinite(window.UsedPercent) || !double.IsFinite(matches[0].UsedPercent)
                    || window.UsedPercent < matches[0].UsedPercent) continue;
                rates[window] = (matches[0].Remaining - window.Remaining) / hours;
            }
        }
        return rates;
    }

    private static string BucketKey(QuotaBucket bucket) => bucket.LimitId ?? bucket.Name;

    private static bool SameAccount(UsageSnapshot previous, UsageSnapshot current)
    {
        var leftId = previous.AccountId?.Trim();
        var rightId = current.AccountId?.Trim();
        var leftEmail = previous.Email?.Trim();
        var rightEmail = current.Email?.Trim();
        if (!string.Equals(leftId, rightId, StringComparison.Ordinal) || !string.Equals(leftEmail, rightEmail, StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.IsNullOrEmpty(leftId) || !string.IsNullOrEmpty(leftEmail);
    }
}
