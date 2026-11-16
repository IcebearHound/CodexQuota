using System.Text.Json;
using Quota.Core;

internal static class SessionBindingRegression
{
    public static Task Run(string root)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        var home = Path.Combine(root, "binding-home");
        var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var store = new AccountStore(Path.Combine(root, "binding-store"));
        var parent = Guid.NewGuid().ToString("D"); var attachment = Guid.NewGuid().ToString("D"); var fork = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow; var start = now.AddMinutes(-5);
        var parentPath = Path.Combine(sessions, "rollout-" + parent + ".jsonl");
        var childPath = Path.Combine(sessions, "rollout-" + attachment + ".jsonl");
        var forkPath = Path.Combine(sessions, "rollout-" + fork + ".jsonl");
        File.WriteAllText(parentPath, Metadata(parent) + Event("task_started", now.AddMinutes(-1)));
        File.WriteAllText(childPath, Metadata(attachment, parent, true) + Event("turn_aborted", now));
        File.WriteAllText(forkPath, Metadata(fork, parent, false) + Event("turn_aborted", now));

        using var database = new SessionDatabaseFixture(Path.Combine(home, "state_5.sqlite"));
        database.Execute("ALTER TABLE threads ADD COLUMN attached_thread_parent_id TEXT; ALTER TABLE threads ADD COLUMN rollout_path TEXT");
        database.Execute($"INSERT INTO threads VALUES ('{parent}','Live chat',0,NULL,'{Sql(parentPath)}'); INSERT INTO threads VALUES ('{attachment}','Hidden snapshot',0,'{parent}','{Sql(childPath)}'); INSERT INTO threads VALUES ('{fork}','Independent fork',0,NULL,'{Sql(forkPath)}')");
        var monitor = new CodexActivityMonitor(home, store);
        var first = monitor.ReadSince(start);
        Check(first.SessionIssues!.Single().ThreadId == fork, "attached snapshots are excluded while an independent fork remains actionable");
        Check(CodexThreadLink.CreateCurrent(attachment, home)?.AbsoluteUri == "codex://threads/" + parent, "attached links resolve to the visible parent");
        Check(CodexThreadLink.CreateCurrent(fork, home)?.AbsoluteUri == "codex://threads/" + fork, "ordinary forks keep their own link");

        var restart = now.AddSeconds(2);
        monitor.ReadSince(restart);
        File.AppendAllText(parentPath, Event("task_started", restart.AddSeconds(1)));
        File.AppendAllText(forkPath, Event("task_complete", restart.AddSeconds(1)));
        var restored = monitor.ReadSince(restart);
        Check(restored.Kind == CodexActivityKind.Working && restored.SessionIssues!.Count == 0, "resuming after restart removes interruption state without retaining hidden snapshots");

        var replacementPath = Path.Combine(sessions, "current-" + parent + ".jsonl");
        File.WriteAllText(replacementPath, Metadata(parent) + Event("task_complete", restart.AddSeconds(3)));
        File.AppendAllText(parentPath, Event("turn_aborted", restart.AddSeconds(2)));
        database.Execute($"UPDATE threads SET rollout_path='{Sql(replacementPath)}' WHERE id='{parent}'");
        var replaced = new CodexActivityMonitor(home, store).ReadSince(restart);
        Check(replaced.SessionIssues!.Count == 0, "only the canonical rollout contributes status when old and current files share a chat ID");

        var checkpoint = store.LoadActivityJournal()!;
        checkpoint.Sessions["legacy-hidden-checkpoint"] = new() { Kind = CodexActivityKind.Paused, ThreadId = attachment, InterruptedAt = restart };
        checkpoint.Sessions["legacy-obsolete-rollout"] = new() { Kind = CodexActivityKind.Paused, ThreadId = parent, InterruptedAt = restart };
        store.SaveActivityJournal(checkpoint);
        Check(new CodexActivityMonitor(home, store).ReadSince(null).SessionIssues!.Count == 0, "legacy hidden and obsolete checkpoints are removed even while Codex is offline");
        database.Execute($"UPDATE threads SET archived=1 WHERE id='{parent}'");
        Check(CodexThreadLink.CreateCurrent(attachment, home) is null, "archived parents cannot be reopened through a stale attachment");
        database.Execute($"UPDATE threads SET archived=0, attached_thread_parent_id='{attachment}' WHERE id='{parent}'");
        Check(CodexThreadLink.CreateCurrent(attachment, home) is null, "invalid cyclic bindings cannot launch a different chat");

        var metadataHome = Path.Combine(root, "binding-without-db"); Directory.CreateDirectory(Path.Combine(metadataHome, "sessions"));
        var metadataFile = Path.Combine(metadataHome, "sessions", attachment + ".jsonl");
        var largeMetadata = JsonSerializer.Serialize(new { timestamp = now, type = "session_meta", payload = new { id = attachment, forked_from_id = parent, attached_thread = true, base_instructions = new string('x', 32 * 1024) } });
        File.WriteAllText(metadataFile, largeMetadata + "\n" + Event("turn_aborted", now));
        Check(new CodexActivityMonitor(metadataHome).ReadSince(start).SessionIssues!.Count == 0, "attached metadata excludes hidden snapshots when the database is unavailable");
        return Task.CompletedTask;
    }
    private static string Metadata(string id, string? parent = null, bool attached = false)
        => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "session_meta", payload = new { id, forked_from_id = parent, attached_thread = attached } }) + "\n";
    private static string Event(string type, DateTimeOffset time)
        => JsonSerializer.Serialize(new { timestamp = time, type = "event_msg", payload = new { type } }) + "\n";
    private static string Sql(string value) => value.Replace("'", "''");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
