using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quota.Core;
using Quota.Desktop;

internal static class SwitchingSmoke
{
    public static async Task Run(MainWindow main, AccountStore store)
    {
        if (!AccountSwitcher.CurrentHome.StartsWith(store.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Switch fixture must use its isolated Codex home.");
        var accounts = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)Field("visibleAccounts").GetValue(main)!;
        var cli = store.LoadSettings().CodexPath!;
        var startup = (Task?)Field("startupRefresh").GetValue(main);
        if (startup is not null) await startup;
        Application.Current.Windows.OfType<GuideWindow>().Single().Dismiss();
        var current = accounts.Single(account => account.Record.IsCurrent);
        foreach (var verificationFails in new[] { false, true })
        {
            var home = AccountSwitcher.CurrentHome;
            WriteAuthentication(home, "old-current");
            current.SetSnapshot(new() { Email = "old-current@example.com", AccountId = "old-current", Buckets = [new() { Windows = [new() { UsedPercent = 99 }] }] });
            File.WriteAllText(Path.Combine(home, ".quota-test-scenario"), "switch-race");
            File.Delete(Path.Combine(home, ".quota-test-release")); File.Delete(Path.Combine(home, ".quota-test-reading"));
            File.WriteAllText(Path.Combine(home, ".quota-test-hold"), "hold");
            var record = new AccountRecord { Name = "Switch target" };
            var targetProfile = store.CreateProfile(record.Id);
            WriteAuthentication(targetProfile, "new-current");
            File.WriteAllText(Path.Combine(targetProfile, ".quota-test-scenario"), "switch-race");
            var target = new AccountViewModel(record); target.SetSnapshot(new() { Email = "new-current@example.com", AccountId = "new-current" });
            accounts.Add(target);
            var refresh = typeof(MainWindow).GetMethod("RefreshAccountAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var oldRead = (Task)refresh.Invoke(main, [cli, current])!;
            try
            {
                await WaitFor(() => File.Exists(Path.Combine(home, ".quota-test-reading")), "old current read starts");
                var joined = (Task)refresh.Invoke(main, [cli, current])!;
                Check(!joined.IsCompleted, "duplicate refresh waits for its existing read");
                var targetGroup = groups.Single(group => group.Sources.Contains(target));
                var verification = VerifyDialogAsync();
                typeof(MainWindow).GetMethod("SwitchAccountClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [new Button { Tag = targetGroup }, new RoutedEventArgs()]);
                await verification;
                await Task.WhenAll(oldRead, joined);
                Check(!current.IsRefreshing && !((Dictionary<AccountViewModel, Task>)Field("accountRefreshes").GetValue(main)!).ContainsKey(current), "finished reads leave no pending tracker");
                Console.WriteLine(verificationFails
                    ? "PASS committed switch retains the new identity when current-account verification fails"
                    : "PASS switching waits for the old quota read and publishes the new current account");

                async Task VerifyDialogAsync()
                {
                    Window? dialog = null;
                    try
                    {
                        await WaitFor(() => Application.Current.Windows.OfType<Window>().Any(window => window.Title == L.T("正在切换")), "switch dialog opens");
                        dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == L.T("正在切换"));
                        await Task.Delay(150);
                        Check(ReadAccount(home) == "old-current", "authentication waits for the active old read");
                        Check(dialog.Title == L.T("正在切换"), "switch dialog stays pending until the read finishes");
                        var skipped = (Task)refresh.Invoke(main, [cli, target])!;
                        Check(skipped.IsCompleted, "new quota refreshes wait outside the switch transaction");
                        if (verificationFails) File.WriteAllText(Path.Combine(home, ".quota-test-scenario"), "quota-error");
                        File.Delete(Path.Combine(home, ".quota-test-hold"));
                        File.WriteAllText(Path.Combine(home, ".quota-test-release"), "release");
                        await WaitFor(() => dialog.Title == L.T("账号已切换") && !current.IsRefreshing, "committed switch completes current verification");
                        Check(ReadAccount(home) == "new-current", "target authentication was committed");
                        Check(current.Snapshot?.AccountId == "new-current" && current.Snapshot.Email == "new-current@example.com", "current card uses the target identity");
                        Check(store.LoadCache("current")?.AccountId == "new-current", "current cache cannot retain the previous identity");
                        Check(current.HasError == verificationFails && current.IsCached == verificationFails, "verification freshness is explicit");
                        if (!verificationFails) Check(current.Snapshot!.Buckets.Single().Windows.Single().Remaining == 73, "old low-quota response cannot overwrite the switched account");
                        var later = Descendants<Button>(dialog).Single(button => Equals(button.Content, L.T("稍后重启")));
                        later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    finally
                    {
                        File.Delete(Path.Combine(home, ".quota-test-hold"));
                        File.WriteAllText(Path.Combine(home, ".quota-test-release"), "release");
                        if (dialog is not null)
                        {
                            await WaitFor(() => dialog.Title != L.T("正在切换"), "switch dialog becomes closable");
                            dialog.Close();
                        }
                    }
                }
            }
            finally
            {
                File.Delete(Path.Combine(home, ".quota-test-hold"));
                File.WriteAllText(Path.Combine(home, ".quota-test-release"), "release");
                await oldRead; accounts.Remove(target); store.DeleteProfile(record.Id);
            }
        }
        await CheckEarlyDialogClose(main, store, accounts, groups, current);
        Console.WriteLine("PASS duplicate refresh callers share completion and switches leave no pending refresh trackers");
    }

    private static async Task CheckEarlyDialogClose(MainWindow main, AccountStore store, ObservableCollection<AccountViewModel> accounts,
        ObservableCollection<AccountGroupViewModel> groups, AccountViewModel current)
    {
        var home = AccountSwitcher.CurrentHome;
        WriteAuthentication(home, "old-current");
        File.WriteAllText(Path.Combine(home, ".quota-test-scenario"), "switch-race");
        File.Delete(Path.Combine(home, ".quota-test-release")); File.Delete(Path.Combine(home, ".quota-test-reading"));
        File.WriteAllText(Path.Combine(home, ".quota-test-hold-account"), "new-current");
        current.SetSnapshot(new() { Email = "old-current@example.com", AccountId = "old-current" });
        var targets = new[] { "new-current", "later-current" }.Select(identity =>
        {
            var record = new AccountRecord { Name = identity };
            var profile = store.CreateProfile(record.Id); WriteAuthentication(profile, identity);
            File.WriteAllText(Path.Combine(profile, ".quota-test-scenario"), "switch-race");
            var target = new AccountViewModel(record); target.SetSnapshot(new() { Email = identity + "@example.com", AccountId = identity });
            accounts.Add(target); return target;
        }).ToArray();
        var switchAccount = typeof(MainWindow).GetMethod("SwitchAccountClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var target in targets)
            {
                var second = ReferenceEquals(target, targets[1]);
                var observation = ObserveDialogAsync(second);
                switchAccount.Invoke(main, [new Button { Tag = groups.Single(group => group.Sources.Contains(target)) }, new RoutedEventArgs()]);
                await observation;
            }
            Check(current.Snapshot?.AccountId == "later-current" && !current.HasError && store.LoadCache("current")?.AccountId == "later-current",
                "a second switch cannot be overwritten by verification from the first switch");
            Console.WriteLine("PASS closing the success dialog early preserves ordering for the next account switch");
        }
        finally
        {
            File.Delete(Path.Combine(home, ".quota-test-hold-account"));
            File.WriteAllText(Path.Combine(home, ".quota-test-release"), "release");
            await Task.WhenAll(((Dictionary<AccountViewModel, Task>)Field("accountRefreshes").GetValue(main)!).Values.ToArray());
            foreach (var target in targets) { accounts.Remove(target); store.DeleteProfile(target.Record.Id); }
        }

        async Task ObserveDialogAsync(bool second)
        {
            Window? dialog = null;
            try
            {
                await WaitFor(() => Application.Current.Windows.OfType<Window>().Any(window => window.Title == L.T("正在切换") || window.Title == L.T("账号已切换")), "switch dialog opens");
                dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == L.T("正在切换") || window.Title == L.T("账号已切换"));
                if (second)
                {
                    await Task.Delay(150);
                    Check(dialog.Title == L.T("正在切换") && ReadAccount(home) == "new-current", "second switch waits for the first verification");
                    File.Delete(Path.Combine(home, ".quota-test-hold-account"));
                    File.WriteAllText(Path.Combine(home, ".quota-test-release"), "release");
                }
                await WaitFor(() => dialog.Title == L.T("账号已切换") && (second ? !current.IsRefreshing : File.Exists(Path.Combine(home, ".quota-test-reading"))),
                    "first verification is held and second verification finishes");
                if (!second) Check(current.IsRefreshing, "success dialog can close while current verification is pending");
                Descendants<Button>(dialog).Single(button => Equals(button.Content, L.T("稍后重启"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally
            {
                if (dialog is not null && dialog.Title == L.T("账号已切换")) dialog.Close();
            }
        }
    }

    public static void WriteAuthentication(string profile, string account)
        => File.WriteAllText(Path.Combine(profile, "auth.json"), JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt", tokens = new { account_id = account, id_token = "fixture-id", access_token = "fixture-access", refresh_token = "fixture-refresh" }
        }));
    private static string? ReadAccount(string home)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
        return document.RootElement.GetProperty("tokens").Str("account_id");
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task WaitFor(Func<bool> ready, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        Check(ready(), message);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
