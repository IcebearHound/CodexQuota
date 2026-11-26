using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Quota.Core;

public sealed record ApiCreditReading(decimal RemainingUsd, decimal? GrantedUsd, DateOnly? NextExpiry, decimal? ExpiringUsd, DateOnly? LatestGrant = null)
{
    [JsonIgnore] public DateTimeOffset? ExpiryBoundary => NextExpiry is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)) : null;
    [JsonIgnore] public double? RemainingPercent => GrantedUsd is > 0 ? Math.Clamp((double)(100 * RemainingUsd / GrantedUsd.Value), 0, 100)
        : RemainingUsd == 0 ? 0 : null;
    [JsonIgnore] public decimal? SpentUsd => GrantedUsd is { } issued && issued >= RemainingUsd ? issued - RemainingUsd : null;

    public decimal? DisplayAllowance(decimal? cycleAllowanceUsd) => IsCycleAllowanceValid(cycleAllowanceUsd) ? cycleAllowanceUsd : GrantedUsd;

    public double? DisplayPercent(decimal? cycleAllowanceUsd, bool showUsed = false)
    {
        var amount = showUsed ? SpentUsd : RemainingUsd;
        if (amount is null) return null;
        return DisplayAllowance(cycleAllowanceUsd) is > 0 and { } allowance
            ? Math.Clamp((double)(amount.Value / allowance * 100), 0, 100)
            : amount == 0 ? 0 : null;
    }

    public static bool IsCycleAllowanceValid(decimal? amount) =>
        amount is > 0 and <= 1_000_000_000 && decimal.Round(amount.Value, 2) == amount;

    public UsageSnapshot ToSnapshot(string organizationId) => new()
    {
        AccountId = organizationId,
        Buckets = [new()
        {
            Name = "API", Plan = "API", Windows = [new()
            {
                Kind = "api-credits", ApiCredit = this, UsedPercent = 100 - (RemainingPercent ?? 100)
            }]
        }]
    };
}

public sealed record ApiBillingPage(string? OrganizationId, string? OrganizationName, string? Text, string[][]? GrantRows = null);

public static partial class ApiCredits
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private const string Usd = @"(?:US\$|\$)\s*(-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{2})?)";
    [GeneratedRegex("^" + Usd + "$", RegexOptions.CultureInvariant)] private static partial Regex MoneyPattern();
    [GeneratedRegex(@"^org-[A-Za-z0-9_-]{4,100}$", RegexOptions.CultureInvariant)] private static partial Regex OrganizationPattern();
    [GeneratedRegex(@"^下一笔到期额度\s*[:：]\s*" + Usd + @"\s*[，,]\s*到期日期为\s*(\d{4}年\d{1,2}月\d{1,2}日)", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseExpiry();
    [GeneratedRegex(@"^(?:Next expiration|Next expiring credits?)\s*:\s*" + Usd + @"\s*[,·]?\s*(?:expires?|expiring|on)(?:\s+on)?\s+([A-Za-z]+ \d{1,2}, \d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishExpiry();
    public static bool IsOrganizationId(string? value) => value is not null && OrganizationPattern().IsMatch(value);
    public static bool HasTrialBalance(string? text) => TrialBalance(text) is not null;

    public static ApiCreditReading? Read(ApiBillingPage overview, ApiBillingPage? grants = null)
    {
        if (!IsOrganizationId(overview.OrganizationId)) return null;
        if (TrialBalance(overview.Text) is not { } trial) return null;
        var (lines, heading, remaining) = trial;
        DateOnly? expiry = null;
        decimal? expiring = null;
        foreach (var line in lines.Skip(heading + 3).Take(3))
        {
            var match = ChineseExpiry().Match(line);
            if (!match.Success) match = EnglishExpiry().Match(line);
            if (match.Success && Date(match.Groups[2].Value) is { } date
                && ParseNumber(match.Groups[1].Value) is { } amount && amount > 0 && amount <= remaining)
                (expiry, expiring) = (date, amount);
        }

        decimal? total = null;
        DateOnly? latestGrant = null;
        if (grants is not null && (grants.OrganizationId is null || grants.OrganizationId == overview.OrganizationId)
            && string.Equals(grants.OrganizationName, overview.OrganizationName, StringComparison.Ordinal)
            && ReadRows(grants.GrantRows) is { Count: > 0 } rows && rows.Sum(row => row.Remaining) == remaining)
        {
            total = rows.Sum(row => row.Total);
            latestGrant = rows.Max(row => row.Received);
            var next = rows.Where(row => row.Remaining > 0 && row.Expiry is not null).MinBy(row => row.Expiry)?.Expiry;
            if (expiry is null && next is not null)
                (expiry, expiring) = (next, rows.Where(row => row.Expiry == next).Sum(row => row.Remaining));
        }
        return new(remaining, total, expiry, expiring, latestGrant);
    }

    private static (string[] Lines, int Heading, decimal Remaining)? TrialBalance(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var heading = Array.FindIndex(lines, line => line is "Free trial" or "免费试用");
        // The generic prepaid balance can mix paid and promotional credits.
        if (heading < 0 || lines.Length <= heading + 2 || lines[heading + 1] is not ("API credit balance" or "API 额度余额" or "API 信用额度余额")
            || Money(lines[heading + 2]) is not { } remaining || remaining < 0) return null;
        return (lines, heading, remaining);
    }

    private sealed record Grant(decimal Remaining, decimal Total, DateOnly? Expiry, DateOnly? Received);
    private static List<Grant>? ReadRows(string[][]? rows)
    {
        if (rows is not { Length: > 1 } ||
            !(rows[0].SequenceEqual(["Received", "State", "Balance", "Expires"]) ||
              rows[0].SequenceEqual(["领取日期", "状态", "余额", "到期日期"]))) return null;
        var grants = new List<Grant>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Length != 4) return null;
            if (row[1] is "Expired" or "已过期") continue;
            if (row[1] is not ("Available" or "Used" or "Used up" or "Consumed" or "可用" or "已用完")) return null;
            var pair = row[2].Split('/', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || Money(pair[0]) is not { } left || Money(pair[1]) is not { } total ||
                left < 0 || total <= 0 || left > total) return null;
            grants.Add(new(left, total, Date(row[3]), Date(row[0])));
        }
        return grants;
    }

    private static decimal? Money(string value) => MoneyPattern().Match(value) is { Success: true } match ? ParseNumber(match.Groups[1].Value) : null;
    private static decimal? ParseNumber(string value) => decimal.TryParse(value, NumberStyles.Number, Us, out var number) && Math.Abs(number) <= 1_000_000_000 ? number : null;
    private static DateOnly? Date(string value) => DateOnly.TryParseExact(value,
        ["MMM d, yyyy", "MMMM d, yyyy", "yyyy年M月d日", "yyyy-MM-dd"], Us, DateTimeStyles.None, out var date) ? date : null;
}
