using System.Text.Json;
using Quota.Core;

if (args.FirstOrDefault() == "--fake") { await Fake(args.ElementAtOrDefault(1) ?? "normal"); return; }
if (args.FirstOrDefault() is "--cc-scan" or "--cc-import")
{
    var scan = CcSwitchImport.Scan(CcSwitchImport.DefaultDirectory);
    if (args[0] == "--cc-scan")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { Candidates = scan.Accounts.Count, WithAccessToken = scan.Accounts.Count(account => account.HasAccessToken), Notices = scan.Notices.Count, CliFound = CliLocator.Find() is not null })); return;
    }
    var store = new AccountStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget"));
    var existing = store.LoadAccounts(); var added = CcSwitchImport.Import(store, existing, scan.Accounts);
    var cli = CliLocator.Find() ?? throw new Exception("Codex CLI not found"); var service = new CodexService(store);
    var succeeded = 0; var failed = 0;
    foreach (var record in added)
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65)); var usage = await service.RefreshAsync(cli, record, deadline.Token); store.SaveCache(record.Id, usage); succeeded++; }
        catch (Exception error) when (error is CodexException or OperationCanceledException or IOException or InvalidOperationException) { failed++; }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Imported = added.Count, QuotaReadSucceeded = succeeded, QuotaReadFailed = failed, TotalManaged = store.LoadAccounts().Count })); return;
}
if (args.FirstOrDefault() == "app-server")
{
    var scenario = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.GetTempPath(), ".quota-test-scenario");
    await Fake(File.Exists(scenario) ? File.ReadAllText(scenario).Trim() : "per-profile"); return;
}
if (args.FirstOrDefault() == "--live")
{
    var cli = CliLocator.Find(); if (cli is null) throw new Exception("Codex CLI not found");
    await using var client = new CodexClient(cli, null); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var usage = await client.ReadUsageAsync(deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { LiveUsageRead = true, BucketCount = usage.Buckets.Count, WindowCount = usage.Buckets.Sum(b => b.Windows.Count), HasIdentity = usage.Email is not null, HasBackendAccountId = usage.AccountId is not null, ResetCreditCount = usage.ResetCreditAvailableCount, ResetDetailsKnown = usage.ResetCreditDetailsKnown, ExpiringResetCards = usage.ResetCredits.Count(credit => credit.ExpiresAt is not null && credit.ExpiryKnown) })); return;
}
if (args.FirstOrDefault() == "--live-activity")
{
    var activity = await Task.Run(() => new CodexActivityMonitor().Read());
    Console.WriteLine(JsonSerializer.Serialize(new { Kind = activity.Kind.ToString(), activity.Running, activity.Waiting, activity.Tracked, ReadIssue = activity.ReadIssue.ToString() })); return;
}
if (args.FirstOrDefault() == "--probe-login")
{
    var cli = CliLocator.Find() ?? throw new Exception("Codex CLI not found");
    var root = Path.Combine(Path.GetTempPath(), "CodexQuotaLoginProbe-" + Guid.NewGuid().ToString("N"));
    var store = new AccountStore(root); var id = Guid.NewGuid().ToString("N"); store.CreateProfile(id);
    using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var officialUrl = false;
    try
    {
        var service = new CodexService(store, openBrowser: uri => { officialUrl = uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com"; canceled.Cancel(); });
        try { await service.LoginAsync(cli, id, _ => { }, canceled.Token); } catch (OperationCanceledException) when (officialUrl) { }
        if (!officialUrl) throw new Exception("No verified official login URL returned");
        Console.WriteLine("{\"LiveLoginStartAndCancel\":true,\"OfficialHostValidated\":true,\"BrowserOpened\":false}");
    }
    finally { store.DeleteProfile(id); Directory.Delete(root, true); }
    return;
}

var pass = 0; var total = 0;
async Task Test(string name, Func<Task> test)
{
    total++; try { await test(); Console.WriteLine("PASS " + name); pass++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + " : " + e); Environment.ExitCode = 1; }
}
void Check(bool value, string reason = "assertion failed") { if (!value) throw new Exception(reason); }
async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
CodexClient Client(string mode = "normal", string? profile = null) => new(Environment.ProcessPath!, profile, ["--fake", mode]);
var testRoot = Path.Combine(Path.GetTempPath(), "CodexQuotaTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

await Test("native process metadata reads quoted Unicode commands without COM", async () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var type = typeof(CodexActivityMonitor).Assembly.GetType("Quota.Core.CodexProcessMonitor", true)!;
    var read = type.GetMethod("ReadNativeMetadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    foreach (var scenario in new[] { (Argument: "app-server", Kind: "AppServer"), (Argument: "exec-server", Kind: "Worker"), (Argument: "interactive", Kind: "Interactive") })
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var word in new[] { "--fake", "normal", scenario.Argument, "带空格的参数 " + new string('x', 16000) }) start.ArgumentList.Add(word);
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            var metadata = await Task.Run(() => read.Invoke(null, [process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime())]));
            Check(metadata is not null, "native metadata is available");
            var data = metadata!.GetType();
            Check(data.GetProperty("Kind")!.GetValue(metadata)!.ToString() == scenario.Kind, "native commands retain process classification");
            Check((int)data.GetProperty("Parent")!.GetValue(metadata)! == Environment.ProcessId, "native parent identity is retained");
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
    }
    Check(read.Invoke(null, [int.MaxValue, DateTimeOffset.UtcNow]) is null, "exited or unavailable processes are retried through the fallback");
});
await Test("activity distinguishes missing session files from waiting for events", () =>
{
    var root = Path.Combine(testRoot, "activity-read-reasons");
    var monitor = new CodexActivityMonitor(root); var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    var missing = monitor.ReadSince(started);
    Check(missing.Kind == CodexActivityKind.Unknown && missing.ReadIssue == CodexActivityReadIssue.SessionsMissing);
    Directory.CreateDirectory(Path.Combine(root, "sessions"));
    var waiting = monitor.ReadSince(started);
    Check(waiting.Kind == CodexActivityKind.Unknown && waiting.ReadIssue == CodexActivityReadIssue.NoSessionEvents);
    File.WriteAllText(Path.Combine(root, "sessions", "active.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "event_msg", payload = new { type = "task_started" } }) + "\n");
    var recovered = new CodexActivityMonitor(root).ReadSince(started);
    Check(recovered.Kind == CodexActivityKind.Working && recovered.ReadIssue == CodexActivityReadIssue.None);
    return Task.CompletedTask;
});
await Test("session read and ignore states persist per occurrence and preserve running work", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "issue-acknowledgements")); var issues = new CodexSessionIssues(store);
    var paused = new CodexSessionIssue("paused", Guid.NewGuid().ToString("D"), CodexActivityKind.Paused, DateTimeOffset.UtcNow, "first");
    var error = new CodexSessionIssue("error", Guid.NewGuid().ToString("D"), CodexActivityKind.Error, DateTimeOffset.UtcNow, "first-error");
    var snapshot = new CodexActivitySnapshot(CodexActivityKind.Error, Running: 1, SessionIssues: [paused, error]);
    Check(issues.Apply(snapshot).Kind == CodexActivityKind.Error);
    issues.Acknowledge([error], CodexIssueDisposition.Read);
    Check(issues.Apply(snapshot).Kind == CodexActivityKind.Paused);
    issues.Acknowledge([paused], CodexIssueDisposition.Ignored);
    var cleared = new CodexSessionIssues(store).Apply(snapshot);
    Check(cleared.Kind == CodexActivityKind.Working && cleared.SessionIssues!.Count == 0);
    var repeated = paused with { Occurrence = "second" };
    var fresh = issues.Apply(snapshot with { SessionIssues = [repeated, error] });
    Check(fresh.Kind == CodexActivityKind.Paused && fresh.SessionIssues!.Count(issue => !issue.IsRead) == 1);
    issues.Acknowledge([paused], CodexIssueDisposition.Read);
    Check(issues.Apply(snapshot with { SessionIssues = [repeated] }).Kind == CodexActivityKind.Paused, "an action on an old row cannot mark a new occurrence read");
    issues.Acknowledge([repeated, error], CodexIssueDisposition.Read);
    snapshot = snapshot with { SessionIssues = [repeated, error] };
    Check(issues.Apply(snapshot with { Running = 0 }).Kind == CodexActivityKind.Idle);
    Check(issues.Apply(snapshot with { Waiting = 1 }).Kind == CodexActivityKind.NeedsInput);
    Check(issues.Apply(snapshot with { Kind = CodexActivityKind.Offline }).Kind == CodexActivityKind.Offline);
    return Task.CompletedTask;
});
await Test("monitor occurrence identities survive polling and restart and renew after another interruption", () =>
{
    var home = Path.Combine(testRoot, "issue-monitor"); var folder = Path.Combine(home, "sessions"); Directory.CreateDirectory(folder);
    var store = new AccountStore(Path.Combine(home, "store")); var issues = new CodexSessionIssues(store);
    var id = Guid.NewGuid().ToString("D"); var path = Path.Combine(folder, id + ".jsonl"); var now = DateTimeOffset.UtcNow;
    string Event(string kind, string turn, int seconds) => JsonSerializer.Serialize(new { timestamp = now.AddSeconds(seconds), type = "event_msg", payload = new { type = kind, turn_id = turn } }) + "\n";
    File.WriteAllText(path, Event("task_started", "one", 0) + Event("turn_aborted", "one", 1));
    var monitor = new CodexActivityMonitor(home, store); var first = monitor.ReadSince(now.AddMinutes(-1));
    var issue = first.SessionIssues!.Single(); issues.Acknowledge([issue], CodexIssueDisposition.Read);
    Check(issues.Apply(monitor.ReadSince(now.AddMinutes(-1))).Kind == CodexActivityKind.Idle);
    var restored = new CodexActivityMonitor(home, store).ReadSince(now.AddMinutes(-1));
    Check(restored.SessionIssues!.Single().Occurrence == issue.Occurrence && new CodexSessionIssues(store).Apply(restored).Kind == CodexActivityKind.Idle);
    File.AppendAllText(path, Event("task_started", "two", 2) + Event("turn_aborted", "two", 3));
    var next = monitor.ReadSince(now.AddMinutes(-1));
    Check(next.SessionIssues!.Single().Occurrence != issue.Occurrence && issues.Apply(next).Kind == CodexActivityKind.Paused);
    issues.Acknowledge(next.SessionIssues!, CodexIssueDisposition.Ignored);
    Check(issues.Apply(new CodexActivityMonitor(home, store).ReadSince(now.AddMinutes(-1))).SessionIssues!.Count == 0);
    File.AppendAllText(path, Event("task_started", "three", 4) + Event("error", "three", 5));
    var failed = monitor.ReadSince(now.AddMinutes(-1)); Check(issues.Apply(failed).Kind == CodexActivityKind.Error);
    return Task.CompletedTask;
});
await Test("failed session acknowledgement saves retain the unread state and can retry", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "issue-save-failure")); var issues = new CodexSessionIssues(store);
    var issue = new CodexSessionIssue("unknown-id", null, CodexActivityKind.Paused, null, "legacy");
    var snapshot = new CodexActivitySnapshot(CodexActivityKind.Paused, SessionIssues: [issue]);
    var blocker = Path.Combine(store.Root, "session-issues.json"); Directory.CreateDirectory(blocker);
    try { issues.Acknowledge([issue], CodexIssueDisposition.Read); throw new Exception("save must fail"); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    Check(issues.Apply(snapshot).Kind == CodexActivityKind.Paused && !issues.Apply(snapshot).SessionIssues!.Single().IsRead);
    Directory.Delete(blocker); issues.Acknowledge([issue], CodexIssueDisposition.Read);
    Check(new CodexSessionIssues(store).Apply(snapshot).Kind == CodexActivityKind.Idle);
    return Task.CompletedTask;
});
await Test("read error identities survive a tail restart after the turn start leaves the window", () =>
{
    var home = Path.Combine(testRoot, "read-error-tail"); var folder = Path.Combine(home, "sessions"); Directory.CreateDirectory(folder);
    var store = new AccountStore(Path.Combine(home, "store")); var engine = new CodexSessionIssues(store);
    var now = DateTimeOffset.UtcNow; var path = Path.Combine(folder, Guid.NewGuid().ToString("D") + ".jsonl");
    File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", payload = new { type = "task_started", turn_id = "one" } }) + "\n");
    var monitor = new CodexActivityMonitor(home, store); monitor.ReadSince(now.AddMinutes(-1));
    File.AppendAllText(path, new string('x', 4 * 1024 * 1024 + 2) + "\n" + JsonSerializer.Serialize(new { timestamp = now.AddSeconds(1), type = "event_msg", payload = new { type = "error" } }) + "\n");
    var failed = monitor.ReadSince(now.AddMinutes(-1)); engine.Acknowledge(failed.SessionIssues!, CodexIssueDisposition.Read);
    var recovered = new CodexActivityMonitor(home, store).ReadSince(now.AddMinutes(-1));
    Check(new CodexSessionIssues(store).Apply(recovered).Kind == CodexActivityKind.Idle);
    return Task.CompletedTask;
});
await Test("desktop restart resolves the registered app identity for the matching executable", () =>
{
    var method = typeof(CodexDesktopRestart).GetMethod("ResolveApplicationId", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    var root = Path.Combine(testRoot, "OpenAI.Codex_1.0.0.0_x64__publisher");
    var path = Path.Combine(root, "app", "ChatGPT.exe");
    var manifest = System.Xml.Linq.XDocument.Parse("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="OpenAI.Codex"/><Applications><Application Id="Helper" Executable="helper.exe"/><Application Id="App" Executable="app/ChatGPT.exe"/></Applications></Package>""");
    Check((string?)method.Invoke(null, [manifest, root, path, "OpenAI.Codex_publisher"]) == "OpenAI.Codex_publisher!App");
    Check(method.Invoke(null, [manifest, root, Path.Combine(root, "missing.exe"), "OpenAI.Codex_publisher"]) is null);
    var foreign = System.Xml.Linq.XDocument.Parse(manifest.ToString().Replace("Name=\"OpenAI.Codex\"", "Name=\"Another.App\""));
    Check(method.Invoke(null, [foreign, root, path, "OpenAI.Codex_publisher"]) is null);
    var resolver = typeof(CodexDesktopRestart).GetMethod("FindApplicationId", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    Check(resolver.Invoke(null, [Path.Combine(root, "missing", "ChatGPT.exe")]) is null);
    return Task.CompletedTask;
});
await Test("archive state and session titles refresh from the live WAL database", () =>
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    var home = Path.Combine(testRoot, "live-archive"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
    var id = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow; var path = Path.Combine(home, "sessions", id + ".jsonl");
    File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", payload = new { type = "turn_aborted" } }) + "\n");
    using var database = new SessionDatabaseFixture(Path.Combine(home, "state_5.sqlite"));
    database.Execute($"INSERT INTO threads VALUES ('{id}', '初始名称', 0)");
    var store = new AccountStore(Path.Combine(home, "store")); var monitor = new CodexActivityMonitor(home, store);
    Check(monitor.ReadSince(now.AddMinutes(-1)).SessionIssues!.Single().Title == "初始名称");
    database.Execute($"UPDATE threads SET title='更新后的名称', archived=1 WHERE id='{id}'");
    var archived = monitor.ReadSince(now.AddMinutes(-1));
    Check(archived.SessionIssues!.Count == 0 && archived.Kind != CodexActivityKind.Paused, "archiving clears the list and red interruption state on the next poll");
    Check(monitor.ReadSince(null).SessionIssues!.Count == 0, "offline checkpoints also exclude archived sessions");
    Check(new CodexActivityMonitor(home, store).ReadSince(now.AddMinutes(-1)).SessionIssues!.Count == 0, "archived state persists across monitor restart");
    database.Execute($"UPDATE threads SET archived=0 WHERE id='{id}'");
    Check(monitor.ReadSince(now.AddMinutes(-1)).SessionIssues!.Single().Title == "更新后的名称", "unarchive uses the current title and re-evaluates the live log");
    File.AppendAllText(path, JsonSerializer.Serialize(new { timestamp = now.AddSeconds(1), type = "event_msg", payload = new { type = "task_complete" } }) + "\n");
    Check(monitor.ReadSince(now.AddMinutes(-1)).SessionIssues!.Count == 0, "completed sessions disappear without reopening the list");
    return Task.CompletedTask;
});
await Test("archived rollout files suppress stale journal entries when no database exists", () =>
{
    var home = Path.Combine(testRoot, "archive-files"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
    var store = new AccountStore(Path.Combine(home, "store")); var id = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow;
    var path = Path.Combine(home, "sessions", "rollout-" + id + ".jsonl");
    File.WriteAllText(path, JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", payload = new { type = "turn_aborted" } }) + "\n");
    var monitor = new CodexActivityMonitor(home, store); Check(monitor.ReadSince(now.AddMinutes(-1)).SessionIssues!.Count == 1);
    var archive = Path.Combine(home, "archived_sessions"); Directory.CreateDirectory(archive); File.Move(path, Path.Combine(archive, Path.GetFileName(path)));
    Check(monitor.ReadSince(now.AddMinutes(-1)).SessionIssues!.Count == 0);
    Check(new CodexActivityMonitor(home, store).ReadSince(now.AddMinutes(-1)).SessionIssues!.Count == 0);
    return Task.CompletedTask;
});
await Test("thread links accept canonical chat IDs and reject arbitrary navigation", () =>
{
    var id = Guid.NewGuid().ToString("D");
    Check(CodexThreadLink.Create(id.ToUpperInvariant())?.AbsoluteUri == "codex://threads/" + id);
    foreach (var invalid in new[] { "", "new", "../settings", "https://example.com", Guid.Empty.ToString(), "abc?prompt=run" }) Check(CodexThreadLink.Create(invalid) is null);
    return Task.CompletedTask;
});
await Test("overall taskbar background ignores isolated bright points and averages the remaining area", () =>
{
    var dark = new RgbColor(25, 30, 35); var white = new RgbColor(255, 255, 255);
    Check(ContrastColors.AverageBackground(Enumerable.Repeat(dark, 116).Append(white)) == dark);
    Check(ContrastColors.AverageBackground(Enumerable.Repeat(white, 116).Append(dark)) == white);
    var gradient = Enumerable.Range(0, 100).Select(value => new RgbColor((byte)value, (byte)value, (byte)value));
    Check(ContrastColors.AverageBackground(gradient) is { Red: >= 49 and <= 50 });
    Check(ContrastColors.AverageBackground([]) is null);
    Check(ContrastColors.TextOn(dark, new(0, 0, 0)) == white);
    return Task.CompletedTask;
});
await Test("interrupted chat targets survive monitor restart missing logs and oversized history", () =>
{
    var root = Path.Combine(testRoot, "interruption-links"); var home = Path.Combine(root, "home"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
    var store = new AccountStore(Path.Combine(root, "data")); var id = Guid.NewGuid().ToString("D"); var start = DateTimeOffset.UtcNow.AddMinutes(-2); var stopped = DateTimeOffset.UtcNow.AddMinutes(-1);
    var file = Path.Combine(sessions, "session.jsonl");
    var metadata = JsonSerializer.Serialize(new { timestamp = start.AddDays(-1), type = "session_meta", payload = new { id } });
    string ActivityEvent(string type, DateTimeOffset time) => JsonSerializer.Serialize(new { timestamp = time, type = "event_msg", payload = new { type } }) + "\n";
    File.WriteAllText(file, metadata + "\n" + new string(' ', 4 * 1024 * 1024 + 100) + "\n" + ActivityEvent("task_started", start.AddSeconds(1)) + ActivityEvent("turn_aborted", stopped));
    var result = new CodexActivityMonitor(home, store).ReadSince(start);
    Check(result.Kind == CodexActivityKind.Paused && result.InterruptedTasks?.Single().ThreadId == id);
    Check(store.LoadActivityJournal()!.Sessions.Values.Single().ThreadId == id);
    Check(new CodexActivityMonitor(home, store).ReadSince(start).InterruptedTasks?.Single().ThreadId == id);
    var offline = new CodexActivityMonitor(home, store).ReadSince(null);
    Check(offline.Kind == CodexActivityKind.Offline && offline.InterruptedTasks?.Single().ThreadId == id);
    File.Delete(file);
    Check(new CodexActivityMonitor(home, store).ReadSince(start.AddSeconds(5)).InterruptedTasks?.Single().ThreadId == id);
    return Task.CompletedTask;
});
await Test("rollout filenames locate interruptions when session metadata is unavailable", () =>
{
    var home = Path.Combine(testRoot, "filename-interruption"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
    var id = Guid.NewGuid().ToString("D"); var now = DateTimeOffset.UtcNow;
    File.WriteAllText(Path.Combine(sessions, "rollout-2026-11-11T12-00-00-" + id + ".jsonl"), JsonSerializer.Serialize(new { timestamp = now, type = "event_msg", payload = new { type = "turn_aborted" } }) + "\n");
    Check(new CodexActivityMonitor(home).ReadSince(now.AddMinutes(-1)).InterruptedTasks?.Single().ThreadId == id);
    return Task.CompletedTask;
});

await Test("account name limits preserve complete Unicode text elements", () =>
{
    Check(AccountStore.CleanName(new string('a', 39) + "😀end") == new string('a', 39));
    var name = AccountStore.CleanName(new string('a', 38) + "😀end");
    Check(name == new string('a', 38) + "😀" && name.Length == 40);
    Check(AccountStore.CleanName(new string('a', 39) + "e\u0301") == new string('a', 39));
    Check(JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(name)) == name); return Task.CompletedTask;
});
await Test("account names sanitize empty controls and malformed UTF-16", () =>
{
    Check(AccountStore.CleanName(" \0\n\t ") == "未命名账号");
    var name = AccountStore.CleanName("\ud800name");
    Check(name == "\ufffdname" && JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(name)) == name); return Task.CompletedTask;
});
await Test("nullable and malformed windows stay unknown", () =>
{
    var snapshot = UsageSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":{"usedPercent":35,"windowDurationMins":null,"resetsAt":null}}}"""), Json("{}"));
    Check(snapshot.Buckets[0].Windows.Count == 1); Check(snapshot.Buckets[0].Windows[0].Remaining == 65); Check(snapshot.Buckets[0].Windows[0].ResetsAt is null); return Task.CompletedTask;
});
await Test("multi-bucket quotas and backend blocked state are retained", () =>
{
    var snapshot = UsageSnapshot.Parse(Json("""{"ordinaryUsageAllowed":false,"rateLimits":{},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":140}},"other":{"primary":{"usedPercent":-8},"credits":{"balance":"12.4","unlimited":false}}}}"""), Json("""{"account":{"email":"test@example.com"}}"""));
    Check(snapshot.Buckets.Count == 2 && snapshot.Buckets[0].Windows[0].Remaining == 0 && snapshot.Buckets[1].Windows.Count == 0); Check(snapshot.OrdinaryUsageAllowed == false); Check(snapshot.Email == "test@example.com"); return Task.CompletedTask;
});
await Test("invalid quota bucket cannot discard another valid quota", () =>
{
    var snapshot = UsageSnapshot.Parse(Json("""{"rateLimitsByLimitId":{"unavailable":null,"codex":{"primary":{"usedPercent":40,"windowDurationMins":90}},"bad":"unknown"}}"""), Json("{}"));
    Check(snapshot.Buckets.Single().Windows.Single().Remaining == 60); return Task.CompletedTask;
});
await Test("quota duration label does not truncate partial hours", () => { Check(new QuotaWindow { DurationMinutes = 90 }.Label.Contains("30")); Check(new QuotaWindow { DurationMinutes = -4 }.Label == "用量周期"); return Task.CompletedTask; });
await Test("profile path traversal is rejected", async () => { var store = new AccountStore(Path.Combine(testRoot, "paths")); await Throws<ArgumentException>(() => Task.Run(() => store.ProfilePath("../../outside"))); });
await Test("physical display bounds handle a removed secondary monitor", () =>
{
    Check(ScreenLayout.EnsureVisible(new(2400, 200, 690, 600), [new(0, 0, 1920, 1040)]) == new PixelRect(1230, 200, 690, 600)); return Task.CompletedTask;
});
await Test("negative display origins do not push a visible window onto another monitor", () =>
{
    var window = new PixelRect(-1600, 120, 600, 800); Check(ScreenLayout.EnsureVisible(window, [new(0, 0, 1920, 1040), new(-1920, 0, 1920, 1040)]) == window); return Task.CompletedTask;
});
await Test("physical scaled windows are kept entirely within the chosen work area", () =>
{
    Check(ScreenLayout.EnsureVisible(new(1400, 800, 690, 900), [new(0, 0, 1920, 1040)]) == new PixelRect(1230, 140, 690, 900)); return Task.CompletedTask;
});
await Test("oversized stored windows fit a smaller replacement display", () =>
{
    Check(ScreenLayout.EnsureVisible(new(80, 40, 1400, 1200), [new(0, 0, 1280, 680)]) == new PixelRect(0, 0, 1280, 680)); return Task.CompletedTask;
});
await Test("extreme persisted coordinates are discarded before creating a native window", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "invalid-placement")); File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"left":1e100,"top":-1e100}""");
    var settings = store.LoadSettings(); Check(settings.Left is null && settings.Top is null); return Task.CompletedTask;
});
await Test("legacy accounts migrate without resurrecting removed entries", () =>
{
    var root = Path.Combine(testRoot, "legacy"); Directory.CreateDirectory(root); var id = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(root, "accounts.json"), "[{\"id\":\"" + id + "\",\"name\":\"旧账号\"}]");
    var store = new AccountStore(root); Check(store.LoadAccounts().Single().Id == id); store.SaveAccounts([]); Check(store.LoadAccounts().Count == 0); return Task.CompletedTask;
});
await Test("atomic replacement recovers previous valid backup", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "recovery")); var a = new AccountRecord { Name = "first" }; store.SaveAccounts([a]); a.Name = "second"; store.SaveAccounts([a]); File.WriteAllText(Path.Combine(store.Root, "accounts-v2.json"), "broken");
    Check(store.LoadAccounts().Single().Name == "first"); Check(store.RecoveryNotice is not null); return Task.CompletedTask;
});
await Test("saving after recovery keeps a usable backup for a second recovery", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "second-recovery")); var original = new AccountRecord { Name = "original" }; store.SaveAccounts([original]);
    store.SaveAccounts([new() { Name = "later" }]); var path = Path.Combine(store.Root, "accounts-v2.json"); File.WriteAllText(path, "broken");
    var recovered = store.LoadAccounts(); Check(recovered.Single().Id == original.Id); store.SaveAccounts(recovered); File.WriteAllText(path, "broken again");
    Check(new AccountStore(store.Root).LoadAccounts().Single().Id == original.Id); return Task.CompletedTask;
});
await Test("damaged modern account list never resurrects legacy deleted accounts", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "no-resurrection")); File.WriteAllText(Path.Combine(store.Root, "accounts.json"), JsonSerializer.Serialize(new[] { new AccountRecord { Name = "deleted" } }));
    store.SaveAccounts([]); File.WriteAllText(Path.Combine(store.Root, "accounts-v2.json"), "broken"); Check(store.LoadAccounts().Count == 0); Check(store.RecoveryNotice is not null); return Task.CompletedTask;
});
await Test("unreadable or damaged settings can retain their valid recovery copy", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "settings-recovery")); store.SaveSettings(new() { RefreshMinutes = 15 }); store.SaveSettings(new() { RefreshMinutes = 30 });
    var path = Path.Combine(store.Root, "settings.json"); File.WriteAllText(path, "null"); var recovered = store.LoadSettings(); Check(recovered.RefreshMinutes == 15); store.SaveSettings(recovered);
    File.WriteAllText(path, "null"); Check(new AccountStore(store.Root).LoadSettings().RefreshMinutes == 15); return Task.CompletedTask;
});
await Test("failed refresh never overwrites successful cache", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "cache")); var snapshot = new UsageSnapshot { Email = "cache@example.com" }; store.SaveCache("current", snapshot); Check(store.LoadCache("current")?.Email == snapshot.Email); return Task.CompletedTask;
});
await Test("profile credentials are restricted to current user", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "acl")); var id = Guid.NewGuid().ToString("N"); var path = store.CreateProfile(id); Check(File.ReadAllText(Path.Combine(path, "config.toml")).Contains("\"file\""));
    if (OperatingSystem.IsWindows()) Check(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected); store.DeleteProfile(id); Check(!Directory.Exists(path)); return Task.CompletedTask;
});
await Test("RPC ignores malformed output and initializes normally", async () => { await using var client = Client(); await client.InitializeAsync(default); var result = await client.RequestAsync("echo", new { value = 7 }, default); Check(result.GetProperty("value").GetInt32() == 7); });
await Test("out-of-order responses are matched to their requests", async () =>
{
    await using var client = Client(); await client.InitializeAsync(default);
    var slow = client.RequestAsync("delay", new { value = 1, ms = 150 }, default); var fast = client.RequestAsync("delay", new { value = 2, ms = 5 }, default);
    Check((await fast).GetProperty("value").GetInt32() == 2); Check((await slow).GetProperty("value").GetInt32() == 1);
});
await Test("RPC errors never expose upstream token text", async () => { await using var client = Client(); try { await client.RequestAsync("error", null, default); throw new Exception("Expected error"); } catch (CodexException e) { Check(!e.Message.Contains("secret-token")); } });
await Test("timeout is bounded and client remains usable", async () => { await using var client = Client(); await Throws<CodexException>(() => client.RequestAsync("delay", new { ms = 500 }, default, TimeSpan.FromMilliseconds(70))); Check((await client.RequestAsync("echo", new { value = 3 }, default)).GetProperty("value").GetInt32() == 3); });
await Test("cancellation does not contaminate next request", async () => { await using var client = Client(); using var cancel = new CancellationTokenSource(70); await Throws<OperationCanceledException>(() => client.RequestAsync("delay", new { ms = 400 }, cancel.Token)); Check((await client.RequestAsync("echo", new { value = 4 }, default)).GetProperty("value").GetInt32() == 4); });
await Test("server exit promptly fails a pending request", async () => { await using var client = Client(); await Throws<CodexException>(() => client.RequestAsync("exit", null, default, TimeSpan.FromSeconds(2))); });
await Test("disposal cancels active requests and closes the reader", async () => { var client = Client(); var pending = client.RequestAsync("delay", new { ms = 5000 }, default); await client.DisposeAsync(); await Throws<OperationCanceledException>(() => pending); Check(client.Completion.IsCompleted); });
await Test("isolated account gets own home and no API-key override", async () =>
{
    var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY"); Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-secret");
    try { await using var client = Client(profile: testRoot); var env = await client.RequestAsync("env", null, default); Check(env.Str("profile") == testRoot); Check(env.Str("key") is null); } finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
});
await Test("usage service reads identity and quota through actual RPC", async () => { var store = new AccountStore(Path.Combine(testRoot, "service")); var service = new CodexService(store, (_, profile) => Client(profile: profile)); var snapshot = await service.RefreshAsync("fake", new() { Id = "current" }, default); Check(snapshot.Email == "test@example.com"); Check(snapshot.Buckets[0].Windows[0].Remaining == 73); });
await Test("browser login completion follows official RPC notifications", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "login")); var id = Guid.NewGuid().ToString("N"); store.CreateProfile(id); var opened = false;
    var service = new CodexService(store, (_, profile) => Client(profile: profile), uri => opened = uri.Host == "auth.openai.com");
    Check(await service.LoginAsync("fake", id, _ => { }, default) == "test@example.com"); Check(opened); store.DeleteProfile(id);
});
await Test("untrusted login URL is rejected before opening browser", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "evil")); var opened = false; var service = new CodexService(store, (_, profile) => Client("evil", profile), _ => opened = true);
    await Throws<CodexException>(() => service.LoginAsync("fake", Guid.NewGuid().ToString("N"), _ => { }, default)); Check(!opened);
});
await Test("login cancellation closes the live protocol process", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "cancel-login")); var service = new CodexService(store, (_, profile) => Client("no-login", profile), _ => { }); using var cancel = new CancellationTokenSource(150);
    await Throws<OperationCanceledException>(() => service.LoginAsync("fake", Guid.NewGuid().ToString("N"), _ => { }, cancel.Token));
});
await Test("login process exit is detected immediately", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "login-exit")); var service = new CodexService(store, (_, profile) => Client("login-exit", profile), _ => { });
    await Throws<CodexException>(() => service.LoginAsync("fake", Guid.NewGuid().ToString("N"), _ => { }, default).WaitAsync(TimeSpan.FromSeconds(3)));
});
await Test("foreign login notification is rejected", async () =>
{
    var service = new CodexService(new AccountStore(Path.Combine(testRoot, "foreign-login")), (_, profile) => Client("foreign-login", profile), _ => { });
    await Throws<CodexException>(() => service.LoginAsync("fake", Guid.NewGuid().ToString("N"), _ => { }, default));
});
await Test("malformed post-login account data is a recoverable authentication error", async () =>
{
    var service = new CodexService(new AccountStore(Path.Combine(testRoot, "malformed-login")), (_, profile) => Client("malformed-login", profile), _ => { });
    await Throws<CodexException>(() => service.LoginAsync("fake", Guid.NewGuid().ToString("N"), _ => { }, default));
});
await Test("different accounts read independent profiles concurrently", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "multi")); var one = new AccountRecord(); var two = new AccountRecord(); store.CreateProfile(one.Id); store.CreateProfile(two.Id);
    var service = new CodexService(store, (_, profile) => Client("per-profile", profile));
    var snapshots = await Task.WhenAll(service.RefreshAsync("fake", one, default), service.RefreshAsync("fake", two, default));
    Check(snapshots[0].AccountId == one.Id && snapshots[1].AccountId == two.Id); Check(snapshots[0].Email != snapshots[1].Email);
});
await Test("unknown and API-key auth states are explicit errors", async () =>
{
    await using var loggedOut = Client("logged-out"); await Throws<CodexException>(() => loggedOut.ReadUsageAsync(default));
    await using var apiKey = Client("api-key"); await Throws<CodexException>(() => apiKey.ReadUsageAsync(default));
});
await Test("missing managed profile is not silently recreated", async () =>
{
    var service = new CodexService(new AccountStore(Path.Combine(testRoot, "missing")), (_, profile) => Client(profile: profile));
    await Throws<CodexException>(() => service.RefreshAsync("fake", new AccountRecord(), default));
});
await Test("account switch during quota request cannot mix identities", async () => { await using var client = Client("switched-account"); await Throws<CodexException>(() => client.ReadUsageAsync(default)); });
await Test("unusually shaped cached data is sanitized", () =>
{
    var root = Path.Combine(testRoot, "shape"); var store = new AccountStore(root); Directory.CreateDirectory(Path.Combine(root, "cache"));
    File.WriteAllText(Path.Combine(root, "cache/current.json"), """{"fetchedAt":"2026-11-10T00:00:00Z","buckets":[null,{"name":null,"windows":[null,{"usedPercent":15}]}]}""");
    Check(store.LoadCache("current")?.Buckets.Single().Windows.Single().Remaining == 85); return Task.CompletedTask;
});
await Test("cache without a real fetch time cannot masquerade as freshly synchronized", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "cache-time")); Directory.CreateDirectory(Path.Combine(store.Root, "cache"));
    File.WriteAllText(Path.Combine(store.Root, "cache/current.json"), """{"buckets":[{"windows":[{"usedPercent":10}]}]}""");
    Check(store.LoadCache("current") is null); return Task.CompletedTask;
});
await Test("CC Switch stores deduplicate by subject and workspace instead of email", () =>
{
    var root = Path.Combine(testRoot, "cc-identity"); Directory.CreateDirectory(root);
    var first = IdentityToken("one", "workspace-a"); var second = IdentityToken("two", "workspace-a");
    File.WriteAllText(Path.Combine(root, "codex_oauth_auth.json"), JsonSerializer.Serialize(new { accounts = new { one = new { id_token = first, refresh_token = "fake-refresh-one", chatgpt_account_id = "workspace-a", email = "same@example.com", token_updated_at_ms = 1 }, two = new { id_token = second, refresh_token = "fake-refresh-two", chatgpt_account_id = "workspace-a", email = "same@example.com", token_updated_at_ms = 1 } } }));
    File.WriteAllText(Path.Combine(root, "codex-login-stash.json"), JsonSerializer.Serialize(new { logins = new { first = NativeAuth(first, "workspace-a") } }));
    var scan = CcSwitchImport.Scan(root); Check(scan.Accounts.Count == 2); Check(scan.Accounts.Count(account => account.HasAccessToken) == 1); return Task.CompletedTask;
});
await Test("managed CC Switch import creates native Codex auth and preserves source bytes", () =>
{
    var source = Path.Combine(testRoot, "cc-managed.json"); var identity = IdentityToken("managed", "workspace-b");
    var original = JsonSerializer.Serialize(new { accounts = new { one = new { id_token = identity, refresh_token = "fake-refresh", token_updated_at_ms = 100 } } }); File.WriteAllText(source, original);
    var scan = CcSwitchImport.Scan(source); Check(scan.Accounts.Count == 1 && !scan.Accounts[0].HasAccessToken);
    var store = new AccountStore(Path.Combine(testRoot, "cc-import")); var records = CcSwitchImport.Import(store, [], scan.Accounts);
    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(store.ProfilePath(records.Single().Id), "auth.json")));
    var auth = doc.RootElement; Check(auth.Str("auth_mode") == "chatgpt"); Check(auth.GetProperty("tokens").Str("account_id") == "workspace-b"); Check(auth.GetProperty("tokens").Str("refresh_token") == "fake-refresh");
    Check(DateTimeOffset.Parse(auth.Str("last_refresh")!) == DateTimeOffset.UnixEpoch); Check(File.ReadAllText(source) == original); Check(store.LoadAccounts().Single().ImportKey == scan.Accounts[0].Key);
    Check(!JsonSerializer.Serialize(scan).Contains("fake-refresh")); return Task.CompletedTask;
});
await Test("CC Switch repeated import does not create duplicate profiles", () =>
{
    var source = Path.Combine(testRoot, "cc-native.json"); File.WriteAllText(source, JsonSerializer.Serialize(NativeAuth(IdentityToken("native", "workspace-c"), "workspace-c")));
    var scan = CcSwitchImport.Scan(source); var store = new AccountStore(Path.Combine(testRoot, "cc-repeat")); Check(CcSwitchImport.Import(store, [], scan.Accounts).Count == 1);
    Check(CcSwitchImport.Import(store, store.LoadAccounts(), scan.Accounts).Count == 0); Check(Directory.GetDirectories(Path.Combine(store.Root, "profiles")).Length == 1); return Task.CompletedTask;
});
await Test("CC Switch failed commit cleans up newly created credential profiles", async () =>
{
    var source = Path.Combine(testRoot, "cc-rollback.json"); File.WriteAllText(source, JsonSerializer.Serialize(NativeAuth(IdentityToken("rollback", "workspace-d"), "workspace-d")));
    var store = new AccountStore(Path.Combine(testRoot, "cc-rollback")); Directory.CreateDirectory(Path.Combine(store.Root, "accounts-v2.json"));
    await Throws<IOException>(() => Task.Run(() => CcSwitchImport.Import(store, [], CcSwitchImport.Scan(source).Accounts)));
    Check(Directory.GetDirectories(Path.Combine(store.Root, "profiles")).Length == 0);
});
await Test("CC Switch unusable credentials provide a notice without leaking token contents", () =>
{
    var source = Path.Combine(testRoot, "cc-invalid.json"); File.WriteAllText(source, """{"accounts":{"one":{"id_token":"not-a-jwt","refresh_token":"never-print-this"}}}""");
    var scan = CcSwitchImport.Scan(source); Check(scan.Accounts.Count == 0 && scan.Notices.Count > 0); Check(!string.Join(" ", scan.Notices).Contains("never-print-this")); return Task.CompletedTask;
});
await Test("missing CC Switch store is actionable", () => { var scan = CcSwitchImport.Scan(Path.Combine(testRoot, "absent.json")); Check(scan.Accounts.Count == 0 && scan.Notices.Count == 1); return Task.CompletedTask; });
await Test("native CC Switch records report invalid credentials while retaining valid imports", () =>
{
    var source = Path.Combine(testRoot, "cc-native-invalid.json");
    var invalid = new { tokens = new { id_token = "invalid", refresh_token = "private-test-refresh" } };
    File.WriteAllText(source, JsonSerializer.Serialize(invalid));
    var single = CcSwitchImport.Scan(source); Check(single.Accounts.Count == 0 && single.Notices.Count == 1);
    File.WriteAllText(source, JsonSerializer.Serialize(new { logins = new { invalid, valid = NativeAuth(IdentityToken("valid", "workspace"), "workspace") } }));
    var mixed = CcSwitchImport.Scan(source); Check(mixed.Accounts.Count == 1 && mixed.Notices.Count == 1);
    Check(!string.Join(" ", mixed.Notices).Contains("private-test-refresh")); return Task.CompletedTask;
});
await Test("explicit quoted executable paths are normalized", () => { var located = CliLocator.Find("\"" + Environment.ProcessPath + "\""); Check(located == Environment.ProcessPath); return Task.CompletedTask; });
await Test("stale executable selection falls back to the current installed Codex", () => { var actual = CliLocator.Find(); if (actual is not null) Check(CliLocator.Find(Path.Combine(testRoot, "removed-codex.exe")) is not null); return Task.CompletedTask; });
await Test("removed desktop widget migrates to independent main and taskbar layouts", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "simplified-settings"));
    File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"LayoutVersion":4,"DesktopWidgetEnabled":true,"Width":560,"Height":350,"SimplifiedLayout":"unknown"}""");
    var settings = store.LoadSettings();
    Check(!settings.DesktopWidgetEnabled && settings.LayoutVersion == 5 && settings.SimplifiedLayout == "ring");
    Check(settings.Width == 560 && settings.Height == 350 && !settings.SimplifiedMode);
    settings.SimplifiedMode = true; settings.SimplifiedLayout = "bar"; settings.DetailedTaskbar = false;
    store.SaveSettings(settings); settings = store.LoadSettings();
    Check(settings.SimplifiedMode && settings.SimplifiedLayout == "bar" && !settings.DetailedTaskbar && settings.Width == 560 && settings.Height == 350);
    settings.DetailedTaskbar = true; settings.SimplifiedLayout = "ring"; store.SaveSettings(settings);
    settings = store.LoadSettings(); Check(settings.DetailedTaskbar && settings.SimplifiedLayout == "ring");
    return Task.CompletedTask;
});
await Test("palette and panel material migrate safely and persist independently", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "appearance-settings"));
    File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"Theme":"light","Palette":"old-theme","Material":"unknown"}""");
    var settings = store.LoadSettings(); Check(settings.Theme == "light" && settings.Palette == "mint" && settings.Material == "solid");
    foreach (var palette in new[] { "mint", "ocean", "violet", "amber" })
    {
        settings.Palette = palette; settings.Material = "glass"; store.SaveSettings(settings);
        var saved = store.LoadSettings(); Check(saved.Palette == palette && saved.Material == "glass" && saved.Theme == "light");
    }
    return Task.CompletedTask;
});
await Test("new settings and old settings default to compact with persisted normal dimensions", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "compact-settings"));
    Check(store.LoadSettings().Compact);
    File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"Compact":false,"Width":460,"Height":720}""");
    var settings = store.LoadSettings(); Check(settings.Compact && settings.Width == 400 && settings.Height == 464);
    settings.Compact = false; settings.Width = 560; settings.Height = 316; store.SaveSettings(settings);
    Check(!store.LoadSettings().Compact && store.LoadSettings().Height == 316); return Task.CompletedTask;
});
await Test("switch entry returns the committed quota identity and retains the selected credentials", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "switch-entry"));
    var target = new AccountRecord(); var profile = store.CreateProfile(target.Id);
    var selected = JsonSerializer.Serialize(NativeAuth("selected", target.Id));
    File.WriteAllText(Path.Combine(profile, "auth.json"), selected);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    try
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(store.Root, "current-home"));
        var result = await new AccountSwitcher(store).SwitchAsync(Environment.ProcessPath!, target, default);
        Check(result?.AccountId == target.Id && result.Buckets.Single().Windows.Single().Remaining == 73);
        Check(File.ReadAllText(Path.Combine(AccountSwitcher.CurrentHome, "auth.json")) == selected);
        Check(File.ReadAllText(Path.Combine(profile, "auth.json")) == selected);
    }
    finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
});
await Test("switching the current source preserves the existing no-op behavior", async () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "switch-current-entry"));
    Check(await new AccountSwitcher(store).SwitchAsync("missing-cli", new() { Id = "current" }, default) is null);
    Check(!Directory.EnumerateFileSystemEntries(store.Root).Any());
});
await Test("native switching preserves previous authentication and commits the selected workspace", async () =>
{
    var root = Path.Combine(testRoot, "native-switch"); Directory.CreateDirectory(root);
    var home = Path.Combine(root, "home"); Directory.CreateDirectory(home);
    var source = Path.Combine(root, "source.json");
    var oldAuth = JsonSerializer.Serialize(NativeAuth("previous", "old-workspace"));
    var newAuth = JsonSerializer.Serialize(NativeAuth("selected", "new-workspace"));
    File.WriteAllText(Path.Combine(home, "auth.json"), oldAuth); File.WriteAllText(source, newAuth);
    await AccountSwitcher.CommitAuthenticationAsync(source, home, Path.Combine(root, "backup"), "new-workspace", default);
    Check(File.ReadAllText(Path.Combine(home, "auth.json")) == newAuth);
    Check(File.ReadAllText(Path.Combine(root, "backup", "previous-codex-auth.json")) == oldAuth);
    Check(File.ReadAllText(source) == newAuth);
    await Throws<CodexException>(() => AccountSwitcher.CommitAuthenticationAsync(source, home, Path.Combine(root, "backup"), "wrong-workspace", default));
    Check(File.ReadAllText(Path.Combine(home, "auth.json")) == newAuth);
});
await Test("invalid and canceled switching retain current authentication", async () =>
{
    var root = Path.Combine(testRoot, "invalid-switch"); Directory.CreateDirectory(root);
    var home = Path.Combine(root, "home"); Directory.CreateDirectory(home);
    var target = Path.Combine(home, "auth.json"); File.WriteAllText(target, "previous");
    var source = Path.Combine(root, "source.json"); File.WriteAllText(source, "{}");
    await Throws<CodexException>(() => AccountSwitcher.CommitAuthenticationAsync(source, home, root, null, default));
    File.WriteAllText(source, JsonSerializer.Serialize(NativeAuth("selected", "workspace")));
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => AccountSwitcher.CommitAuthenticationAsync(source, home, root, "workspace", canceled.Token));
    Check(File.ReadAllText(target) == "previous" && !Directory.GetFiles(home, "*.tmp").Any());
});
await Test("native switch respects another authentication writer", async () =>
{
    var root = Path.Combine(testRoot, "locked-switch"); Directory.CreateDirectory(root);
    var source = Path.Combine(root, "source.json"); File.WriteAllText(source, JsonSerializer.Serialize(NativeAuth("selected", "workspace")));
    var target = Path.Combine(root, "auth.json"); File.WriteAllText(target, "previous");
    using var held = new FileStream(Path.Combine(root, "auth.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await Throws<IOException>(() => AccountSwitcher.CommitAuthenticationAsync(source, root, Path.Combine(root, "backup"), "workspace", default));
    Check(File.ReadAllText(target) == "previous");
});
await Test("quota notifications deduplicate low and exhausted events and allow a new reset cycle", () =>
{
    var root = Path.Combine(testRoot, "alerts"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings { NotifyLowQuota = true, LowQuotaThreshold = 5 };
    var now = DateTimeOffset.UtcNow; var reset = now.AddMinutes(-1).ToUnixTimeSeconds();
    Check(engine.Evaluate([new("a", "weekly", 4, reset)], settings, now).Single().Kind == "low");
    Check(engine.Evaluate([new("a", "weekly", 4, reset)], settings, now).Count == 0);
    Check(engine.Evaluate([new("a", "weekly", 0, reset)], settings, now).Single().Kind == "exhausted");
    Check(new QuotaAlerts(root).Evaluate([new("a", "weekly", 0, reset)], settings, now).Count == 0);
    Check(engine.Evaluate([new("a", "weekly", 100, now.AddDays(7).ToUnixTimeSeconds())], settings, now).Single().Kind == "reset");
    Check(engine.Evaluate([new("a", "weekly", 3, now.AddDays(7).ToUnixTimeSeconds())], settings, now).Single().Kind == "low");
    return Task.CompletedTask;
});
await Test("quota decreases without a due reset never emit a reset notification", () =>
{
    var root = Path.Combine(testRoot, "no-false-reset"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow;
    engine.Evaluate([new("a", "w", 40, now.AddDays(1).ToUnixTimeSeconds())], settings, now);
    Check(engine.Evaluate([new("a", "w", 60, now.AddDays(1).ToUnixTimeSeconds())], settings, now).Count == 0);
    return Task.CompletedTask;
});
await Test("reset card reminders fire at configured thresholds and on expiry only once", () =>
{
    var root = Path.Combine(testRoot, "expiry-alerts"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var now = DateTimeOffset.UtcNow;
    var settings = new WidgetSettings { CardReminderDays = "3,1", CardExpirations = new() { ["a"] = now.AddDays(2) } };
    Check(engine.Evaluate([], settings, now).Single().Kind == "expiry");
    Check(engine.Evaluate([], settings, now).Count == 0);
    Check(engine.Evaluate([], settings, now.AddDays(1.5)).Single().Kind == "expiry");
    Check(engine.Evaluate([], settings, now.AddDays(2.1)).Single().Kind == "expired");
    Check(new QuotaAlerts(root).Evaluate([], settings, now.AddDays(3)).Count == 0);
    return Task.CompletedTask;
});
await Test("enabling low quota alerts while already below the threshold sends one notification", () =>
{
    var root = Path.Combine(testRoot, "enable-alerts"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings { NotifyLowQuota = false };
    var now = DateTimeOffset.UtcNow;
    Check(engine.Evaluate([new("a", "w", 4, null)], settings, now).Count == 0);
    settings.NotifyLowQuota = true;
    Check(engine.Evaluate([new("a", "w", 4, null)], settings, now).Single().Kind == "low");
    Check(engine.Evaluate([new("a", "w", 4, null)], settings, now).Count == 0);
    return Task.CompletedTask;
});
await Test("CC Switch names survive import and sync preserves manually edited names", () =>
{
    var root = Path.Combine(testRoot, "sync-names"); Directory.CreateDirectory(root);
    var source = Path.Combine(root, "native.json");
    var token = IdentityToken("named-user", "named-workspace");
    var auth = JsonSerializer.SerializeToElement(NativeAuth(token, "named-workspace"));
    var native = System.Text.Json.Nodes.JsonNode.Parse(auth.GetRawText())!; native["name"] = "Original name"; File.WriteAllText(source, native.ToJsonString());
    var store = new AccountStore(Path.Combine(root, "data")); var scan = CcSwitchImport.Scan(source);
    Check(scan.Accounts.Single().Name == "Original name");
    var original = CcSwitchImport.Import(store, [], scan.Accounts).Single(); Check(original.Name == "Original name");
    original.Name = "My alias"; original.CustomName = true; store.SaveAccounts([original]);
    var synced = CcSwitchImport.Synchronize(store, [original], scan.Accounts).Single();
    Check(synced.Name == "My alias" && synced.CustomName && synced.Id != original.Id && !Directory.Exists(store.ProfilePath(original.Id)));
    return Task.CompletedTask;
});
await Test("taskbar strip stays within a 150-percent horizontal taskbar", () =>
{
    var strip = TaskbarLayout.Place(new(0, 1528, 2560, 72), 1.5, 8);
    Check(strip == new PixelRect(12, 1534, 66, 60)); return Task.CompletedTask;
});
await Test("automatic centered placement follows the actual Widgets edge", () =>
{
    var bar = new PixelRect(0, 1528, 2560, 72); var widgets = new PixelRect(9, 1528, 228, 72);
    var result = TaskbarLayout.FindAutomaticSpace(bar, 1.5, false, widgets, new(1800, 1528, 760, 72), [widgets, new(600, 1528, 1100, 72)]);
    Check(result == new PixelRect(249, 1534, 66, 60)); return Task.CompletedTask;
});
await Test("full taskbar tile matches the weather footprint at 150-percent scaling", () =>
{
    var bar = new PixelRect(0, 1528, 2560, 72); var weather = new PixelRect(9, 1528, 228, 72);
    var full = TaskbarLayout.FindAutomaticSpace(bar, 1.5, false, weather, new(1800, 1528, 760, 72), [weather, new(600, 1528, 1100, 72)], desiredWidth: 152, fillHeight: true);
    Check(full == new PixelRect(249, 1528, weather.Width, weather.Height)); return Task.CompletedTask;
});
await Test("full taskbar tile keeps automatic left and notification-area placement", () =>
{
    var bar = new PixelRect(0, 1528, 2560, 72); var tray = new PixelRect(1800, 1528, 760, 72);
    Check(TaskbarLayout.FindAutomaticSpace(bar, 1.5, false, null, tray, [new(600, 1528, 1100, 72), tray], desiredWidth: 152, fillHeight: true) == new PixelRect(0, 1528, 228, 72));
    Check(TaskbarLayout.FindAutomaticSpace(bar, 1.5, true, null, tray, [new(0, 1528, 1100, 72), tray], desiredWidth: 152, fillHeight: true) == new PixelRect(1560, 1528, 228, 72));
    return Task.CompletedTask;
});
await Test("full taskbar tile fits narrow taskbars and respects occupied space", () =>
{
    Check(TaskbarLayout.Place(new(0, 0, 100, 48), 1, 0, desiredWidth: 152, fillHeight: true) == new PixelRect(0, 0, 100, 48));
    Check(TaskbarLayout.FindAutomaticSpace(new(0, 0, 400, 48), 1, false, null, null, [new(100, 0, 300, 48)], desiredWidth: 152, fillHeight: true) is null);
    return Task.CompletedTask;
});
await Test("full taskbar preference persists while existing settings retain compact mode", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "full-taskbar-settings"));
    Check(!store.LoadSettings().DetailedTaskbar);
    var settings = store.LoadSettings(); settings.DetailedTaskbar = true; store.SaveSettings(settings);
    Check(store.LoadSettings().DetailedTaskbar); return Task.CompletedTask;
});
await Test("automatic placement reaches the taskbar left edge when Widgets is hidden", () =>
{
    var result = TaskbarLayout.FindAutomaticSpace(new(0, 1528, 2560, 72), 1.5, false, null, new(1800, 1528, 760, 72), [new(600, 1528, 1100, 72), new(1800, 1528, 760, 72)]);
    Check(result == new PixelRect(0, 1534, 66, 60)); return Task.CompletedTask;
});
await Test("left-aligned Start places the quota ring next to the notification area", () =>
{
    var bar = new PixelRect(0, 1528, 2560, 72); var tray = new PixelRect(1800, 1528, 760, 72);
    var result = TaskbarLayout.FindAutomaticSpace(bar, 1.5, true, new(0, 1528, 228, 72), tray, [new(0, 1528, 1100, 72), tray]);
    Check(result == new PixelRect(1722, 1534, 66, 60)); return Task.CompletedTask;
});
await Test("automatic right placement avoids tray buttons and handles negative monitor origins", () =>
{
    var bar = new PixelRect(-1920, 0, 1920, 48); var tray = new PixelRect(-300, 0, 300, 48);
    var result = TaskbarLayout.FindAutomaticSpace(bar, 1, true, null, tray, [new(-400, 0, 80, 48), tray, new(-1920, 0, 1000, 48)]);
    Check(result == new PixelRect(-452, 4, 44, 40)); return Task.CompletedTask;
});
await Test("automatic placement safely handles missing tray metadata and crowded taskbars", () =>
{
    Check(TaskbarLayout.FindAutomaticSpace(new(0, 0, 1920, 48), 1, true, null, null, [new(0, 0, 900, 48), new(1800, 0, 120, 48)]) == new PixelRect(1748, 4, 44, 40));
    Check(TaskbarLayout.FindAutomaticSpace(new(0, 0, 400, 48), 1, true, null, new(300, 0, 100, 48), [new(0, 0, 300, 48), new(300, 0, 100, 48)]) is null);
    return Task.CompletedTask;
});
await Test("taskbar strip positions support top bars and negative monitor origins", () =>
{
    Check(TaskbarLayout.Place(new(-1920, 0, 1920, 48), 1, 8) == new PixelRect(-1912, 4, 44, 40));
    Check(TaskbarLayout.Place(new(0, 0, 1920, 48), 1, 8).Top == 4); return Task.CompletedTask;
});
await Test("taskbar strip clamps offsets and narrow workspaces", () =>
{
    var strip = TaskbarLayout.Place(new(0, 0, 200, 48), 1, 9999); Check(strip.Left == 156 && strip.Right == 200);
    strip = TaskbarLayout.Place(new(0, 0, 120, 48), 1, -30); Check(strip.Left == 0 && strip.Width == 44); return Task.CompletedTask;
});
await Test("taskbar strip finds space after weather without covering centered buttons", () =>
{
    var strip = TaskbarLayout.FindSpace(new(0, 1528, 2560, 72), 1.5, 8, [new(12, 1528, 240, 72), new(900, 1528, 1200, 72)]);
    Check(strip == new PixelRect(264, 1534, 66, 60)); return Task.CompletedTask;
});
await Test("taskbar strip skips overlapping obstacles independent of their order", () =>
{
    var strip = TaskbarLayout.FindSpace(new(-1920, 0, 1920, 48), 1, 8, [new(-1740, 0, 100, 48), new(-1920, 0, 200, 48), new(-1920, 100, 1000, 50)]);
    Check(strip == new PixelRect(-1632, 4, 44, 40)); return Task.CompletedTask;
});
await Test("taskbar strip refuses occupied bars and offsets without enough free space", () =>
{
    Check(TaskbarLayout.FindSpace(new(0, 0, 400, 48), 1, 8, [new(0, 0, 400, 48)]) is null);
    Check(TaskbarLayout.FindSpace(new(0, 0, 400, 48), 1, 999, [new(350, 0, 50, 48)]) is null);
    Check(TaskbarLayout.FindSpace(new(0, 0, 400, 48), 1, 8, []) == new PixelRect(8, 4, 44, 40));
    return Task.CompletedTask;
});
await Test("fullscreen suppression distinguishes fullscreen from maximized windows", () =>
{
    Check(TaskbarLayout.CoversMonitor(new(0, 0, 2560, 1600), new(0, 0, 2560, 1600)));
    Check(TaskbarLayout.CoversMonitor(new(-8, -8, 2576, 1616), new(0, 0, 2560, 1600)));
    Check(!TaskbarLayout.CoversMonitor(new(-8, -8, 2576, 1544), new(0, 0, 2560, 1600)));
    Check(!TaskbarLayout.CoversMonitor(new(0, 0, 1920, 1080), new(-1920, 0, 1920, 1080)));
    Check(TaskbarLayout.CoversMonitor(new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1080)));
    return Task.CompletedTask;
});
await Test("taskbar flyout stays above a bottom bar at 150-percent scaling", () =>
{
    var flyout = TaskbarLayout.PlaceFlyout(new(0, 1528, 2560, 72), new(249, 1534, 276, 60), new(0, 0, 2560, 1528), 1.5, 328);
    Check(flyout == new PixelRect(249, 1024, 498, 492)); return Task.CompletedTask;
});
await Test("taskbar flyout fits small high-DPI monitors and the right edge", () =>
{
    var flyout = TaskbarLayout.PlaceFlyout(new(0, 936, 1280, 144), new(1000, 948, 280, 120), new(0, 0, 1280, 936), 3, 458);
    Check(flyout == new PixelRect(284, 24, 996, 888)); return Task.CompletedTask;
});
await Test("taskbar flyout opens below top bars on monitors with negative origins", () =>
{
    var flyout = TaskbarLayout.PlaceFlyout(new(-1920, -1080, 1920, 48), new(-1912, -1076, 184, 40), new(-1920, -1032, 1920, 1032), 1, 328);
    Check(flyout == new PixelRect(-1912, -1024, 332, 328)); return Task.CompletedTask;
});
await Test("taskbar flyout keeps a usable rectangle in very narrow work areas", () =>
{
    var flyout = TaskbarLayout.PlaceFlyout(new(0, 101, 100, 48), new(8, 105, 92, 40), new(0, 0, 100, 101), 1, 328);
    Check(flyout == new PixelRect(0, 8, 100, 85));
    Check(TaskbarLayout.PlaceFlyout(new(0, 1, 1, 1), new(0, 1, 1, 1), new(0, 0, 1, 1), 1, 328) == new PixelRect(0, 0, 1, 1));
    return Task.CompletedTask;
});
await Test("Codex session lifecycle distinguishes working, completed, paused and terminal errors", () =>
{
    var session = new CodexSessionActivity();
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"one"}}""")); Check(session.Kind == CodexActivityKind.Working);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"old"}}""")); Check(session.Kind == CodexActivityKind.Working);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"one"}}""")); Check(session.Kind == CodexActivityKind.Completed);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"two"}}"""));
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"two","error":{"message":"failure"}}}""")); Check(session.Kind == CodexActivityKind.Error);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"three"}}"""));
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"turn_aborted","turn_id":"three"}}""")); Check(session.Kind == CodexActivityKind.Paused);
    session.Apply(Json("[]")); session.Apply(Json("""{"type":"event_msg","payload":null}""")); Check(session.Kind == CodexActivityKind.Paused);
    return Task.CompletedTask;
});
await Test("synchronous and asynchronous input requests remain waiting until answered", () =>
{
    var session = new CodexSessionActivity();
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input","call_id":"sync"}}""")); Check(session.Kind == CodexActivityKind.NeedsInput);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"sync","output":"{}"}}""")); Check(session.Kind == CodexActivityKind.Working);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"async"}}"""));
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"async","output":"{\"accepted\":true}"}}"""));
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete"}}""")); Check(session.Kind == CodexActivityKind.NeedsInput);
    var first = CodexSessionActivity.Aggregate([session]).AttentionToken;
    Check(CodexSessionActivity.Aggregate([session]).AttentionToken == first);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"message","role":"user"}}""")); Check(session.Kind == CodexActivityKind.Working);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"next"}}"""));
    Check(CodexSessionActivity.Aggregate([session]).AttentionToken != first);
    return Task.CompletedTask;
});
await Test("Codex input approvals and rejected prompts recover without phantom waiting", () =>
{
    var session = new CodexSessionActivity();
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"exec_approval_request","call_id":"approval"}}""")); Check(session.Kind == CodexActivityKind.NeedsInput);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"exec_approval_response","call_id":"approval"}}""")); Check(session.Kind == CodexActivityKind.Working);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"rejected"}}"""));
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"rejected","output":"{\"accepted\":false}"}}""")); Check(session.Kind == CodexActivityKind.Working);
    return Task.CompletedTask;
});
await Test("Codex activity aggregates all tasks and gives waiting and errors priority", () =>
{
    var done = new CodexSessionActivity(); done.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete"}}"""));
    var working = new CodexSessionActivity(); working.Apply(Json("""{"type":"event_msg","payload":{"type":"task_started"}}"""));
    Check(CodexSessionActivity.Aggregate([done, working]).Kind == CodexActivityKind.Working);
    working.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input","call_id":"input"}}"""));
    Check(CodexSessionActivity.Aggregate([done, working]).Kind == CodexActivityKind.NeedsInput);
    done.Apply(Json("""{"type":"event_msg","payload":{"type":"error"}}""")); Check(CodexSessionActivity.Aggregate([done, working]).Kind == CodexActivityKind.Error);
    return Task.CompletedTask;
});
await Test("answering one input request preserves other pending requests", () =>
{
    var session = new CodexSessionActivity();
    foreach (var id in new[] { "first-call", "second-call" }) session.Apply(Json(JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call", name = "request_user_input_async", call_id = id } })));
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage"}}}"""));
    session.Apply(Json("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"<send_user_message_question_reply>first-call</send_user_message_question_reply>"}]}}"""));
    Check(session.Kind == CodexActivityKind.NeedsInput);
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"error"}}"""));
    var snapshot = CodexSessionActivity.Aggregate([session]); Check(snapshot.Kind == CodexActivityKind.Error && snapshot.Waiting == 1);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"<send_user_message_question_reply>second-call</send_user_message_question_reply>"}]}}"""));
    Check(session.Kind == CodexActivityKind.Working);
    return Task.CompletedTask;
});
await Test("terminal Codex states survive late tool outputs and reasoning", () =>
{
    foreach (var terminal in new[] { "task_complete", "turn_aborted" })
    {
        var session = new CodexSessionActivity();
        session.Apply(Json(JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = terminal } })));
        var expected = terminal == "task_complete" ? CodexActivityKind.Completed : CodexActivityKind.Paused;
        session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"late","output":"{}"}}"""));
        session.Apply(Json("""{"type":"response_item","payload":{"type":"reasoning"}}"""));
        session.Apply(Json("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage"}}}"""));
        Check(session.Kind == expected);
        session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_started","turn_id":"next"}}"""));
        Check(session.Kind == CodexActivityKind.Working);
    }
    return Task.CompletedTask;
});
await Test("late rejected input responses preserve completion while removing waiting", () =>
{
    var session = new CodexSessionActivity();
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"input"}}"""));
    session.Apply(Json("""{"type":"event_msg","payload":{"type":"task_complete"}}"""));
    Check(session.Kind == CodexActivityKind.NeedsInput);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"function_call_output","call_id":"input","output":"{\"accepted\":false}"}}"""));
    Check(session.Kind == CodexActivityKind.Completed);
    session.Apply(Json("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"text":"continue"}]}}"""));
    Check(session.Kind == CodexActivityKind.Working);
    return Task.CompletedTask;
});
await Test("Codex activity tails partial records and handles truncation and process restarts", () =>
{
    var home = Path.Combine(testRoot, "activity"); var directory = Path.Combine(home, "sessions"); Directory.CreateDirectory(directory);
    var started = DateTimeOffset.UtcNow.AddMinutes(-1); var timestamp = DateTimeOffset.UtcNow.ToString("O"); var file = Path.Combine(directory, "session.jsonl");
    File.WriteAllText(file, JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "task_started" } }) + "\n");
    var monitor = new CodexActivityMonitor(home); Check(monitor.ReadSince(started).Kind == CodexActivityKind.Working);
    var completed = JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "task_complete" } });
    File.AppendAllText(file, completed[..20]); Check(monitor.ReadSince(started).Kind == CodexActivityKind.Working);
    File.AppendAllText(file, completed[20..] + "\n"); Check(monitor.ReadSince(started).Kind == CodexActivityKind.Completed);
    File.WriteAllText(file, JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "error" } }) + "\n");
    Check(monitor.ReadSince(started).Kind == CodexActivityKind.Error);
    Check(monitor.ReadSince(null).Kind == CodexActivityKind.Offline);
    Check(monitor.ReadSince(DateTimeOffset.UtcNow.AddSeconds(1)).Kind == CodexActivityKind.Error);
    return Task.CompletedTask;
});
await Test("interrupted Codex sessions survive monitor and Codex restarts until resumed", () =>
{
    var home = Path.Combine(testRoot, "interruption-home"); var directory = Path.Combine(home, "sessions"); Directory.CreateDirectory(directory);
    var store = new AccountStore(Path.Combine(testRoot, "interruption-state"));
    var initialStart = DateTimeOffset.UtcNow.AddMinutes(-10); var restart = DateTimeOffset.UtcNow;
    var file = Path.Combine(directory, "session.jsonl");
    string Event(string type, DateTimeOffset time) => JsonSerializer.Serialize(new { timestamp = time.ToString("O"), type = "event_msg", payload = new { type } }) + "\n";
    File.WriteAllText(file, Event("task_started", initialStart.AddMinutes(1)));
    var monitor = new CodexActivityMonitor(home, store);
    Check(monitor.ReadSince(initialStart).Kind == CodexActivityKind.Working);
    Check(new CodexActivityMonitor(home, store).ReadSince(initialStart).Kind == CodexActivityKind.Working);
    monitor = new CodexActivityMonitor(home, store);
    Check(monitor.ReadSince(restart).Kind == CodexActivityKind.Paused);
    Check(new CodexActivityMonitor(home, store).ReadSince(restart).Kind == CodexActivityKind.Paused);
    File.AppendAllText(file, Event("thread_settings_applied", restart.AddMilliseconds(10)));
    Check(monitor.ReadSince(restart).Kind == CodexActivityKind.Paused);
    File.AppendAllText(file, JsonSerializer.Serialize(new { timestamp = restart.AddMilliseconds(20).ToString("O"), type = "response_item", payload = new { type = "message", role = "user" } }) + "\n");
    Check(monitor.ReadSince(restart).Kind == CodexActivityKind.Paused);
    File.AppendAllText(file, Event("task_started", restart.AddSeconds(1)));
    Check(monitor.ReadSince(restart).Kind == CodexActivityKind.Working);
    File.AppendAllText(file, Event("task_complete", restart.AddSeconds(2)));
    Check(monitor.ReadSince(restart).Kind == CodexActivityKind.Completed);
    Check(store.LoadActivityJournal()!.Sessions.Count == 0);
    return Task.CompletedTask;
});
await Test("Codex offline stays offline while persisting interrupted work for the next launch", () =>
{
    var home = Path.Combine(testRoot, "offline-home"); var directory = Path.Combine(home, "sessions"); Directory.CreateDirectory(directory);
    var store = new AccountStore(Path.Combine(testRoot, "offline-state")); var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    File.WriteAllText(Path.Combine(directory, "session.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToString("O"), type = "event_msg", payload = new { type = "task_started" } }) + "\n");
    var monitor = new CodexActivityMonitor(home, store);
    Check(monitor.ReadSince(started).Kind == CodexActivityKind.Working);
    Check(monitor.ReadSince(null).Kind == CodexActivityKind.Offline);
    Check(new CodexActivityMonitor(home, store).ReadSince(null).Kind == CodexActivityKind.Offline);
    Check(new CodexActivityMonitor(home, store).ReadSince(DateTimeOffset.UtcNow.AddSeconds(1)).Kind == CodexActivityKind.Paused);
    return Task.CompletedTask;
});
await Test("failure to persist Codex state preserves the live signal and permits a safe retry", () =>
{
    var home = Path.Combine(testRoot, "journal-failure-home"); Directory.CreateDirectory(Path.Combine(home, "sessions"));
    var store = new AccountStore(Path.Combine(testRoot, "journal-failure-state"));
    var blocker = Path.Combine(store.Root, "activity-state.json"); Directory.CreateDirectory(blocker);
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    File.WriteAllText(Path.Combine(home, "sessions", "session.jsonl"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToString("O"), type = "event_msg", payload = new { type = "task_started" } }) + "\n");
    var monitor = new CodexActivityMonitor(home, store);
    Check(monitor.ReadSince(started).Kind == CodexActivityKind.Working && !monitor.PersistenceHealthy);
    Directory.Delete(blocker);
    Check(monitor.ReadSince(started).Kind == CodexActivityKind.Working && monitor.PersistenceHealthy);
    return Task.CompletedTask;
});
await Test("taskbar typography and semantic accents maintain contrast on light dark and colored taskbars", () =>
{
    var backgrounds = new[] { new RgbColor(245,245,245), new(15,18,22), new(120,120,120), new(40,150,120), new(180,60,75), new(190,155,35) };
    var accents = new[] { new RgbColor(100,201,176), new(219,82,103), new(213,161,62), new(53,181,130), new(130,147,141) };
    foreach (var background in backgrounds)
    {
        Check(ContrastColors.Contrast(ContrastColors.TextOn(background), background) >= 4.5);
        foreach (var accent in accents) Check(ContrastColors.Contrast(ContrastColors.AccentOn(accent, background, 4.5), background) >= 4.5);
    }
    Check(ContrastColors.Contrast(new(0,0,0), new(255,255,255)) == 21);
    var red = ContrastColors.AccentOn(new(219,82,103), new(180,60,75), 4.5); Check(red.Red > red.Green && red.Red > red.Blue);
    var green = ContrastColors.AccentOn(new(53,181,130), new(40,150,120), 4.5); Check(green.Green > green.Red && green.Green > green.Blue);
    return Task.CompletedTask;
});
await Test("reset credit detail parsing distinguishes available, redeemed and non-expiring cards", () =>
{
    var snapshot = UsageSnapshot.Parse(Json("""{"rateLimitResetCredits":{"availableCount":2,"credits":[{"id":"one","status":"available","expiresAt":1900000000},{"id":"two","status":"available","expiresAt":null},{"id":"used","status":"redeemed","expiresAt":1900000000}]}}"""), Json("{}"));
    Check(snapshot.ResetCreditAvailableCount == 2 && snapshot.ResetCreditDetailsKnown && snapshot.ResetCredits.Count == 2);
    Check(snapshot.ResetCredits[0].ExpiresAt == 1900000000 && snapshot.ResetCredits[0].ExpiryKnown);
    Check(snapshot.ResetCredits[1].ExpiresAt is null && snapshot.ResetCredits[1].ExpiryKnown); return Task.CompletedTask;
});
await Test("reset credit counts without details stay distinguishable from an empty detail list", () =>
{
    var missing = UsageSnapshot.Parse(Json("""{"rateLimitResetCredits":{"availableCount":2,"credits":null}}"""), Json("{}"));
    var empty = UsageSnapshot.Parse(Json("""{"rateLimitResetCredits":{"availableCount":0,"credits":[]}}"""), Json("{}"));
    Check(!missing.ResetCreditDetailsKnown && missing.ResetCreditAvailableCount == 2);
    Check(empty.ResetCreditDetailsKnown && empty.ResetCredits.Count == 0); return Task.CompletedTask;
});
await Test("malformed reset credit expiries never become invented deadlines", () =>
{
    var snapshot = UsageSnapshot.Parse(Json("""{"rateLimitResetCredits":{"availableCount":3,"credits":[{"id":"a","status":"available","expiresAt":"invalid"},{"id":"b","status":"available","expiresAt":-1},{"id":"c","status":"available"}]}}"""), Json("{}"));
    Check(snapshot.ResetCredits.Count == 3 && snapshot.ResetCredits.All(credit => !credit.ExpiryKnown && credit.ExpiresAt is null)); return Task.CompletedTask;
});
await Test("account reset card reminders use server deadlines and deduplicate each card", () =>
{
    var root = Path.Combine(testRoot, "server-card-alerts"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var now = DateTimeOffset.UtcNow;
    var settings = new WidgetSettings { CardExpirations = new() { ["old-manual"] = now.AddHours(2) } };
    var cards = new[] { new ResetCreditDeadline("a", "one", now.AddDays(2)), new ResetCreditDeadline("a", "two", now.AddHours(8)) };
    var alert = engine.Evaluate([], settings, now, cards).Single(); Check(alert.AccountKey == "a" && alert.Count == 2 && alert.Kind == "expiry");
    Check(engine.Evaluate([], settings, now, cards).Count == 0);
    Check(engine.Evaluate([], settings, now.AddDays(1.5), cards).Any(alert => alert.Kind == "expiry"));
    Check(new QuotaAlerts(root).Evaluate([], settings, now, []).Count == 0); return Task.CompletedTask;
});
await Test("guide positions round trip across negative monitor origins", () =>
{
    var area = new PixelRect(-1920, -200, 1920, 1040);
    foreach (var point in new[] { (0d, 0d), (0.5, 0.5), (1d, 1d) })
    {
        var placed = WidgetPlacement.Place(area, 280, 280, point.Item1, point.Item2);
        var normalized = WidgetPlacement.Normalize(area, placed);
        Check(Math.Abs(normalized.X - point.Item1) < 0.001 && Math.Abs(normalized.Y - point.Item2) < 0.001);
        Check(placed.Left >= area.Left && placed.Right <= area.Right && placed.Top >= area.Top && placed.Bottom <= area.Bottom);
    }
    return Task.CompletedTask;
});
await Test("guide positions clamp oversized widgets and invalid coordinates", () =>
{
    var area = new PixelRect(10, 20, 100, 80);
    Check(WidgetPlacement.Place(area, 280, 280, -1, 2) == area);
    Check(WidgetPlacement.Place(area, 20, 20, double.NaN, double.PositiveInfinity) == new PixelRect(50, 50, 20, 20));
    var normalized = WidgetPlacement.Normalize(area, area); Check(normalized.X == 0 && normalized.Y == 0);
    return Task.CompletedTask;
});
await Test("guide completion and taskbar preference persist and sanitize", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "guide-settings"));
    Check(store.LoadSettings().GuideVersion == 0 && store.LoadSettings().TaskbarPlacement == "auto");
    store.SaveSettings(new() { GuideVersion = 1, TaskbarPlacement = "right" });
    Check(store.LoadSettings().GuideVersion == 1 && store.LoadSettings().TaskbarPlacement == "right");
    store.SaveSettings(new() { GuideVersion = -1, TaskbarPlacement = "invalid" });
    Check(store.LoadSettings().GuideVersion == 0 && store.LoadSettings().TaskbarPlacement == "auto");
    return Task.CompletedTask;
});
await Test("explicit taskbar side choices avoid weather buttons and tray", () =>
{
    var bar = new PixelRect(0, 1000, 1920, 48);
    var weather = new PixelRect(0, 1000, 120, 48); var tray = new PixelRect(1700, 1000, 220, 48);
    PixelRect[] obstacles = [weather, new(700, 1000, 500, 48), tray];
    var left = TaskbarLayout.FindAutomaticSpace(bar, 1, true, weather, tray, obstacles, placement: "left");
    var right = TaskbarLayout.FindAutomaticSpace(bar, 1, false, weather, tray, obstacles, placement: "right");
    Check(left is { Left: 128 } && right is { Right: 1692 });
    var rightWeather = new PixelRect(1500, 1000, 100, 48);
    Check(TaskbarLayout.FindAutomaticSpace(bar, 1, false, rightWeather, tray, [rightWeather, tray], placement: "left") is { Left: 0 });
    return Task.CompletedTask;
});
await Test("desktop widget positions preserve legacy coordinates and physical pixels", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "widget-physical-position"));
    Check(store.LoadSettings().WidgetPixelLeft is null && store.LoadSettings().WidgetPixelTop is null);
    store.SaveSettings(new() { WidgetLeft = -1150.5, WidgetTop = 40.25, WidgetPixelLeft = -1726, WidgetPixelTop = 60 });
    var settings = store.LoadSettings();
    Check(settings.WidgetLeft == -1150.5 && settings.WidgetTop == 40.25 && settings.WidgetPixelLeft == -1726 && settings.WidgetPixelTop == 60);
    return Task.CompletedTask;
});
await Test("extreme widget positions are discarded without losing other preferences", () =>
{
    var store = new AccountStore(Path.Combine(testRoot, "widget-invalid-position"));
    File.WriteAllText(Path.Combine(store.Root, "settings.json"), """{"Language":"en","WidgetLeft":1e308,"WidgetTop":-1e308,"WidgetPixelLeft":2147483647,"WidgetPixelTop":-2147483648}""");
    var settings = store.LoadSettings();
    Check(settings.Language == "en" && settings.WidgetLeft is null && settings.WidgetTop is null && settings.WidgetPixelLeft is null && settings.WidgetPixelTop is null);
    return Task.CompletedTask;
});
await Test("notification commit failures preserve low and reset events for a retry", async () =>
{
    var root = Path.Combine(testRoot, "alert-retry"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow;
    var before = new AlertWindow("a", "w", 40, now.AddMinutes(-1).ToUnixTimeSeconds());
    engine.Evaluate([before], settings, now);
    var path = Path.Combine(root, "notification-state.json"); var original = File.ReadAllText(path);
    var after = before with { Remaining = 4, ResetsAt = now.AddHours(5).ToUnixTimeSeconds() };
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        await ThrowsStorageFailure(() => Task.Run(() => engine.Evaluate([after], settings, now)));
    Check(File.ReadAllText(path) == original);
    var retry = engine.Evaluate([after], settings, now);
    Check(retry.Count == 2 && retry.Any(alert => alert.Kind == "low") && retry.Any(alert => alert.Kind == "reset"));
    Check(engine.Evaluate([after], settings, now).Count == 0);
    Check(new QuotaAlerts(root).Evaluate([after], settings, now).Count == 0);
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any());
});
await Test("notification commit failures preserve reset card reminders for a retry", async () =>
{
    var root = Path.Combine(testRoot, "card-alert-retry"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow;
    engine.Evaluate([new("a", "w", 60, null)], settings, now);
    ResetCreditDeadline[] cards = [new("a", "one", now.AddDays(2)), new("a", "two", now.AddHours(12))];
    var path = Path.Combine(root, "notification-state.json");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        await ThrowsStorageFailure(() => Task.Run(() => engine.Evaluate([], settings, now, cards)));
    var retry = engine.Evaluate([], settings, now, cards).Single(); Check(retry.Kind == "expiry" && retry.Count == 2);
    Check(engine.Evaluate([], settings, now, cards).Count == 0);
});
await Test("unchanged notification state avoids redundant disk writes", () =>
{
    var root = Path.Combine(testRoot, "unchanged-alerts"); Directory.CreateDirectory(root);
    var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow; var engine = new QuotaAlerts(root);
    AlertWindow[] windows = [new("a", "w", 40, null)]; engine.Evaluate(windows, settings, now);
    var path = Path.Combine(root, "notification-state.json"); var marker = now.AddDays(-2).UtcDateTime;
    File.SetLastWriteTimeUtc(path, marker);
    Check(engine.Evaluate(windows, settings, now).Count == 0);
    Check(Math.Abs((File.GetLastWriteTimeUtc(path) - marker).TotalSeconds) < 1);
    return Task.CompletedTask;
});
await Test("concurrent notification evaluation emits a threshold only once", async () =>
{
    var root = Path.Combine(testRoot, "concurrent-alerts"); Directory.CreateDirectory(root);
    var engine = new QuotaAlerts(root); var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow;
    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => engine.Evaluate([new("a", "w", 3, null)], settings, now))));
    Check(results.Sum(result => result.Count) == 1);
    Check(new QuotaAlerts(root).Evaluate([new("a", "w", 3, null)], settings, now).Count == 0);
});
await Test("notification state recovers backups without replacing them with damaged data", () =>
{
    var root = Path.Combine(testRoot, "alert-backup"); Directory.CreateDirectory(root);
    var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow; var engine = new QuotaAlerts(root);
    engine.Evaluate([new("a", "w", 40, null)], settings, now);
    Check(engine.Evaluate([new("a", "w", 4, null)], settings, now).Single().Kind == "low");
    engine.Evaluate([new("a", "w", 3, null)], settings, now);
    var path = Path.Combine(root, "notification-state.json"); var backup = File.ReadAllText(path + ".bak");
    File.WriteAllText(path, "damaged");
    Check(new QuotaAlerts(root).Evaluate([new("a", "w", 3, null)], settings, now).Count == 0);
    Check(File.ReadAllText(path + ".bak") == backup);
    File.WriteAllText(path, "damaged again");
    Check(new QuotaAlerts(root).Evaluate([new("a", "w", 3, null)], settings, now).Count == 0);
    return Task.CompletedTask;
});
await Test("nonfinite quota readings cannot poison notification persistence", () =>
{
    var root = Path.Combine(testRoot, "alert-invalid-quota"); Directory.CreateDirectory(root);
    var settings = new WidgetSettings(); var now = DateTimeOffset.UtcNow; var engine = new QuotaAlerts(root);
    Check(engine.Evaluate([new("a", "w", double.NaN, null), new("b", "w", double.PositiveInfinity, null)], settings, now).Count == 0);
    Check(engine.Evaluate([new("a", "w", 4, null)], settings, now).Single().Kind == "low");
    return Task.CompletedTask;
});
await Test("idle session polling avoids allocating a read buffer per unchanged file", () =>
{
    var root = Path.Combine(testRoot, "idle-session-allocation"); var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    var record = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "event_msg", payload = new { type = "task_started", turn_id = "test-turn" } });
    for (var index = 0; index < 16; index++) File.WriteAllText(Path.Combine(sessions, index + ".jsonl"), record + "\n");
    var monitor = new CodexActivityMonitor(root); Check(monitor.ReadSince(started).Running == 16);
    monitor.ReadSince(started);
    var before = GC.GetAllocatedBytesForCurrentThread();
    Check(monitor.ReadSince(started).Running == 16);
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated < 16 * 12 * 1024, $"idle allocation was {allocated} bytes");
    Console.WriteLine($"METRIC idle session polling for 16 files allocated {allocated} bytes");
    return Task.CompletedTask;
});
await Test("session tail keeps an event starting exactly at the tail boundary", () =>
{
    const int maximumRecord = 4 * 1024 * 1024;
    var root = Path.Combine(testRoot, "session-boundary"); var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    var record = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "event_msg", payload = new { type = "task_started", turn_id = "boundary-turn" } }) + "\n";
    File.WriteAllText(Path.Combine(sessions, "boundary.jsonl"), "\n" + record + new string('x', maximumRecord - record.Length - 1) + "\n");
    Check(new CodexActivityMonitor(root).ReadSince(started).Kind == CodexActivityKind.Working);
    return Task.CompletedTask;
});
await Test("session tail recovers valid activity after an oversized record", () =>
{
    var root = Path.Combine(testRoot, "session-oversized"); var sessions = Path.Combine(root, "sessions"); Directory.CreateDirectory(sessions);
    var path = Path.Combine(sessions, "oversized.jsonl"); File.WriteAllText(path, "");
    var started = DateTimeOffset.UtcNow.AddMinutes(-1); var monitor = new CodexActivityMonitor(root); monitor.ReadSince(started);
    var record = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type = "event_msg", payload = new { type = "request_user_input", call_id = "after-large-record" } }) + "\n";
    File.AppendAllText(path, new string('x', 4 * 1024 * 1024 + 128) + "\n" + record);
    var state = monitor.ReadSince(started); Check(state.Kind == CodexActivityKind.NeedsInput && state.Waiting == 1);
    return Task.CompletedTask;
});
await Test("quota parsing preserves primary and secondary identity when either window is missing", () =>
{
    var secondary = UsageSnapshot.Parse(Json("""{"rateLimits":{"secondary":{"usedPercent":25,"windowDurationMins":10080}}}"""), Json("{}"));
    Check(secondary.Buckets.Single().Windows.Single().Kind == "secondary");
    var primary = UsageSnapshot.Parse(Json("""{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300}}}"""), Json("{}"));
    Check(primary.Buckets.Single().Windows.Single().Kind == "primary");
    var store = new AccountStore(Path.Combine(testRoot, "window-identity-cache")); store.SaveCache("current", secondary);
    Check(store.LoadCache("current")!.Buckets.Single().Windows.Single().Kind == "secondary");
    return Task.CompletedTask;
});
await Test("notification migration establishes typed baselines and then reports future resets", () =>
{
    var root = Path.Combine(testRoot, "typed-alert-migration"); Directory.CreateDirectory(root);
    var now = DateTimeOffset.UtcNow; var settings = new WidgetSettings(); var engine = new QuotaAlerts(root);
    engine.Evaluate([new("a", "0", 40, now.AddMinutes(-1).ToUnixTimeSeconds())], settings, now);
    var first = new AlertWindow("a", "0", 50, now.AddMinutes(5).ToUnixTimeSeconds(), "primary");
    Check(new QuotaAlerts(root).Evaluate([first], settings, now).Count == 0);
    var next = first with { Remaining = 100, ResetsAt = now.AddHours(5).ToUnixTimeSeconds() };
    Check(new QuotaAlerts(root).Evaluate([next], settings, now.AddMinutes(6)).Single().Kind == "reset");
    return Task.CompletedTask;
});
await Test("typed quota changes cannot reuse a different period's reset or exhausted markers", () =>
{
    var root = Path.Combine(testRoot, "typed-period-change"); Directory.CreateDirectory(root);
    var now = DateTimeOffset.UtcNow; var settings = new WidgetSettings(); var engine = new QuotaAlerts(root);
    Check(engine.Evaluate([new("a", "slot", 0, now.AddMinutes(-1).ToUnixTimeSeconds(), "secondary")], settings, now).Single().Kind == "exhausted");
    var changed = engine.Evaluate([new("a", "slot", 3, now.AddHours(5).ToUnixTimeSeconds(), "primary")], settings, now);
    Check(changed.Single().Kind == "low");
    return Task.CompletedTask;
});
Console.WriteLine($"{pass}/{total} checks passed");
// Only remove the unique test directory created by this process.
Directory.Delete(testRoot, true);

