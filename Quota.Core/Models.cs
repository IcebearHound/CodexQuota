using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quota.Core;

public sealed class AccountRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新账号";
    public string? Email { get; set; }
    public string? ImportKey { get; set; }
    public bool CustomName { get; set; }
    public bool IsCurrent => Id == "current";
}
public sealed class QuotaWindow
{
    public string? Kind { get; set; }
    public double UsedPercent { get; set; }
    public int? DurationMinutes { get; set; }
    public long? ResetsAt { get; set; }
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
    public string Label => DurationMinutes switch
    {
        null or <= 0 => "用量周期",
        10080 => "每周",
        var minutes when minutes % 1440 == 0 => $"{minutes / 1440} 天",
        var minutes when minutes % 60 == 0 => $"{minutes / 60} 小时",
        >= 60 => $"{DurationMinutes / 60} 小时 {DurationMinutes % 60} 分钟",
        _ => $"{DurationMinutes} 分钟"
    };
}
public sealed class QuotaBucket
{
    public string Name { get; set; } = "Codex";
    public string? Plan { get; set; }
    public List<QuotaWindow> Windows { get; set; } = [];
    public string? CreditBalance { get; set; }
    public bool UnlimitedCredits { get; set; }
}
public sealed class ResetCredit
{
    public string Id { get; set; } = "";
    public long? ExpiresAt { get; set; }
    public bool ExpiryKnown { get; set; }
}
public sealed class UsageSnapshot
{
    [JsonRequired] public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Email { get; set; }
    public string? AccountId { get; set; }
    public bool? OrdinaryUsageAllowed { get; set; }
    public List<QuotaBucket> Buckets { get; set; } = [];
    public int? ResetCreditAvailableCount { get; set; }
    public bool ResetCreditDetailsKnown { get; set; }
    public List<ResetCredit> ResetCredits { get; set; } = [];
    public bool HasLowQuota => Buckets.SelectMany(x => x.Windows).Any(x => x.Remaining <= 20);
    public static UsageSnapshot Parse(JsonElement usage, JsonElement account)
    {
        if (usage.ValueKind != JsonValueKind.Object || account.ValueKind != JsonValueKind.Object) throw new JsonException("额度响应格式无效。");
        var result = new UsageSnapshot { AccountId = usage.Str("accountId"), OrdinaryUsageAllowed = usage.Bool("ordinaryUsageAllowed") };
        string? accountPlan = null;
        if (account.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object) { result.Email = a.Str("email"); accountPlan = a.Str("planType"); }
        if (usage.TryGetProperty("rateLimitsByLimitId", out var many) && many.ValueKind == JsonValueKind.Object)
            foreach (var p in many.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Object) result.Buckets.Add(ParseBucket(p.Value, p.Name));
        if (result.Buckets.Count == 0 && usage.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            result.Buckets.Add(ParseBucket(single, "Codex"));
        foreach (var bucket in result.Buckets) bucket.Plan ??= accountPlan;
        if (usage.TryGetProperty("rateLimitResetCredits", out var resetCredits) && resetCredits.ValueKind == JsonValueKind.Object)
        {
            var count = resetCredits.Int("availableCount");
            result.ResetCreditAvailableCount = count is >= 0 and <= 1_000_000 ? count : null;
            if (resetCredits.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Array)
            {
                result.ResetCreditDetailsKnown = true;
                foreach (var credit in credits.EnumerateArray())
                {
                    if (credit.Str("status") != "available" || credit.Str("id") is not { Length: > 0 } id) continue;
                    var expiry = credit.Long("expiresAt");
                    var valid = expiry is >= 0 and < 253402300799;
                    var unlimited = credit.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Null;
                    result.ResetCredits.Add(new() { Id = id, ExpiresAt = valid ? expiry : null, ExpiryKnown = valid || unlimited });
                }
                result.ResetCredits = result.ResetCredits.DistinctBy(credit => credit.Id).ToList();
            }
        }
        return result;
    }
    private static QuotaBucket ParseBucket(JsonElement data, string key)
    {
        var bucket = new QuotaBucket { Name = data.Str("limitName") ?? key, Plan = data.Str("planType") };
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!data.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("usedPercent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0) continue;
            bucket.Windows.Add(new QuotaWindow { Kind = name, UsedPercent = used, DurationMinutes = w.Int("windowDurationMins"), ResetsAt = w.Long("resetsAt") });
        }
        if (data.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object) { bucket.CreditBalance = credits.Str("balance"); bucket.UnlimitedCredits = credits.Bool("unlimited") == true; }
        return bucket;
    }
}
public static class JsonFields
{
    public static string? Str(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool? Bool(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    public static int? Int(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    public static long? Long(this JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
public sealed class WidgetSettings
{
    public bool Topmost { get; set; } = true;
    public bool Privacy { get; set; } = true;
    public bool Compact { get; set; } = true;
    public bool SimplifiedMode { get; set; }
    public string SimplifiedLayout { get; set; } = "ring";
    public int LayoutVersion { get; set; }
    public string Theme { get; set; } = "system";
    public string Palette { get; set; } = "mint";
    public string Material { get; set; } = "solid";
    public string Language { get; set; } = "zh-CN";
    public bool CcSwitchPromptShown { get; set; }
    public int GuideVersion { get; set; }
    public string TaskbarPlacement { get; set; } = "auto";
    public string? CurrentAccountName { get; set; }
    public bool ShowUsed { get; set; }
    public bool DesktopOnly { get; set; }
    public bool NotifyLowQuota { get; set; } = true;
    public double LowQuotaThreshold { get; set; } = 5;
    public bool NotifyExhausted { get; set; } = true;
    public bool NotifyReset { get; set; } = true;
    public bool NotifyCardExpiry { get; set; } = true;
    public string CardReminderDays { get; set; } = "3,1";
    public Dictionary<string, DateTimeOffset> CardExpirations { get; set; } = [];
    // Retained only to read older settings. The independent desktop widget was removed.
    public bool DesktopWidgetEnabled { get; set; }
    public bool TaskbarWidgetEnabled { get; set; } = true;
    public bool DetailedTaskbar { get; set; }
    public int TaskbarVersion { get; set; }
    public double TaskbarOffset { get; set; } = 8;
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public int? WidgetPixelLeft { get; set; }
    public int? WidgetPixelTop { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;
    public string? CodexPath { get; set; }
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 464;
    public double? Left { get; set; }
    public double? Top { get; set; }
}
