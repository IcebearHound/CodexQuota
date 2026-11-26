namespace Quota.Core;

public static class QuotaPace
{
    /// <summary>Remaining-quota loss in percentage points per hour, by current window.</summary>
    public static Dictionary<QuotaWindow, double> Measure(UsageSnapshot? previous, UsageSnapshot current)
    {
        var rates = new Dictionary<QuotaWindow, double>();
        if (previous is null) return rates;
        foreach (var (old, window) in ComparableWindows(previous, current))
            rates[window] = (old.Remaining - window.Remaining) / (current.FetchedAt - previous.FetchedAt).TotalHours;
        return rates;
    }

    internal static IEnumerable<(QuotaWindow Previous, QuotaWindow Current)> ComparableWindows(UsageSnapshot previous, UsageSnapshot current)
    {
        if (current.FetchedAt <= previous.FetchedAt || !SameAccount(previous, current)) yield break;
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
                yield return (matches[0], window);
            }
        }
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

public readonly record struct QuotaPaceReading(double PercentagePointsPerHour, TimeSpan ObservedFor, DateTimeOffset SampledAt);

/// <summary>Tracks reported changes in the last hour of verified readings for the same account and quota window.</summary>
public sealed class QuotaPaceHistory
{
    public static TimeSpan Lookback { get; } = TimeSpan.FromHours(1);
    private UsageSnapshot? previous;
    private Dictionary<QuotaWindow, List<Sample>> history = [];
    private readonly record struct Sample(DateTimeOffset At, double Remaining);

    public IReadOnlyDictionary<QuotaWindow, QuotaPaceReading> Observe(UsageSnapshot current)
    {
        var rates = new Dictionary<QuotaWindow, QuotaPaceReading>();
        var nextHistory = new Dictionary<QuotaWindow, List<Sample>>();
        if (previous is { } prior && current.FetchedAt - prior.FetchedAt <= Lookback)
        {
            foreach (var (old, window) in QuotaPace.ComparableWindows(prior, current))
            {
                var samples = history.GetValueOrDefault(old) ?? [new(prior.FetchedAt, old.Remaining)];
                samples.Add(new(current.FetchedAt, window.Remaining));
                // Keep the last baseline at or before the cutoff. Count integer changes at the reading that reported them.
                var firstRecent = samples.FindIndex(sample => current.FetchedAt - sample.At < Lookback);
                if (firstRecent > 1) samples.RemoveRange(0, firstRecent - 1);
                var span = current.FetchedAt - samples[0].At;
                if (span > Lookback) span = Lookback;
                rates[window] = new((samples[0].Remaining - window.Remaining) / span.TotalHours, span, current.FetchedAt);
                nextHistory[window] = samples;
            }
        }
        previous = current;
        history = nextHistory;
        return rates;
    }
}