static async Task ThrowsStorageFailure(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected a storage failure");
}
static string IdentityToken(string subject, string workspace)
{
    var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["sub"] = subject, ["email"] = "test@example.com", ["https://api.openai.com/auth"] = new { chatgpt_account_id = workspace } });
    return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test";
}
static object NativeAuth(string identity, string workspace) => new { auth_mode = "chatgpt", OPENAI_API_KEY = (string?)null, tokens = new { id_token = identity, access_token = "fake-access", refresh_token = "fake-refresh", account_id = workspace }, last_refresh = DateTimeOffset.UtcNow.ToString("O") };

static async Task Fake(string mode)
{
    var profile = Environment.GetEnvironmentVariable("CODEX_HOME");
    string? switchAccount = null;
    if (mode == "switch-race")
    {
        using var authentication = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile!, "auth.json")));
        switchAccount = authentication.RootElement.GetProperty("tokens").Str("account_id");
    }
    Console.WriteLine("not-json"); Console.WriteLine("[]"); Console.WriteLine("null");
    var writes = new SemaphoreSlim(1);
    async Task Send(object obj) { await writes.WaitAsync(); try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(obj)); await Console.Out.FlushAsync(); } finally { writes.Release(); } }
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var doc = JsonDocument.Parse(line); var message = doc.RootElement; if (!message.TryGetProperty("id", out var idElement)) continue;
        var id = idElement.GetInt64(); var method = message.Str("method"); var p = message.GetProperty("params").Clone();
        switch (method)
        {
            case "exit": return;
            case "error": await Send(new { id, error = new { message = "secret-token-do-not-expose" } }); break;
            case "delay": _ = Task.Run(async () => { await Task.Delay(p.Int("ms") ?? 20); await Send(new { id, result = p }); }); break;
            case "echo": await Send(new { id, result = p }); break;
            case "env": await Send(new { id, result = new { profile = Environment.GetEnvironmentVariable("CODEX_HOME"), key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") } }); break;
            case "account/read":
                if (mode == "switch-race") { await Send(new { id, result = new { account = new { type = "chatgpt", email = switchAccount + "@example.com" }, workspaceRouting = new { chatgptAccountId = switchAccount } } }); break; }
                if (mode == "malformed-login") await Send(new { id, result = new[] { "invalid-account" } });
                else if (mode == "logged-out") await Send(new { id, result = new { account = (object?)null } });
                else if (mode == "api-key") await Send(new { id, result = new { account = new { type = "apiKey" } } });
                else await Send(new { id, result = new { account = new { type = "chatgpt", email = mode == "per-profile" ? Path.GetFileName(Environment.GetEnvironmentVariable("CODEX_HOME")) + "@example.com" : "test@example.com" }, workspaceRouting = mode == "switched-account" ? new { chatgptAccountId = "first" } : null } }); break;
            case "account/rateLimits/read":
                if (mode == "switch-race")
                {
                    var holdAccount = Path.Combine(profile!, ".quota-test-hold-account");
                    if (File.Exists(Path.Combine(profile!, ".quota-test-hold")) || File.Exists(holdAccount) && File.ReadAllText(holdAccount) == switchAccount)
                    {
                        File.WriteAllText(Path.Combine(profile!, ".quota-test-reading"), "ready");
                        while (!File.Exists(Path.Combine(profile!, ".quota-test-release"))) await Task.Delay(20);
                    }
                    await Send(new { id, result = new { accountId = switchAccount, rateLimits = new { primary = new { usedPercent = switchAccount == "old-current" ? 99 : 27, windowDurationMins = 300 } } } }); break;
                }
                if (mode == "quota-error") { await Send(new { id, error = new { code = 401, message = "unauthorized" } }); break; }
                if (mode == "slow") await Task.Delay(TimeSpan.FromSeconds(40));
                await Send(new { id, result = new { accountId = mode == "per-profile" ? Path.GetFileName(Environment.GetEnvironmentVariable("CODEX_HOME")) : mode == "switched-account" ? "second" : null, rateLimits = new { primary = new { usedPercent = 27, windowDurationMins = 300 } } } }); break;
            case "account/login/start":
                await Send(new { id, result = new { loginId = "test-login", authUrl = mode == "evil" ? "https://evil.example/login" : "https://auth.openai.com/authorize" } });
                if (mode == "login-exit") return;
                if (mode is "normal" or "foreign-login" or "malformed-login") await Send(new { method = "account/login/completed", @params = new { success = true, loginId = mode == "foreign-login" ? "another-login" : "test-login" } }); break;
            default: await Send(new { id, result = new { } }); break;
        }
    }
}
