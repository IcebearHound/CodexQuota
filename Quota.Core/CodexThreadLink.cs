namespace Quota.Core;

public static class CodexThreadLink
{
    public static string? NormalizeId(string? value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("D") : null;

    public static Uri? Create(string? threadId)
        => NormalizeId(threadId) is { } id ? new Uri("codex://threads/" + id) : null;

    public static Uri? CreateCurrent(string? threadId, string? codexHome = null)
    {
        var home = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var catalog = new CodexSessionCatalog(home);
        catalog.Refresh();
        return Create(catalog.ResolveThreadId(NormalizeId(threadId)));
    }
}
