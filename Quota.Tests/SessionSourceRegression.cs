using System.Text.Json;
using Quota.Core;

internal static class SessionSourceRegression
{
    public static Task CheckRollouts(string root)
    {
        var home = Path.Combine(root, "background-rollouts");
        var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var store = new AccountStore(Path.Combine(home, "store"));
        var now = DateTimeOffset.UtcNow; var started = now.AddMinutes(-1);
        var parent = Guid.NewGuid().ToString("D"); var worker = Guid.NewGuid().ToString("D");
        var review = Guid.NewGuid().ToString("D"); var aborted = Guid.NewGuid().ToString("D");
        string Rollout(string id) => Path.Combine(sessions, id + ".jsonl");
        File.WriteAllText(Rollout(parent), Record(now, "session_meta", new { id = parent, source = "vscode", thread_source = "agent_created_thread" }) + TaskEvent(now, "task_started"));
        // The older source field is sufficient even if thread_source or the SQLite row is not available yet.
        var spawnSource = new { subagent = new { thread_spawn = new { parent_thread_id = parent, depth = 1 } } };
        File.WriteAllText(Rollout(worker), Record(now, "session_meta", new { id = worker, source = spawnSource }) + TaskEvent(now, "task_started")
            + Record(now, "event_msg", new { type = "token_count", info = new { total_token_usage = new { output_tokens = 60 }, last_token_usage = new { output_tokens = 60 } } }));
        File.WriteAllText(Rollout(review), Record(now, "session_meta", new { id = review, thread_source = "guardian_review" }) + TaskEvent(now, "error"));
        File.WriteAllText(Rollout(aborted), Record(now, "session_meta", new { id = aborted, thread_source = "subagent" }) + TaskEvent(now, "turn_aborted"));

        var monitor = new CodexActivityMonitor(home, store);
        var live = monitor.ReadSince(started, default, includeTokenRate: true);
        Check(live.Kind == CodexActivityKind.Working && live.Running == 1 && live.SessionIssues!.Count == 0, "only the main chat contributes status; internal failures and cancellations do not alert");
        Check(live.OutputTokensPerSecond == 1, "the local output rate still counts internal model output");

        var offline = monitor.ReadSince(null);
        Check(offline.Kind == CodexActivityKind.Offline && offline.SessionIssues!.Single().ThreadId == parent
            && offline.InterruptedTasks!.Single().ThreadId == parent, "stopping Codex only records the real running chat, even without a title or a database");
        Check(store.LoadActivityJournal()!.Sessions.Values.Single().ThreadId == parent, "no internal checkpoint survives to create a false interruption");
        File.AppendAllText(Rollout(parent), TaskEvent(now.AddSeconds(10), "task_started") + TaskEvent(now.AddSeconds(11), "task_complete"));
        var resumed = new CodexActivityMonitor(home, store).ReadSince(now.AddSeconds(5));
        Check(resumed.Kind == CodexActivityKind.Completed && resumed.SessionIssues!.Count == 0, "resuming the real chat clears its interruption");
        return Task.CompletedTask;
    }

    public static Task CheckJournal(string root)
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        foreach (var modern in new[] { true, false })
        {
            var home = Path.Combine(root, modern ? "modern-thread-source" : "legacy-session-source");
            Directory.CreateDirectory(home);
            var store = new AccountStore(Path.Combine(home, "store"));
            var normal = Guid.NewGuid().ToString("D"); var internalId = Guid.NewGuid().ToString("D");
            var now = DateTimeOffset.UtcNow;
            new CodexActivityMonitor(home, store).ReadSince(null);
            var legacy = store.LoadActivityJournal()!;
            legacy.Sessions["real"] = new() { ThreadId = normal, Kind = CodexActivityKind.Paused, InterruptedAt = now };
            legacy.Sessions["internal"] = new() { ThreadId = internalId, Kind = CodexActivityKind.Paused, InterruptedAt = now };
            store.SaveActivityJournal(legacy);

            using var db = new SessionDatabaseFixture(Path.Combine(home, "state_5.sqlite"));
            db.Execute("ALTER TABLE threads ADD COLUMN source TEXT");
            if (modern)
            {
                db.Execute("ALTER TABLE threads ADD COLUMN thread_source TEXT");
                db.Execute($"INSERT INTO threads VALUES ('{normal}', '', 0, 'vscode', 'agent_created_thread'); INSERT INTO threads VALUES ('{internalId}', '', 0, 'vscode', 'subagent')");
            }
            else
                db.Execute($$$$"""
                    INSERT INTO threads VALUES ('{{{{normal}}}}', '', 0, 'vscode');
                    INSERT INTO threads VALUES ('{{{{internalId}}}}', '', 0, '{"subagent":{"thread_spawn":{"parent_thread_id":"{{{{normal}}}}"}}}');
                    """);

            var snapshot = new CodexActivityMonitor(home, store).ReadSince(null);
            Check(snapshot.SessionIssues!.Single().ThreadId == normal && snapshot.InterruptedTasks!.Single().ThreadId == normal, "startup prunes already persisted internal issues but keeps valid chats with missing titles and logs");
            Check(store.LoadActivityJournal()!.Sessions.Values.Single().ThreadId == normal, "the cleaned journal is saved");
            Check(CodexThreadLink.CreateCurrent(internalId, home) is null && CodexThreadLink.CreateCurrent(normal, home)?.AbsoluteUri == "codex://threads/" + normal,
                "only the real chat opens from the exception list");
        }
        return Task.CompletedTask;
    }

    private static string Record(DateTimeOffset timestamp, string type, object payload) => JsonSerializer.Serialize(new { timestamp, type, payload }) + "\n";
    private static string TaskEvent(DateTimeOffset timestamp, string type) => Record(timestamp, "event_msg", new { type });
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
