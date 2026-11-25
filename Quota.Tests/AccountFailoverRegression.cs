using System.Text.Json;
using Quota.Core;

internal static class AccountFailoverRegression
{
    internal static void CheckQuota()
    {
        var now = DateTimeOffset.UtcNow;
        var freshness = TimeSpan.FromMinutes(6);
        UsageSnapshot Sample(string id, double used, string limit = "codex", DateTimeOffset? reset = null)
            => new()
            {
                AccountId = id, Email = id + "@example.com", FetchedAt = now,
                Buckets = [new() { LimitId = limit, Windows = [new() { UsedPercent = used, ResetsAt = (reset ?? now.AddHours(1)).ToUnixTimeSeconds() }] }]
            };
        var exhausted = Sample("active", 100);
        Check(QuotaFailover.IsExhausted(exhausted, now, freshness), "100% included usage triggers failover");
        Check(!QuotaFailover.IsExhausted(Sample("active", 99), now, freshness), "low quota does not trigger");
        Check(!QuotaFailover.IsExhausted(Sample("active", 100, "model-specific"), now, freshness), "a model-specific limit does not trigger");
        Check(!QuotaFailover.IsExhausted(Sample("active", 100, reset: now), now, freshness), "a window whose reset passed is not actionable");
        var backup = Sample("backup", 20);
        backup.Buckets[0].Windows.Add(new() { UsedPercent = 40 });
        backup.Buckets.Add(Sample("backup", 100, "model-specific").Buckets.Single());
        Check(QuotaFailover.Available(exhausted, backup, now, freshness) == 60, "rank by the smallest included headroom");
        Check(QuotaFailover.Available(exhausted, Sample("active", 0), now, freshness) is null, "do not switch to another source of the same account");
        Check(QuotaFailover.Available(exhausted, Sample("backup", 100), now, freshness) is null, "exhausted backup is ineligible");
        Check(QuotaFailover.Available(exhausted, Sample("backup", 50, reset: now), now, freshness) is null, "expired backup readings cannot prove headroom");
        Check(QuotaFailover.Available(exhausted, Sample("backup", 10, "other"), now, freshness) is null, "missing included windows cannot prove quota");
        backup.OrdinaryUsageAllowed = false;
        Check(QuotaFailover.Available(exhausted, backup, now, freshness) is null, "an explicit quota block makes the backup ineligible");
        backup.OrdinaryUsageAllowed = true;
        Check(QuotaFailover.Available(exhausted, backup, now.AddMinutes(7), freshness) is null, "stale snapshots are ineligible");
    }

    internal static async Task CheckRestart(string root)
    {
        var home = Path.Combine(root, "restart-capture-home");
        var sessions = Directory.CreateDirectory(Path.Combine(home, "sessions")).FullName;
        var archive = Directory.CreateDirectory(Path.Combine(home, "archived_sessions")).FullName;
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-1);
        string Rollout(string id) => Path.Combine(sessions, $"rollout-{id}.jsonl");
        string Event(DateTimeOffset timestamp, object payload) => JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload });
        string Session(DateTimeOffset time, string? source = null, string? parent = null)
        {
            var id = Guid.NewGuid().ToString("D");
            File.WriteAllLines(Rollout(id), [
                JsonSerializer.Serialize(new { timestamp = time, type = "session_meta", payload = new { id, thread_source = source, attached_thread = parent is not null, forked_from_id = parent } }),
                Event(time, new { type = "task_started", turn_id = "turn" })
            ]);
            return id;
        }
        var oldQuotaError = Session(now.AddMinutes(-45));
        File.AppendAllLines(Rollout(oldQuotaError), [Event(now.AddMinutes(-44), new { type = "error", codex_error_info = "usageLimitExceeded" })]);
        var quotaError = Session(now.AddSeconds(-25));
        File.AppendAllLines(Rollout(quotaError), [
            Event(now.AddSeconds(-24), new { type = "error", codex_error_info = "usageLimitExceeded" }),
            Event(now.AddSeconds(-23), new { type = "task_complete", turn_id = "turn" })
        ]);
        var otherError = Session(now.AddSeconds(-20));
        File.AppendAllLines(Rollout(otherError), [Event(now.AddSeconds(-19), new { type = "error", codex_error_info = "contextWindowExceeded" })]);
        var older = Session(now.AddSeconds(-18));
        var latest = Session(now.AddSeconds(-14));
        var archived = Session(now.AddSeconds(-12));
        File.Copy(Rollout(archived), Path.Combine(archive, $"rollout-{archived}.jsonl"));
        _ = Session(now.AddSeconds(-10), source: "guardian_review");
        _ = Session(now.AddSeconds(-9), parent: latest);
        var journal = new AccountStore(Path.Combine(root, "restart-capture-state"));
        var monitor = new CodexActivityMonitor(home, journal);
        var order = new List<string>();
        Task<string?> Capture(CancellationToken _) { order.Add("capture"); return Task.FromResult(monitor.ReadSince(start).RestartThreadId); }
        Task<bool> Close() { order.Add("close"); return Task.FromResult(true); }
        Task Commit(CancellationToken _) { order.Add("commit"); return Task.CompletedTask; }
        CodexRestartOutcome Launch(Uri? link)
        {
            order.Add("launch");
            Check(link?.AbsoluteUri == "codex://threads/" + latest, "launch the latest visible running chat by ID");
            return CodexRestartOutcome.SessionRequested;
        }
        var outcome = await CodexDesktopRestart.RestartPreparedAsync(Capture, Close, Commit, Launch, CancellationToken.None);
        Check(outcome == CodexRestartOutcome.SessionRequested && order.SequenceEqual(["capture", "close", "commit", "launch"]),
            "capture the chat before stopping and commit before relaunch");
        order.Clear();
        outcome = await CodexDesktopRestart.RestartPreparedAsync(Capture, () => Task.FromResult(false), Commit, Launch, CancellationToken.None);
        Check(outcome == CodexRestartOutcome.StillRunning && order.SequenceEqual(["capture"]), "no credentials change and no launch when the desktop stays running");
        order.Clear();
        try
        {
            await CodexDesktopRestart.RestartPreparedAsync(Capture, Close, _ => throw new IOException("test commit failure"), Launch, CancellationToken.None);
            throw new InvalidOperationException("commit failure must propagate");
        }
        catch (IOException) { Check(order.SequenceEqual(["capture", "close", "launch"]), "the old desktop relaunches if writing credentials fails"); }
        foreach (var id in new[] { older, latest }) File.AppendAllLines(Rollout(id), [Event(now.AddSeconds(-1), new { type = "task_complete", turn_id = "turn" })]);
        var afterCompletion = monitor.ReadSince(start);
        Check(afterCompletion.RestartThreadId is null && afterCompletion.QuotaBlockedThreadId == quotaError, "a recent explicitly quota-blocked chat is the fallback");
        File.AppendAllLines(Rollout(quotaError), [Event(now, new { type = "turn_aborted", turn_id = "turn" })]);
        var afterAbort = monitor.ReadSince(start);
        Check(afterAbort.RestartThreadId is null && afterAbort.QuotaBlockedThreadId is null, "do not reopen old errors, archived sessions, or internal workers");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
