using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class UpdatePromptSmoke
{
    public static async Task Run(GuideWindow guide, MainWindow main, AccountStore store, string[] args)
    {
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var state = Field("appUpdateState").GetValue(main)!;
        var check = Method("CheckForAppUpdateAsync");
        var setPhase = state.GetType().GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var next = new AppUpdate(new(3, 0, 0), "CodexQuota-3.0.0-win-x64.zip",
            new("https://github.com/IcebearHound/CodexQuota/releases/download/v3.0.0/CodexQuota-3.0.0-win-x64.zip"),
            68 * 1024 * 1024, new string('a', 64), new("https://github.com/IcebearHound/CodexQuota/releases/tag/v3.0.0"))
        {
            PublishedAt = new DateTimeOffset(2026, 11, 18, 6, 0, 0, TimeSpan.Zero),
            ReleaseNotes = "# Codex Quota 3.0\n\n- **更快启动**：从托盘恢复时更快看到额度。\n"
                + "- 改进任务栏小组件的状态刷新。\n- 修复部分显示器上弹窗位置偏移的问题。\n"
                + "![screenshot](https://example.invalid/image.png)"
        };
        Window? Prompt() => (Window?)Field("updatePrompt").GetValue(main);
        Task Fetch(bool automatic, Func<CancellationToken, Task<AppUpdate?>> respond, bool rateLimited = false)
            => (Task)check.Invoke(main, [automatic, respond, (Func<bool>)(() => rateLimited)])!;
        Task Found(AppUpdate release, bool automatic = true) => Fetch(automatic, _ => Task.FromResult<AppUpdate?>(release));
        void Tomorrow() => settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(-1);
        Button Action(Window window, string name) => Descendants<Button>(window).Single(button => Equals(button.Content, name));
        Button SettingsAction(Window window, string name) => Descendants<Button>(window).Single(button => AutomationProperties.GetName(button) == name);
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        guide.Dismiss();
        main.Hide();
        if (!args.Contains("--demo")) await Task.Delay(TimeSpan.FromMilliseconds(3200)); // Let the real startup hook observe the persisted opt-out.
        await Fetch(true, _ => throw new InvalidOperationException("disabled check hit the network"));
        Check(!store.LoadSettings().AutoCheckUpdates && settings.LastUpdateCheckUtc is null, "automatic checking is disabled by the saved setting");
        settings.AutoCheckUpdates = true;
        var started = 0;
        var finish = new TaskCompletionSource<AppUpdate?>();
        var first = Fetch(true, token => { started++; return finish.Task.WaitAsync(token); });
        var concurrent = Fetch(true, _ => { started++; return Task.FromResult<AppUpdate?>(next); });
        await concurrent;
        Check(started == 1 && !first.IsCompleted && Prompt() is null, "concurrent automatic starts are coalesced without a prompt");
        finish.SetResult(next); await first;
        Check(Prompt() is null && store.LoadSettings().LastUpdateCheckUtc is not null, "tray-only result waits for a visible window");

        L.Apply("zh-CN");
        settings.Theme = "light";
        Method("ApplySettings").Invoke(main, null);
        main.Show(); main.Activate();
        await Settle(main);
        var prompt = Prompt() ?? throw new InvalidOperationException("available update was not shown on opening");
        await Settle(prompt);
        Check(main.IsActive && !prompt.IsActive && prompt.Owner == main, "popup does not take focus and stays with its owner");
        var update = Action(prompt, "更新并重启");
        var skip = Action(prompt, "跳过此版本");
        var texts = Descendants<TextBlock>(prompt).Select(block => block.Text).ToArray();
        Check(texts.Contains("Codex Quota 3.0.0") && texts.Any(text => text.Contains("68.0 MB"))
            && texts.Any(text => text.Contains("• 更快启动") && !text.Contains("example.invalid")), "release version, size and plain-text notes are visible");
        var updatePosition = update.TranslatePoint(new Point(update.ActualWidth, update.ActualHeight), prompt);
        var skipPosition = skip.TranslatePoint(new Point(skip.ActualWidth, skip.ActualHeight), prompt);
        Check(updatePosition.X > prompt.ActualWidth / 2 && updatePosition.X > skipPosition.X
            && updatePosition.Y > prompt.ActualHeight - 32, "update and skip are in the lower-right footer");
        var preview = Array.IndexOf(args, "--preview-dir");
        if (preview >= 0) ProductScreenshotSmoke.Capture(prompt, args[preview + 1], "update-prompt.png");

        if (!args.Contains("--demo"))
        {
            var accepted = false;
            _ = main.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var alert = Application.Current.Windows.OfType<Window>().SingleOrDefault(window => window.Title == "原地更新不可用");
                accepted = alert is not null;
                alert?.Close();
            });
            Click(update);
            await Settle(prompt);
            Check(accepted && ReferenceEquals(prompt, Prompt()), "update button enters the existing installer guard without a second confirmation");
        }
        setPhase.Invoke(state, ["downloading", null]); await Settle(prompt);
        Check(Action(prompt, "取消下载").IsVisible && Descendants<ProgressBar>(prompt).Single().IsVisible
            && !update.IsVisible && !skip.IsVisible, "download progress and cancellation replace the decision actions");
        setPhase.Invoke(state, ["available", null]); await Settle(prompt);
        Click(skip);
        Check(Prompt() is null && store.LoadSettings().SkippedUpdateVersion == next.Version.ToString(), "skip closes the prompt and persists its exact version");
        await Fetch(true, _ => { started++; return Task.FromResult<AppUpdate?>(next); });
        Check(started == 1 && Prompt() is null, "reopening on the same day does not check again");
        Tomorrow();
        await Found(next);
        Check(Prompt() is null, "the skipped release is not prompted on later days");
        await Found(next, automatic: false);
        Check(Prompt() is null && Equals(Field("availableUpdate").GetValue(main), next), "skipped release remains available to manual checks");

        L.Apply("en");
        settings.Theme = "dark";
        Method("ApplySettings").Invoke(main, null);
        Tomorrow();
        await Found(next with
        {
            Version = new Version(3, 0, 1),
            ReleaseNotes = "## Improvements\n- The dashboard opens faster from the tray.\n- Fixed update dialog placement on secondary displays."
        });
        var newer = Prompt() ?? throw new InvalidOperationException("different version was suppressed");
        await Settle(newer);
        Check(Action(newer, "Update and restart").IsVisible && Action(newer, "Skip this version").IsVisible, "English actions are visible for a different version");
        if (preview >= 0) ProductScreenshotSmoke.Capture(newer, args[preview + 1], "update-prompt-en.png");
        newer.Close();

        Field("settingsPage").SetValue(main, "通用");
        Method("SettingsClick").Invoke(main, [main, new RoutedEventArgs()]);
        var preferences = (Window)Field("settingsWindow").GetValue(main)!;
        preferences.Activate();
        await Settle(preferences);
        CheckBox AutoToggle() => Descendants<CheckBox>(preferences).Single(box => AutomationProperties.GetName(box) == "Check for updates automatically");
        Tomorrow();
        var cancellation = false;
        var request = Fetch(true, async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return null; }
            catch (OperationCanceledException) { cancellation = true; throw; }
        });
        AutoToggle().IsChecked = false;
        await request;
        Check(cancellation && !store.LoadSettings().AutoCheckUpdates && Prompt() is null, "settings toggle cancels an automatic check and saves the opt-out");
        await Fetch(false, _ => Task.FromResult<AppUpdate?>(null));
        Check((string)state.GetType().GetField("Phase", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)! == "current",
            "manual checks still work with automatic checks disabled");
        await Fetch(false, _ => throw new UpdateRateLimitedException());
        await Settle(preferences);
        var limited = Prompt() ?? throw new InvalidOperationException("rate-limit prompt was not shown");
        await Settle(limited);
        Check(Action(limited, "Open GitHub").IsVisible && limited.Owner?.IsVisible == true,
            "a manual rate-limited check offers the official download page on the visible owner even when automatic checks are disabled");
        Check(SettingsAction(preferences, "Open GitHub downloads").IsVisible
            && Descendants<TextBlock>(preferences).Any(text => text.Text.Contains("rate limiting update checks")),
            "manual rate-limited checks explain the blocker and offer the official download page");
        if (preview >= 0)
        {
            ProductScreenshotSmoke.Capture(limited, args[preview + 1], "update-rate-limited.png");
        }
        Click(Action(limited, "Later"));
        Check(Prompt() is null, "Later dismisses the reminder");
        await Fetch(false, _ => Task.FromResult<AppUpdate?>(next), rateLimited: true);
        limited = Prompt() ?? throw new InvalidOperationException("successful fallback did not present the rate-limit reminder");
        await Settle(limited);
        Check(Descendants<TextBlock>(limited).Any(text => text.Text.Contains(next.Version.ToString()))
            && SettingsAction(preferences, "Download and update").IsVisible, "verified fallback offers both manual and in-app update routes");
        limited.Close();
        await Fetch(false, _ => Task.FromResult<AppUpdate?>(null), rateLimited: true);
        limited = Prompt() ?? throw new InvalidOperationException("up-to-date fallback did not identify the throttled API");
        await Settle(limited);
        Check(Descendants<TextBlock>(limited).Any(text => text.Text.Contains("confirmed you're up to date")), "an up-to-date result does not instruct the user to reinstall");
        limited.Close();
        await Fetch(false, _ => throw new HttpRequestException("offline fixture"));
        Check(Descendants<TextBlock>(preferences).Any(text => text.Text.StartsWith("Could not check for updates")),
            "a check failure is distinct from an installation failure");
        await Fetch(false, _ => throw new System.IO.InvalidDataException("invalid manifest fixture"));
        Check(SettingsAction(preferences, "Open GitHub downloads").IsVisible && !SettingsAction(preferences, "Download and update").IsVisible,
            "invalid metadata does not terminate the app or enable installation");
        await Fetch(false, _ => Task.FromResult<AppUpdate?>(null));
        AutoToggle().IsChecked = true;
        Tomorrow();
        await Fetch(true, _ => throw new HttpRequestException("offline fixture"));
        Check(Prompt() is null && Equals(state.GetType().GetField("Phase", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state), "current"),
            "an automatic network failure leaves the UI undisturbed");
        preferences.Close(); main.Hide(); Tomorrow();
        await Fetch(true, _ => throw new UpdateRateLimitedException());
        Check(Prompt() is null, "background rate limiting does not summon a tray-only window");
        L.Apply("zh-CN"); main.Show(); main.Activate(); await Settle(main);
        limited = Prompt() ?? throw new InvalidOperationException("deferred rate-limit prompt was not shown when the app reopened");
        await Settle(limited);
        Check(limited.Owner == main && Action(limited, "前往 GitHub").IsVisible, "reopening the app shows the localized manual update action");
        if (preview >= 0) ProductScreenshotSmoke.Capture(limited, args[preview + 1], "update-rate-limited-zh.png");
        limited.Close();
        Console.WriteLine("PASS daily opt-out, background deferral, version prompt, skip, and rate-limit reminders");
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
}
