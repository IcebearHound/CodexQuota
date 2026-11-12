using System.Text.Json;

namespace Quota.Core;

public sealed record QuotaAlert(string AccountKey, string Kind, double Value = 0, int Count = 1);
public sealed record AlertWindow(string AccountKey, string WindowKey, double Remaining, long? ResetsAt, string? Period = null);
public sealed record ResetCreditDeadline(string AccountKey, string CreditKey, DateTimeOffset ExpiresAt);
public sealed class AlertState
{
    public Dictionary<string, AlertWindow> Windows { get; set; } = [];
    public HashSet<string> ExpirySent { get; set; } = [];
    public HashSet<string> LowSent { get; set; } = [];
    public HashSet<string> ExhaustedSent { get; set; } = [];
}
public sealed class QuotaAlerts
{
    private readonly AccountStore store;
    private readonly object gate = new();
    private AlertState state;
    private string? savedState;
    public QuotaAlerts(string directory)
    {
        store = new AccountStore(directory); state = store.LoadAlertState() ?? new();
        state.Windows ??= []; state.ExpirySent ??= []; state.LowSent ??= []; state.ExhaustedSent ??= [];
        if (store.RecoveryNotice is null) savedState = JsonSerializer.Serialize(state);
    }
    public List<QuotaAlert> Evaluate(IEnumerable<AlertWindow> windows, WidgetSettings settings, DateTimeOffset now, IEnumerable<ResetCreditDeadline>? resetCards = null)
    {
        lock (gate)
        {
            var next = new AlertState
            {
                Windows = new(state.Windows), ExpirySent = new(state.ExpirySent),
                LowSent = new(state.LowSent), ExhaustedSent = new(state.ExhaustedSent)
            };
            var alerts = new List<QuotaAlert>();
            foreach (var window in windows.Where(window => double.IsFinite(window.Remaining)))
            {
                var key = window.AccountKey + "/" + window.WindowKey;
                next.Windows.TryGetValue(key, out var previous);
                // Legacy records establish a baseline when server window identity becomes available.
                if (previous is not null && window.Period is not null && previous.Period != window.Period)
                {
                    if (previous.Period is not null || previous.ResetsAt != window.ResetsAt)
                    {
                        next.LowSent.Remove(key); next.ExhaustedSent.Remove(key);
                    }
                    previous = null;
                }
                var threshold = double.IsFinite(settings.LowQuotaThreshold) ? Math.Clamp(settings.LowQuotaThreshold, 1, 50) : 5;
                var reset = previous?.ResetsAt is { } oldReset && oldReset <= now.ToUnixTimeSeconds() && window.ResetsAt > oldReset;
                if (reset || window.Remaining > threshold) next.LowSent.Remove(key);
                if (reset || window.Remaining > 0) next.ExhaustedSent.Remove(key);
                if (settings.NotifyExhausted && window.Remaining <= 0 && next.ExhaustedSent.Add(key)) alerts.Add(new(window.AccountKey, "exhausted"));
                else if (settings.NotifyLowQuota && window.Remaining > 0 && window.Remaining <= threshold && next.LowSent.Add(key)) alerts.Add(new(window.AccountKey, "low", window.Remaining));
                if (settings.NotifyReset && reset) alerts.Add(new(window.AccountKey, "reset"));
                next.Windows[key] = window;
            }
            if (settings.NotifyCardExpiry)
            {
                var days = settings.CardReminderDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.TryParse(value.Trim(), out var day) ? day : -1).Where(day => day >= 0 && day <= 365).Append(0).Distinct().Order().ToArray();
                var deadlines = resetCards ?? settings.CardExpirations.Select(expiry => new ResetCreditDeadline(expiry.Key, "manual", expiry.Value));
                foreach (var expiry in deadlines.DistinctBy(expiry => (expiry.AccountKey, expiry.CreditKey)))
                {
                    var remaining = (expiry.ExpiresAt - now).TotalDays;
                    var crossed = days.Where(day => remaining <= day).Select(day => (int?)day).FirstOrDefault();
                    if (crossed is null) continue;
                    var creditKey = expiry.AccountKey + "/" + expiry.CreditKey + "/" + expiry.ExpiresAt.ToUnixTimeSeconds();
                    var expiryKey = creditKey + "/" + crossed;
                    if (!next.ExpirySent.Add(expiryKey)) continue;
                    // Mark wider thresholds as handled so the same deadline does not notify again after a clock change.
                    foreach (var day in days.Where(day => day >= crossed)) next.ExpirySent.Add(creditKey + "/" + day);
                    alerts.Add(new(expiry.AccountKey, remaining <= 0 ? "expired" : "expiry", Math.Max(0, Math.Ceiling(remaining))));
                }
            }
            var serialized = JsonSerializer.Serialize(next);
            if (serialized != savedState) store.SaveAlertState(next);
            state = next; savedState = serialized;
            return alerts.GroupBy(alert => (alert.AccountKey, alert.Kind)).Select(group => group.OrderBy(alert => alert.Value).First() with { Count = group.Key.Kind is "expiry" or "expired" ? group.Count() : 1 }).ToList();
        }
    }
}
