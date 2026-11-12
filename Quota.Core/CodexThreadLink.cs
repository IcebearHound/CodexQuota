namespace Quota.Core;

public static class CodexThreadLink
{
    public static string? NormalizeId(string? value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : null;

    public static Uri? Create(string? threadId)
        => NormalizeId(threadId) is { } id ? new Uri("codex://threads/" + id) : null;
}
