using System.Text.Json;
using Quota.Core;

internal static class MonitoringSmoke
{
    public static void CheckTaskbarSpace()
    {
        var taskbar = new PixelRect(0, 1020, 1600, 60);
        var weather = new PixelRect(20, 1020, 210, 60);
        var tray = new PixelRect(1390, 1020, 210, 60);
        PixelRect[] occupied = [weather, new(650, 1020, 400, 60), tray];
        foreach (var width in new[] { 44d, 140d })
        foreach (var side in new[] { "left", "right" })
        {
            var (expanded, rates) = TaskbarLayout.FindWidgetSpace(taskbar, 1.5, false, weather, tray, occupied, width, true, side);
            Check(rates && expanded?.Width == (int)Math.Round((width + TaskbarLayout.WidgetMetricsWidth) * 1.5), "allow the metrics strip at either alignment and DPI");
            var rect = expanded!.Value;
            Check(occupied.All(x => rect.Left >= x.Right || rect.Right <= x.Left), "expanded strip never covers taskbar controls");
            var (legacy, hidden) = TaskbarLayout.FindWidgetSpace(taskbar, 1.5, false, weather, tray, occupied, width, false, side);
            Check(!hidden && legacy == TaskbarLayout.FindAutomaticSpace(taskbar, 1.5, false, weather, tray, occupied, width, fillHeight: true, placement: side), "disabled metrics preserve the original placement");
        }
        var narrow = new PixelRect(0, 1000, 220, 40);
        var (fallback, showsMetrics) = TaskbarLayout.FindWidgetSpace(narrow, 1, false, null, null, [new(80, 1000, 140, 40)], 44, true, "left");
        Check(!showsMetrics && fallback?.Width == 44, "crowded taskbars keep the original widget instead of hiding it");
        var (shortFallback, shortMetrics) = TaskbarLayout.FindWidgetSpace(narrow with { Height = 32 }, 1, false, null, null, [], 44, true, "left");
        Check(!shortMetrics && shortFallback?.Width == 44, "short taskbars do not clip the metrics");
    }

    public static void CheckQuotaPace()
    {
        var time = DateTimeOffset.UtcNow;
        UsageSnapshot Snapshot(DateTimeOffset timestamp, double shortUsed = 25, double weekUsed = 30, string id = "workspace") => new()
        {
            AccountId = id, Email = "fixture@example.com", FetchedAt = timestamp,
            Buckets =
            [
                new() { LimitId = "short", Name = "Codex", Windows = [Window("primary", 300, shortUsed, time.AddHours(5))] },
                new() { LimitId = "week", Name = "Codex", Windows = [Window("primary", 10080, weekUsed, time.AddDays(5))] }
            ]
        };
        var before = Snapshot(time);
        var after = Snapshot(time.AddMinutes(30), shortUsed: 28);
        after.Buckets.Reverse();
        var weekly = after.Buckets[0].Windows[0];
        var shortTerm = after.Buckets[1].Windows[0];
        var result = QuotaPace.Measure(before, after);
        Check(result.Count == 2 && result[shortTerm] == 6 && result[weekly] == 0, "measure both windows by ID and elapsed time");
        Check(QuotaPace.Measure(null, after).Count == 0 && QuotaPace.Measure(after, before).Count == 0, "a pace needs ordered samples");
        Check(QuotaPace.Measure(before, Snapshot(time.AddMinutes(30), id: "other")).Count == 0, "never compare different workspaces");
        after.Email = "other@example.com";
        Check(QuotaPace.Measure(before, after).Count == 0, "never compare different users in one workspace");
        after.Email = before.Email;
        shortTerm.ResetsAt += 3600;
        Check(!QuotaPace.Measure(before, after).ContainsKey(shortTerm), "a new reset period needs a new baseline");
        shortTerm.ResetsAt = before.Buckets[0].Windows[0].ResetsAt;
        shortTerm.UsedPercent = 23;
        Check(!QuotaPace.Measure(before, after).ContainsKey(shortTerm), "a quota increase is not a decline");
        after.FetchedAt = time.AddHours(6);
        Check(!QuotaPace.Measure(before, after).ContainsKey(shortTerm), "an elapsed reset invalidates an old cycle");
        weekly.ResetsAt = null;
        Check(!QuotaPace.Measure(before, after).ContainsKey(weekly), "a window with no reset identity has no measured rate");

        static QuotaWindow Window(string kind, int duration, double used, DateTimeOffset reset) =>
            new() { Kind = kind, DurationMinutes = duration, UsedPercent = used, ResetsAt = reset.ToUnixTimeSeconds() };
    }

    public static void CheckTokenRate(string home)
    {
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var clock = new FixtureClock { Now = DateTimeOffset.UtcNow };
        var started = clock.Now.AddMinutes(-5);
        string PathFor(string id) => Path.Combine(sessions, "rollout-" + id + ".jsonl");
        string CreateSession(string? parent = null, bool attached = false)
        {
            var id = Guid.NewGuid().ToString("D");
            var path = PathFor(id);
            File.WriteAllLines(path, [Event(clock.Now.AddMinutes(-2), "session_meta", new { id, forked_from_id = parent, attached_thread = attached })]);
            return path;
        }
        void Count(string path, int secondsAgo, long total, long last, long input = 2000)
        {
            File.AppendAllLines(path, [Event(clock.Now.AddSeconds(-secondsAgo), "event_msg", new
            {
                type = "token_count",
                info = new { total_token_usage = new { output_tokens = total, input_tokens = input }, last_token_usage = new { output_tokens = last } }
            })]);
        }
        var one = CreateSession();
        var two = CreateSession();
        var attached = CreateSession(Guid.NewGuid().ToString("D"), attached: true);
        var fork = CreateSession(Guid.NewGuid().ToString("D"));
        Count(one, 10, 120, 120);
        Count(one, 5, 120, 120, input: 8000);
        Count(two, 70, 300, 300);
        Count(two, 2, 360, 60);
        Count(attached, 2, 900, 900);
        Count(fork, 1, 600, 600);
        var monitor = new CodexActivityMonitor(home, persistence: null, clock);
        double? Read(bool include = true) => monitor.ReadSince(started, default, includeTokenRate: include).OutputTokensPerSecond;
        Check(Read() == 3, "sum recent output only; ignore duplicate counts, old output, fork baselines and attached history");
        Check(Read() == 3, "unchanged files do not count twice");
        Count(one, 0, 150, 30);
        Check(Read() == 3.5, "appended token usage changes the rolling rate");
        Count(one, 0, 1, 1);
        Check(Read() == 1, "a counter rollback discards the affected session's old rate");
        clock.Now = clock.Now.AddSeconds(60);
        Check(Read() == 0, "expired deltas become zero without new log writes");
        Check(Read(false) is null && Read() is null, "switching off collection discards the sample");
        Count(one, 1, 61, 60);
        Check(Read() is null, "re-enabling mid-file waits for a fresh counter pair");
        Count(one, 0, 121, 60);
        Check(Read() == 1, "valid increments are measured again after re-enabling");
        Check(monitor.ReadSince(null, default, includeTokenRate: true).OutputTokensPerSecond == 0, "offline activity is zero");
    }

    private static string Event(DateTimeOffset timestamp, string type, object payload) => JsonSerializer.Serialize(new { timestamp, type, payload });
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
