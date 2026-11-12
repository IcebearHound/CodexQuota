using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class AccountFlowSmoke
{
    public static void CheckRemovalDuringRefresh(MainWindow main, AccountStore store)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var record = new AccountRecord { Name = "Refreshing import", ImportKey = "refreshing-fixture" };
        var profile = store.CreateProfile(record.Id); WriteAuthentication(profile, "refreshing", "refreshing-workspace");
        var source = new AccountViewModel(record) { IsRefreshing = true };
        accounts.Add(source); store.SaveAccounts(accounts.Select(account => account.Record));
        try
        {
            foreach (var method in new[] { "RemoveSyncedAccounts", "RemoveAccount" })
            {
                Exception? failure = null;
                _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.IsVisible && window.Title == L.T("暂时无法移除账号"));
                    try
                    {
                        Check(Descendants<TextBlock>(dialog).Any(text => text.Text == L.T("请等待账号更新或切换完成后再移除。")), "busy removal explains when to retry");
                        Check(accounts.Contains(source) && store.LoadAccounts().Any(account => account.Id == record.Id), "busy removal preserves the row and stored record");
                        Check(File.Exists(Path.Combine(profile, "auth.json")), "busy removal preserves credentials used by the refresh");
                    }
                    catch (Exception error) { failure = error; }
                    finally { dialog.Close(); }
                });
                typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, method == "RemoveAccount" ? [source] : null);
                if (failure is not null) throw failure;
            }
            Console.WriteLine("PASS removal waits for active source refreshes and explains the retry without deleting credentials");
        }
        finally { accounts.Remove(source); store.SaveAccounts(accounts.Select(account => account.Record)); store.DeleteProfile(record.Id); }
    }

    public static void CheckReloginCommit(MainWindow main, AccountStore store)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var commit = typeof(MainWindow).GetMethod("CommitRelogin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalAccounts = accounts.ToArray();
        var profiles = Path.Combine(store.Root, "profiles");
        var originalProfiles = Directory.Exists(profiles) ? Directory.GetDirectories(profiles).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        try
        {
            foreach (var scenario in new[]
            {
                (Name: "masked", Subject: "original", Workspace: "workspace-a", Edited: false, Custom: false, Fails: false),
                (Name: "alias", Subject: "original", Workspace: "workspace-a", Edited: true, Custom: false, Fails: false),
                (Name: "saved-alias", Subject: "original", Workspace: "workspace-a", Edited: false, Custom: true, Fails: false),
                (Name: "subject", Subject: "different", Workspace: "workspace-a", Edited: false, Custom: true, Fails: false),
                (Name: "workspace", Subject: "original", Workspace: "workspace-b", Edited: false, Custom: false, Fails: false),
                (Name: "save-failure", Subject: "original", Workspace: "workspace-a", Edited: true, Custom: false, Fails: true)
            })
            {
                accounts.Clear(); foreach (var account in originalAccounts) accounts.Add(account);
                var previous = new AccountRecord { Name = scenario.Custom ? "Saved alias" : "fixture@example.com", Email = "fixture@example.com", CustomName = scenario.Custom };
                WriteAuthentication(store.CreateProfile(previous.Id), "original", "workspace-a");
                var scan = CcSwitchImport.Scan(Path.Combine(store.ProfilePath(previous.Id), "auth.json"));
                previous.ImportKey = scan.Accounts.Single().Key;
                var source = new AccountViewModel(previous) { Privacy = true };
                source.SetSnapshot(new() { Email = previous.Email, AccountId = "workspace-a" }); accounts.Add(source);
                store.SaveAccounts(accounts.Select(account => account.Record));
                var pending = new AccountRecord(); WriteAuthentication(store.CreateProfile(pending.Id), scenario.Subject, scenario.Workspace);
                var enteredName = scenario.Edited ? "Edited alias" : source.Name;
                if (scenario.Fails)
                {
                    var failed = false;
                    using (var locked = new FileStream(Path.Combine(store.Root, "accounts-v2.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        try { commit.Invoke(main, [source, pending, enteredName, "fixture@example.com"]); }
                        catch (TargetInvocationException error) when (error.InnerException is IOException or UnauthorizedAccessException) { failed = true; }
                    }
                    Check(failed && accounts.Contains(source) && source.Record.Id == previous.Id && source.Snapshot is not null, "failed commit preserves the original UI account");
                    Check(store.LoadAccounts().Any(account => account.Id == previous.Id) && File.Exists(Path.Combine(store.ProfilePath(previous.Id), "auth.json")), "failed commit preserves the saved record and credentials");
                    Console.WriteLine("PASS failed relogin commit preserves the current record and credential profile");
                    continue;
                }
                var replacement = (AccountViewModel)commit.Invoke(main, [source, pending, enteredName, "fixture@example.com"])!;
                var saved = store.LoadAccounts().Single(account => account.Id == pending.Id);
                Check(JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(replacement.Record), "relogin persistence matches the in-memory record");
                Check(accounts.Contains(replacement) && !accounts.Contains(source) && source.Record.Id == previous.Id && replacement.Snapshot is null, "relogin replaces the source after saving and discards the previous quota");
                Check(saved.Name == (scenario.Edited ? "Edited alias" : previous.Name) && saved.CustomName == (scenario.Custom || scenario.Edited), "relogin preserves names and explicit aliases");
                var sameIdentity = scenario.Subject == "original" && scenario.Workspace == "workspace-a";
                Check(saved.ImportKey == (sameIdentity ? previous.ImportKey : null), "CC Switch linkage follows verified subject and workspace");
                if (sameIdentity)
                {
                    var synchronized = CcSwitchImport.Synchronize(store, accounts.Select(account => account.Record), scan.Accounts);
                    Check(synchronized.Count == originalAccounts.Count(account => account.Managed) + 1, "subsequent sync replaces the same imported account");
                    var imported = synchronized.Single(account => account.ImportKey == previous.ImportKey);
                    if (saved.CustomName) Check(imported.Name == saved.Name && imported.CustomName, "sync retains the relogin alias");
                }
                Console.WriteLine("PASS relogin record consistency for " + scenario.Name);
            }
        }
        finally
        {
            accounts.Clear(); foreach (var account in originalAccounts) accounts.Add(account);
            store.SaveAccounts(accounts.Select(account => account.Record));
            foreach (var path in Directory.GetDirectories(profiles).Where(path => !originalProfiles.Contains(path))) store.DeleteProfile(Path.GetFileName(path));
        }
    }

    public static async Task CheckLoginDialogs(MainWindow main)
    {
        typeof(MainWindow).GetMethod("SettingsClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs()]);
        var settings = (Window)typeof(MainWindow).GetField("settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var owner = (Window)typeof(MainWindow).GetProperty("AccountDialogOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        Check(owner == settings, "account dialogs use the visible settings window as owner");
        var attempts = 0;
        var login = new LoginDialog(owner, "Fixture account", true, CancellationToken.None, (_, _) =>
            ++attempts == 1 ? Task.FromCanceled<string?>(new CancellationToken(true)) : Task.FromResult<string?>("fixture@example.com"));
        Exception? failure = null;
        login.Loaded += (_, _) => login.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                Check(login.Owner == settings && GetWindow(new WindowInteropHelper(login).Handle, 4) == new WindowInteropHelper(settings).Handle,
                    "login remains owned by its launching settings window");
                Check(!IsWindowEnabled(new WindowInteropHelper(settings).Handle), "modal login disables the launching settings window");
                Check(login.Topmost && login.Width <= SystemParameters.WorkArea.Width - 32, "login uses shared modal presentation and fits the work area");
                var close = Descendants<Button>(login).Single(button => Equals(button.ToolTip, L.T("关闭")));
                Check(AutomationProperties.GetName(close) == L.T("关闭") && close.Style == Application.Current.FindResource("IconButton"), "shared dialog close icons use the accessible icon style");
                var button = (Button)typeof(LoginDialog).GetField("login", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(login)!;
                var input = (TextBox)typeof(LoginDialog).GetField("input", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(login)!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(button.IsEnabled && input.IsEnabled && Equals(button.Content, L.T("重新尝试")), "canceled login permits an immediate retry");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; login.Close(); }
        });
        try
        {
            var result = login.ShowDialog();
            if (failure is not null) throw failure;
            Check(result == true && attempts == 2 && login.Email == "fixture@example.com", "retry completes the same modal login");
            Console.WriteLine("PASS modal login follows settings ownership and recovers from cancellation");
        }
        finally { login.Close(); settings.Close(); }
        var hidden = new Window();
        var standalone = new LoginDialog(hidden, "Fixture", false, CancellationToken.None, (_, _) => Task.FromResult<string?>(null));
        try { Check(standalone.Owner is null && standalone.WindowStartupLocation == WindowStartupLocation.CenterScreen, "hidden owners use a standalone centered dialog"); }
        finally { standalone.Close(); hidden.Close(); }
        Console.WriteLine("PASS login from a hidden owner uses the shared standalone dialog layout");
    }

    public static async Task CheckImportNotices(MainWindow main, AccountStore store)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var originalAccounts = accounts.ToArray();
        var profiles = Path.Combine(store.Root, "profiles");
        var originalProfiles = Directory.GetDirectories(profiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixture = new AccountRecord();
        WriteAuthentication(store.CreateProfile(fixture.Id), "import-fixture", "import-workspace");
        var valid = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(store.ProfilePath(fixture.Id), "auth.json")));
        var source = Path.Combine(store.Root, "cc-import-fixture.json");
        var sync = typeof(MainWindow).GetMethod("SyncCcSwitchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var partial in new[] { false, true })
            {
                var invalid = JsonSerializer.SerializeToElement(new { tokens = new { id_token = "invalid", refresh_token = "fixture-refresh" } });
                File.WriteAllText(source, JsonSerializer.Serialize(partial ? new { logins = new { invalid, valid } } : (object)invalid));
                Exception? failure = null;
                _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.IsVisible && window.Title == L.T("同步 CC Switch"));
                    try
                    {
                        Check(Descendants<TextBlock>(dialog).Any(text => text.Text.Contains("refresh credentials")), "import dialog exposes the actionable scan warning");
                        if (partial) Descendants<Button>(dialog).Single(button => Equals(button.Content, L.T("同步账号"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        else dialog.Close();
                    }
                    catch (Exception error) { failure = error; dialog.Close(); }
                });
                var result = await (Task<AccountConnectionResult>)sync.Invoke(main, [source])!;
                if (failure is not null) throw failure;
                Check(result.Outcome == (partial ? AccountConnectionOutcome.Saved : AccountConnectionOutcome.NoAccounts) && result.Notices?.Length == 1, "import outcome retains partial and empty scan warnings");
                Check(result.Description.Contains("refresh credentials"), "guide retains import recovery details");
                if (partial) Check(store.LoadAccounts().Count == originalAccounts.Count(account => account.Managed) + 1, "partial import commits the usable account");
                Console.WriteLine(partial ? "PASS partial CC Switch sync shows warnings and imports its usable account" : "PASS empty CC Switch sync shows its specific credential failure");
            }
        }
        finally
        {
            accounts.Clear(); foreach (var account in originalAccounts) accounts.Add(account);
            store.SaveAccounts(accounts.Select(account => account.Record));
            foreach (var path in Directory.GetDirectories(profiles).Where(path => !originalProfiles.Contains(path))) store.DeleteProfile(Path.GetFileName(path));
            File.Delete(source);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void WriteAuthentication(string profile, string subject, string workspace)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = subject, ["email"] = "fixture@example.com",
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = workspace }
        });
        var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(Path.Combine(profile, "auth.json"), JsonSerializer.Serialize(new
        {
            tokens = new { id_token = "fixture." + payload + ".signature", refresh_token = "fixture-refresh", access_token = "fixture-access", account_id = workspace },
            last_refresh = DateTimeOffset.UtcNow
        }), new UTF8Encoding(false));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
}
