namespace Quota.Core;

public sealed record CodexSessionIssue(string Key, string? ThreadId, CodexActivityKind Kind, DateTimeOffset? OccurredAt, string Occurrence, bool IsRead = false, string? Title = null);
public enum CodexIssueDisposition { Unread, Read, Ignored }
public sealed record CodexIssueAcknowledgement(string Occurrence, CodexIssueDisposition Disposition);
public sealed class CodexSessionIssueState
{
    public Dictionary<string, CodexIssueAcknowledgement> Entries { get; set; } = [];
}

public sealed class CodexSessionIssues
{
    private readonly AccountStore store;
    private CodexSessionIssueState state;

    public CodexSessionIssues(AccountStore store)
    {
        this.store = store;
        state = store.LoadSessionIssueState() ?? new();
        state.Entries = (state.Entries ?? []).Where(entry => entry.Value is not null && entry.Value.Disposition is CodexIssueDisposition.Read or CodexIssueDisposition.Ignored)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    public void Acknowledge(IEnumerable<CodexSessionIssue> issues, CodexIssueDisposition disposition)
    {
        var updated = new CodexSessionIssueState { Entries = new(state.Entries) };
        foreach (var issue in issues) updated.Entries[issue.Key] = new(issue.Occurrence, disposition);
        store.SaveSessionIssueState(updated);
        state = updated;
    }

    public CodexActivitySnapshot Apply(CodexActivitySnapshot snapshot)
    {
        var issues = snapshot.SessionIssues ?? LegacyIssues(snapshot);
        var visible = issues.Where(issue => Disposition(issue) == CodexIssueDisposition.Unread)
            .Select(issue => issue with { IsRead = false })
            .OrderByDescending(issue => issue.OccurredAt).ThenBy(issue => issue.Key, StringComparer.Ordinal).ToArray();
        var unread = visible.Where(issue => !issue.IsRead).ToArray();
        var kind = snapshot.Kind == CodexActivityKind.Offline ? CodexActivityKind.Offline
            : unread.Any(issue => issue.Kind == CodexActivityKind.Error) ? CodexActivityKind.Error
            : unread.Any(issue => issue.Kind == CodexActivityKind.Paused) ? CodexActivityKind.Paused
            : snapshot.Waiting > 0 ? CodexActivityKind.NeedsInput
            : snapshot.Running > 0 ? CodexActivityKind.Working
            : snapshot.Kind is CodexActivityKind.Paused or CodexActivityKind.Error
                ? snapshot.ReadIssue == CodexActivityReadIssue.None ? CodexActivityKind.Idle : CodexActivityKind.Unknown
                : snapshot.Kind;
        var interrupted = unread.Where(issue => issue.Kind == CodexActivityKind.Paused && CodexThreadLink.NormalizeId(issue.ThreadId) is not null)
            .Select(issue => new CodexInterruptedTask(issue.ThreadId!, issue.OccurredAt)).ToArray();
        return snapshot with { Kind = kind, SessionIssues = visible, InterruptedTasks = interrupted };
    }

    private CodexIssueDisposition Disposition(CodexSessionIssue issue)
        => state.Entries.TryGetValue(issue.Key, out var entry) && entry.Occurrence == issue.Occurrence ? entry.Disposition : CodexIssueDisposition.Unread;

    private static CodexSessionIssue[] LegacyIssues(CodexActivitySnapshot snapshot)
    {
        var interrupted = (snapshot.InterruptedTasks ?? []).Select(task => new CodexSessionIssue(task.ThreadId, task.ThreadId,
            CodexActivityKind.Paused, task.InterruptedAt, $"interrupted/{task.InterruptedAt?.UtcTicks}")).ToArray();
        if (interrupted.Length > 0 || snapshot.Kind is not (CodexActivityKind.Paused or CodexActivityKind.Error)) return interrupted;
        return [new("unidentified-session", null, snapshot.Kind, null, snapshot.Kind.ToString())];
    }
}
