using System.Text.Json;

namespace Quota.Core;

internal sealed class CodexOutputRate(bool canMeasureFirstUsage)
{
    private static readonly TimeSpan Lookback = TimeSpan.FromMinutes(1);
    private readonly Queue<(DateTimeOffset Time, long Count)> increments = new();
    private long? total;
    private DateTimeOffset lastTime;
    private bool measured;

    public void Observe(JsonElement record, DateTimeOffset time, DateTimeOffset now, DateTimeOffset started)
    {
        if (time < lastTime || time > now || record.Str("type") != "event_msg"
            || !record.TryGetProperty("payload", out var payload) || payload.Str("type") != "token_count"
            || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty("total_token_usage", out var usage) || usage.Long("output_tokens") is not (>= 0 and var count)) return;

        var baseline = total;
        if (baseline is null && canMeasureFirstUsage && info.TryGetProperty("last_token_usage", out var last)
            && last.Long("output_tokens") == count)
            baseline = 0;
        if (baseline is { } previous && count != previous)
        {
            // Rate-limit-only events can repeat the same cumulative usage. A counter rollback starts a new baseline.
            if (count < previous) { increments.Clear(); measured = false; }
            else if (time >= started && time > now - Lookback)
            {
                Trim(now);
                increments.Enqueue((time, count - previous));
                measured = true;
            }
        }
        total = count;
        lastTime = time;
    }

    public double? At(DateTimeOffset now)
    {
        Trim(now);
        return measured ? increments.Sum(sample => (double)sample.Count) / Lookback.TotalSeconds : null;
    }

    private void Trim(DateTimeOffset now)
    {
        while (increments.TryPeek(out var sample) && sample.Time <= now - Lookback) increments.Dequeue();
    }
}
