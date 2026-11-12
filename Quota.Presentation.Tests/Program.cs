using Quota.Core;
using Quota.Desktop;

if (args.FirstOrDefault() == "--external-parent") { ExternalHostSmoke.RunParent(); return; }
if (args.FirstOrDefault() == "--taskbar-lifecycle") { Environment.ExitCode = TaskbarLifecycleSmoke.Run(args); return; }
if (args.FirstOrDefault() == "--guide-smoke") { Environment.ExitCode = GuideSmoke.Run(args); return; }

var pass = 0;
var cases = new (string Name, Action Verify)[]
{
    ("startup registration respects native state quoted paths upgrades and isolated cleanup", StartupRegistrationSmoke.Run),
    ("status read failures expose specific localized reasons and recover", () =>
    {
        var status = new CodexStatusViewModel(); var updates = new List<string?>(); status.PropertyChanged += (_, args) => updates.Add(args.PropertyName);
        try
        {
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Apply(language);
                foreach (var issue in Enum.GetValues<CodexActivityReadIssue>().Where(issue => issue != CodexActivityReadIssue.None))
                {
                    updates.Clear(); status.Update(new(CodexActivityKind.Unknown, ReadIssue: issue));
                    Check(status.Signal == CodexSignal.Unknown && status.ReadIssue == issue);
                    Check(status.Message != L.F("Codex 状态暂不可读。", "Codex status is unavailable."));
                    Check(updates.Contains(nameof(status.Message)) && updates.Contains(nameof(status.Tooltip)));
                }
                status.Update(new(CodexActivityKind.Working, Running: 1));
                Check(status.Signal == CodexSignal.Working && status.ReadIssue == CodexActivityReadIssue.None);
            }
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("read session issues keep list access while quota failures still signal errors", () =>
    {
        var status = new CodexStatusViewModel();
        var issue = new CodexSessionIssue("test", Guid.NewGuid().ToString("D"), CodexActivityKind.Paused, DateTimeOffset.UtcNow, "one", IsRead: true);
        status.Update(new(CodexActivityKind.Idle, SessionIssues: [issue]));
        Check(status.Signal == CodexSignal.Idle && !status.Flashing && !status.Breathing && status.UnreadSessionCount == 0 && status.CanOpenInterruptedTask);
        status.Update(new(CodexActivityKind.Idle, SessionIssues: [issue]), quotaError: true);
        Check(status.Signal == CodexSignal.Error && status.Flashing);
        status.Update(new(CodexActivityKind.Working, SessionIssues: [issue with { IsRead = false }]));
        Check(status.UnreadSessionCount == 1);
    }),
    ("startup cache remains distinguishable until a successful network read", () =>
    {
        var vm = Account(); var snapshot = Snapshot(DateTimeOffset.Now);
        vm.SetSnapshot(snapshot, fromCache: true); Check(vm.Status == "缓存"); Check(vm.Updated.Contains("缓存"));
        vm.IsRefreshing = true; Check(vm.Status == "更新中"); vm.IsRefreshing = false;
        vm.SetSnapshot(snapshot); Check(vm.Status == "已同步"); Check(!vm.Updated.Contains("缓存"));
    }),
    ("old snapshots include the date and expose an exact-time tooltip", () =>
    {
        var vm = Account(); var time = DateTimeOffset.Now.AddDays(-1); vm.SetSnapshot(Snapshot(time), fromCache: true);
        Check(vm.Updated.Contains(time.ToString("MM-dd HH:mm"))); Check(vm.UpdatedTooltip.Contains(time.ToString("yyyy-MM-dd HH:mm:ss zzz")));
        time = DateTimeOffset.Now.AddYears(-1); vm.SetSnapshot(Snapshot(time), fromCache: true); Check(vm.Updated.Contains(time.ToString("yyyy-MM-dd HH:mm")));
    }),
    ("read failures retain known usage and a recovery removes the stale state", () =>
    {
        var vm = Account(); vm.SetSnapshot(Snapshot(DateTimeOffset.Now)); vm.SetError("网络不可用");
        Check(vm.Status == "已过期" && vm.Buckets[0].Windows[0].Remaining == 73); Check(vm.Updated.Contains("显示上次数据"));
        vm.SetSnapshot(Snapshot(DateTimeOffset.Now)); Check(!vm.HasError && vm.Status == "已同步");
    }),
    ("current-account cache never claims its identity is verified after failure", () =>
    {
        var vm = new AccountViewModel(new() { Id = "current", Name = "current" }); vm.SetSnapshot(Snapshot(DateTimeOffset.Now), fromCache: true); vm.SetError("等待验证");
        Check(vm.Identity.Contains("当前登录未验证")); Check(vm.Error!.Contains("上次读取账号"));
        vm.SetSnapshot(Snapshot(DateTimeOffset.Now)); Check(!vm.Identity.Contains("未验证"));
    }),
    ("freshness clock changes notify the timestamp display", () =>
    {
        var vm = Account(); var notified = new List<string?>(); vm.PropertyChanged += (_, args) => notified.Add(args.PropertyName);
        vm.SetSnapshot(Snapshot(DateTimeOffset.Now), fromCache: true); Check(notified.Contains(nameof(vm.UpdatedTooltip))); notified.Clear();
        vm.UpdateClock(); Check(notified.Contains(nameof(vm.Updated)) && notified.Contains(nameof(vm.Status)));
    }),
    ("plan aliases display readable names", () =>
    {
        var vm = Account(); var snapshot = Snapshot(DateTimeOffset.Now); snapshot.Buckets[0].Plan = "prolite"; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro100");
        snapshot.Buckets[0].Plan = "pro"; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro200");
        snapshot.Buckets[0].Plan = "promax"; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro500");
        foreach (var plan in new[] { " ProLite ", "Pro100", "PRO100" }) { snapshot.Buckets[0].Plan = plan; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro100"); }
        foreach (var plan in new[] { "PRO", "Pro200" }) { snapshot.Buckets[0].Plan = plan; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro200"); }
        foreach (var plan in new[] { "ProMax", "Pro500" }) { snapshot.Buckets[0].Plan = plan; vm.SetSnapshot(snapshot); Check(vm.Plan == "Pro500"); }
    }),
    ("quota metric switches both the number and caption", () =>
    {
        var source = Source("test@example.com", "test", "workspace", 27);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source]);
        Check(group.FocusWindow!.Value == 73 && group.FocusWindow.Caption == "剩余");
        group.ShowUsed = true;
        Check(group.FocusWindow.Value == 27 && group.FocusWindow.Percent == "27%" && group.FocusWindow.Caption == "已用");
        source.IsRefreshing = true;
        Check(group.FocusWindow.Value == 27);
        group.ShowUsed = false; Check(group.FocusWindow.Value == 73);
    }),
    ("current-account email obeys privacy preference", () =>
    {
        var vm = new AccountViewModel(new() { Id = "current" }); var snapshot = Snapshot(DateTimeOffset.Now); snapshot.Email = "current@example.com"; vm.SetSnapshot(snapshot);
        Check(!vm.Identity.Contains(snapshot.Email)); vm.Privacy = false; Check(vm.Identity == snapshot.Email); vm.Privacy = true; Check(!vm.Identity.Contains(snapshot.Email));
    }),
    ("revealed cached identity remains explicitly unverified on failure", () =>
    {
        var vm = new AccountViewModel(new() { Id = "current" }); var snapshot = Snapshot(DateTimeOffset.Now); snapshot.Email = "last@example.com"; vm.SetSnapshot(snapshot); vm.SetError("unavailable"); vm.Privacy = false;
        Check(vm.Identity.Contains(snapshot.Email) && vm.Identity.Contains("上次账号") && vm.Identity.Contains("当前登录未验证"));
    }),
    ("same email merges current and imported sources despite case and whitespace", () =>
    {
        var current = Source(" User@Example.com ", "current", "workspace", 27);
        var imported = Source("user@example.com", "imported", "workspace", 27);
        var groups = AccountGroupViewModel.Build([current, imported]);
        try { Check(groups.Count == 1 && groups[0].Sources.Count == 2 && groups[0].Buckets.Count == 1); Check(groups[0].SourceSummary.Contains("本机 Codex") && groups[0].SourceSummary.Contains("CC Switch")); }
        finally { foreach (var group in groups) group.Dispose(); }
    }),
    ("different and missing emails remain separate", () =>
    {
        var groups = AccountGroupViewModel.Build([Source("one@example.com", "one", "w", 20), Source("two@example.com", "two", "w", 20), new(new() { Id = "unknown-a" }), new(new() { Id = "unknown-b" })]);
        try { Check(groups.Count == 4); } finally { foreach (var group in groups) group.Dispose(); }
    }),
    ("same workspace uses one freshest successful quota snapshot", () =>
    {
        var old = Source("same@example.com", "old", "workspace", 80);
        var fresh = Source("same@example.com", "fresh", "workspace", 12);
        var oldSnapshot = old.Snapshot!; oldSnapshot.FetchedAt = DateTimeOffset.Now.AddHours(-1); old.SetSnapshot(oldSnapshot);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(old), [old, fresh]);
        Check(group.Buckets.Single().Windows.Single().Remaining == 88);
    }),
    ("healthy alternate source prevents a source failure from failing the merged card", () =>
    {
        var failed = Source("same@example.com", "failed", "w", 80); failed.SetError("failed");
        var good = Source("same@example.com", "good", "w", 20);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(good), [failed, good]);
        Check(!group.HasError && group.Status == "已同步" && group.Buckets.Single().Windows.Single().Remaining == 80);
        Check(group.SourceSummary.Contains("1 个来源需处理"));
    }),
    ("same email retains separate workspace quota windows", () =>
    {
        var one = Source("same@example.com", "one", "workspace-one", 20);
        var two = Source("same@example.com", "two", "workspace-two", 90);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(one), [one, two]);
        Check(group.Buckets.Count == 2); Check(group.Buckets.All(bucket => bucket.Name.Contains("工作区")));
        Check(group.Buckets.Select(bucket => bucket.Windows.Single().Remaining).Order().SequenceEqual(new double[] { 10, 80 }));
    }),
    ("current account identity change produces a new grouping key", () =>
    {
        var current = Source("one@example.com", "current", "w1", 20);
        var oldKey = AccountGroupViewModel.KeyFor(current);
        current.SetSnapshot(new() { Email = "two@example.com", AccountId = "w2" });
        Check(AccountGroupViewModel.KeyFor(current) != oldKey);
        current.SetSnapshot(new() { Email = null, AccountId = "w3" });
        Check(AccountGroupViewModel.KeyFor(current) == "source/current");
    }),
    ("group reflects refresh and quota recovery from its sources", () =>
    {
        var one = Source("same@example.com", "one", "w", 20);
        var two = Source("same@example.com", "two", "w", 20);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(one), [one, two]);
        one.IsRefreshing = true; Check(group.IsRefreshing && group.Status == "更新中");
        one.IsRefreshing = false; one.SetError("offline"); two.SetError("offline"); Check(group.HasError);
        var recovered = Snapshot(DateTimeOffset.Now); recovered.Email = "same@example.com"; recovered.AccountId = "w";
        two.SetSnapshot(recovered); Check(!group.HasError);
    }),
    ("group source metadata respects hidden email display", () =>
    {
        var one = Source("private@example.com", "current", "w", 20);
        var two = Source("private@example.com", "two", "w", 20);
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(one), [one, two]);
        Check(!group.Identity.Contains("private@example.com") && !group.SourceTooltip.Contains("private@example.com"));
        one.Privacy = false; two.Privacy = false; Check(group.Identity.Contains("private@example.com"));
    }),
    ("imported email names preserve the source name while respecting privacy", () =>
    {
        var source = Source("person@example.com", "source", "workspace", 10);
        source.Record.Name = "person@example.com";
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source]);
        Check(group.Name == "p•••@example.com" && group.EditableName == "person@example.com");
        source.Privacy = false; Check(group.Name == "person@example.com");
        Check(source.Record.Name == "person@example.com");
    }),
    ("ring selects the most constrained window including a depleted weekly quota", () =>
    {
        var source = Source("one@example.com", "source", "workspace", 0);
        source.Snapshot!.Buckets[0].Windows.Add(new() { UsedPercent = 100, DurationMinutes = 10080 }); source.RefreshDerived();
        using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source]);
        Check(group.FocusWindow!.Remaining == 0 && group.FocusWindow.Label == "每周");
    }),
    ("English language translates quota metrics and account state and switches back", () =>
    {
        try
        {
            L.Apply("en"); var source = Source("same@example.com", "a", "w", 97);
            using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source]);
            Check(group.FocusWindow!.Caption == "Remaining" && group.Status == "Synced");
            Check(L.T("设置") == "Settings" && L.T("重置卡到期日期") == "Reset card expiry dates");
            Check(L.T("显示系统小组件（天气）") == "Show Windows Widgets (weather)");
            Check(L.T("打开 Windows 任务栏设置") == "Open Windows Taskbar settings");
            Check(L.T("任务栏布局") == "Taskbar layout" && L.T("完整信息") == "Full details");
            source.SetError("登录已失效"); Check(group.ActionHint == "Sign in again");
            Check(group.Error!.Contains("Authentication"));
        }
        finally { L.Apply("zh-CN"); }
        Check(L.T("设置") == "设置");
    }),
    ("summary low quota follows the configured threshold including merged workspaces", () =>
    {
        var previous = WindowViewModel.AlertThreshold;
        try
        {
            var source = Source("same@example.com", "one", "work", 86);
            var alternate = Source("same@example.com", "two", "personal", 25);
            using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source, alternate]);
            WindowViewModel.AlertThreshold = 5; Check(!group.HasLowQuota);
            WindowViewModel.AlertThreshold = 15; Check(group.HasLowQuota);
            group.ShowUsed = true; Check(group.HasLowQuota && group.FocusWindow!.Value == 86);
            WindowViewModel.AlertThreshold = 14; Check(group.HasLowQuota);
            WindowViewModel.AlertThreshold = 13; Check(!group.HasLowQuota);
        }
        finally { WindowViewModel.AlertThreshold = previous; }
    }),
    ("applying display preferences refreshes threshold colors and summary bindings", () =>
    {
        var previous = WindowViewModel.AlertThreshold;
        try
        {
            var source = Source("same@example.com", "one", "work", 86);
            using var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(source), [source]);
            var window = group.FocusWindow!;
            var groupChanges = new List<string?>(); var windowChanges = new List<string?>();
            group.PropertyChanged += (_, args) => groupChanges.Add(args.PropertyName);
            window.PropertyChanged += (_, args) => windowChanges.Add(args.PropertyName);
            WindowViewModel.AlertThreshold = 5; var normal = window.Color;
            WindowViewModel.AlertThreshold = 15; group.ShowUsed = false;
            Check(window.Color != normal && groupChanges.Contains(nameof(group.HasLowQuota)) && windowChanges.Contains(nameof(window.Color)));
        }
        finally { WindowViewModel.AlertThreshold = previous; }
    }),
    ("status lamp maps runtime activity and quota priorities to the requested signals", () =>
    {
        var lamp = new CodexStatusViewModel();
        lamp.Update(new(CodexActivityKind.Working)); Check(lamp.Signal == CodexSignal.Working && lamp.Breathing && !lamp.Flashing && lamp.Color == "#35B582");
        lamp.Update(new(CodexActivityKind.Completed)); Check(lamp.Signal == CodexSignal.Completed && lamp.Animation == CodexLampAnimation.Steady && lamp.Color == "#35B582");
        lamp.Update(new(CodexActivityKind.NeedsInput, Waiting: 1)); Check(lamp.Signal == CodexSignal.NeedsInput && lamp.Flashing && !lamp.Breathing && lamp.Color == "#D5A13E");
        lamp.Update(new(CodexActivityKind.Working), low: true); Check(lamp.Signal == CodexSignal.Low && !lamp.Breathing && lamp.Color == "#D5A13E");
        lamp.Update(new(CodexActivityKind.Working), exhausted: true, low: true); Check(lamp.Signal == CodexSignal.Exhausted && lamp.Animation == CodexLampAnimation.Steady && lamp.Color == "#DB5267");
        lamp.Update(new(CodexActivityKind.NeedsInput, Waiting: 1), quotaError: true); Check(lamp.Signal == CodexSignal.Error && lamp.NeedsInput && lamp.Flashing && !lamp.Breathing);
        lamp.Update(new(CodexActivityKind.Error)); Check(lamp.Signal == CodexSignal.Error && lamp.Flashing && !lamp.Breathing && lamp.Color == "#DB5267");
        lamp.Update(new(CodexActivityKind.Completed), exhausted: true); Check(lamp.Signal == CodexSignal.Exhausted && lamp.Animation == CodexLampAnimation.Steady && lamp.Color == "#DB5267");
        lamp.Update(new(CodexActivityKind.NeedsInput, Waiting: 1)); Check(lamp.Signal == CodexSignal.NeedsInput && lamp.Flashing && !lamp.Breathing && lamp.Color == "#D5A13E");
        lamp.Update(new(CodexActivityKind.Unknown)); Check(lamp.Signal == CodexSignal.Unknown && !lamp.Breathing);
        lamp.Update(new(CodexActivityKind.Offline)); Check(lamp.Signal == CodexSignal.Offline && !lamp.Breathing);
        lamp.Update(new(CodexActivityKind.Offline, Waiting: 1), exhausted: true, quotaError: true, low: true); Check(lamp.Signal == CodexSignal.Offline && !lamp.Breathing && !lamp.NeedsInput);
        lamp.Update(new(CodexActivityKind.Paused)); Check(lamp.Signal == CodexSignal.Paused && lamp.Flashing && !lamp.Breathing && lamp.Color == "#DB5267");
    }),
    ("taskbar reset countdown uses short labels and updates its bindings", () =>
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2).AddMinutes(31).ToUnixTimeSeconds();
        var window = new WindowViewModel(new() { UsedPercent = 25, ResetsAt = reset });
        Check(window.CompactResetText.StartsWith("2h") && window.CompactResetText.EndsWith("m") && window.CompactResetText.All(c => c < 128));
        var changes = new List<string?>(); window.PropertyChanged += (_, args) => changes.Add(args.PropertyName); window.UpdateClock();
        Check(changes.Contains(nameof(window.CompactResetText)));
        Check(new WindowViewModel(new()).CompactResetText == "—");
        Check(new WindowViewModel(new() { ResetsAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() }).CompactResetText == "0m");
    }),
    ("status messages switch to English without changing signal semantics", () =>
    {
        try
        {
            L.Apply("en"); var lamp = new CodexStatusViewModel(); lamp.Update(new(CodexActivityKind.NeedsInput, Waiting: 1));
            Check(lamp.Message.Contains("input") && lamp.Flashing && !lamp.Breathing);
            L.Apply("zh-CN"); lamp.Update(new(CodexActivityKind.NeedsInput, Waiting: 1)); Check(lamp.Message.Contains("输入"));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("stable status polling avoids redundant UI notifications and retains language updates", () =>
    {
        var status = new CodexStatusViewModel(); status.Update(new(CodexActivityKind.Working));
        var changes = new List<string?>(); status.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        for (var index = 0; index < 20; index++) status.Update(new(CodexActivityKind.Working));
        Check(changes.Count == 0);
        try
        {
            L.Apply("en"); status.Update(new(CodexActivityKind.Working)); Check(changes.Contains(nameof(status.Message))); changes.Clear();
            status.Update(new(CodexActivityKind.Working)); Check(changes.Count == 0);
            L.Apply("zh-CN"); status.Update(new(CodexActivityKind.Working)); Check(changes.Contains(nameof(status.Message)));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("guide connection feedback distinguishes saved canceled and actionable failures", () =>
    {
        try
        {
            L.Apply("zh-CN");
            Check(new AccountConnectionResult(AccountConnectionOutcome.Saved).Description.Contains("已保存"));
            Check(new AccountConnectionResult(AccountConnectionOutcome.Canceled).Description.Contains("取消"));
            Check(new AccountConnectionResult(AccountConnectionOutcome.CliMissing).Description.Contains("选择 Codex"));
            Check(new AccountConnectionResult(AccountConnectionOutcome.NoAccounts).Description.Contains("CC Switch"));
            Check(new AccountConnectionResult(AccountConnectionOutcome.Busy).Description.Contains("等待"));
            Check(new AccountConnectionResult(AccountConnectionOutcome.Failed).Description.Contains("权限"));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("guide connection feedback updates language while retaining its outcome", () =>
    {
        var result = new AccountConnectionResult(AccountConnectionOutcome.Canceled);
        try
        {
            L.Apply("zh-CN"); Check(result.Description.Contains("取消"));
            L.Apply("en"); Check(result.Description.Contains("canceled") && result.Outcome == AccountConnectionOutcome.Canceled);
            foreach (var outcome in Enum.GetValues<AccountConnectionOutcome>())
                Check(new AccountConnectionResult(outcome).Description.All(character => character < 128 || character == '→'));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("guide account failures use existing localized recovery instructions", () =>
    {
        var result = new AccountConnectionResult(AccountConnectionOutcome.Failed, "账号保存失败，请检查本地文件权限。");
        try
        {
            L.Apply("en"); Check(result.Description.Contains("permissions"));
            L.Apply("zh-CN"); Check(result.Description.Contains("账号保存失败"));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("import outcomes retain actionable scan notices across language changes", () =>
    {
        const string notice = "有账号缺少可导入的身份或刷新凭据，需要在 CC Switch 中重新认证。";
        var result = new AccountConnectionResult(AccountConnectionOutcome.Saved, Notices: [notice, notice]);
        try
        {
            L.Apply("en"); Check(result.Description.Contains("credentials saved") && result.Description.Contains("refresh credentials"));
            Check(result.Description.Split("refresh credentials").Length == 2);
            L.Apply("zh-CN"); Check(result.Description.Contains("认证已保存") && result.Description.Contains(notice));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("cached current-account connection failures retain their recovery action across languages", () =>
    {
        var account = new AccountViewModel(new() { Id = "current", Name = "current" });
        account.SetSnapshot(Snapshot(DateTimeOffset.Now), fromCache: true);
        account.SetError("Codex 连接中断，请重试。");
        try
        {
            foreach (var language in new[] { "zh-CN", "en" })
            {
                L.Apply(language);
                Check(account.ActionHint == L.T("检查网络后刷新"));
                Check(account.Error!.StartsWith(L.T("当前登录未验证，以下为上次读取账号的额度。 ")));
            }
            Check(account.Error!.Contains("network", StringComparison.OrdinalIgnoreCase));
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("account recovery hints are independent of the display language", () =>
    {
        try
        {
            foreach (var language in new[] { "en", "zh-CN" })
            {
                L.Apply(language);
                var account = Account();
                foreach (var issue in new[]
                {
                    (Error: "未找到 Codex，请在设置中选择 codex.exe。", Hint: "打开设置选择 Codex"),
                    (Error: "登录已失效，请重新登录后重试。", Hint: "重新登录以恢复认证"),
                    (Error: "此账号的本地登录目录已丢失，请重新登录。", Hint: "重新登录以恢复认证"),
                    (Error: "读取超时。保留上次额度，稍后可重试。", Hint: "检查网络后刷新")
                })
                {
                    account.SetError(issue.Error); Check(account.ActionHint == L.T(issue.Hint));
                }
            }
        }
        finally { L.Apply("zh-CN"); }
    }),
    ("group disposal detaches source notifications", () =>
    {
        var one = Source("same@example.com", "one", "w", 20);
        var group = new AccountGroupViewModel(AccountGroupViewModel.KeyFor(one), [one]);
        var notifications = 0; group.PropertyChanged += (_, _) => notifications++;
        group.Dispose(); one.IsRefreshing = true; Check(notifications == 0);
    })
};
foreach (var test in cases)
{
    try { test.Verify(); pass++; Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { Environment.ExitCode = 1; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{pass}/{cases.Length} presentation checks passed");

static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static AccountViewModel Account() => new(new() { Name = "test" });
static UsageSnapshot Snapshot(DateTimeOffset time) => new() { FetchedAt = time, Buckets = [new() { Name = "Codex", Windows = [new() { UsedPercent = 27, DurationMinutes = 300 }] }] };
static AccountViewModel Source(string email, string id, string workspace, double used)
{
    var source = new AccountViewModel(new() { Id = id, Name = id, ImportKey = id == "current" ? null : id, Email = email });
    var snapshot = Snapshot(DateTimeOffset.Now); snapshot.Email = email; snapshot.AccountId = workspace; snapshot.Buckets[0].Windows[0].UsedPercent = used;
    source.SetSnapshot(snapshot); return source;
}
