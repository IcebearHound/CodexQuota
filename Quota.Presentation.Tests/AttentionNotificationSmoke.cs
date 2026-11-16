using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class AttentionNotificationSmoke
{
    public static async Task Run(MainWindow main, string[] args)
    {
        var activity = Field("activity");
        var originalActivity = activity.GetValue(main);
        var originalLanguage = L.Language;
        var settings = (WidgetSettings)Field("settings").GetValue(main)!;
        var originalSettings = JsonSerializer.Serialize(settings);
        var accounts = (ObservableCollection<AccountViewModel>)Field("accounts").GetValue(main)!;
        var snapshots = accounts.Select(account => (Account: account, Snapshot: account.Snapshot!)).ToArray();
        var outputIndex = Array.IndexOf(args, "--preview-dir");
        var output = outputIndex >= 0 ? args[outputIndex + 1] : null;
        if (output is not null) Directory.CreateDirectory(output);
        AttentionNotificationWindow Notification() => Application.Current.Windows.OfType<AttentionNotificationWindow>().Single();
        void Update(CodexActivitySnapshot value) { activity.SetValue(main, value); Method("UpdateCodexStatus").Invoke(main, null); }
        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Notification().UpdateLayout(); }
        void Capture(string name) { if (output is not null) ProductScreenshotSmoke.Capture(Notification(), output, name + ".png"); }
        try
        {
            settings.Theme = "light"; settings.Material = "solid"; settings.Language = "zh-CN";
            Method("ApplySettings").Invoke(main, null);
            main.Activate(); var foreground = GetForegroundWindow();
            Update(new(CodexActivityKind.NeedsInput, Waiting: 1, AttentionToken: "first"));
            await Settle();
            var notification = Notification();
            Check(notification.IsVisible && !notification.ShowInTaskbar && !notification.ShowActivated && GetForegroundWindow() == foreground, "input notification is visible without stealing focus or adding a taskbar button");
            Check(notification.ActualWidth <= 332 && notification.ActualHeight <= 150 && !Descendants<ItemsControl>(notification).Any(), "input notification stays compact and contains no account panel");
            Check(!Application.Current.Windows.OfType<TaskbarFlyout>().Any(window => window.IsVisible), "input alert does not expand account flyouts");
            Check(SendMessage(new WindowInteropHelper(notification).Handle, 0x0021, 0, 0) == 3, "notification mouse interaction uses MA_NOACTIVATE");
            Capture("notification-input-light");

            Descendants<Button>(notification).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Update(new(CodexActivityKind.NeedsInput, Waiting: 1, AttentionToken: "first"));
            Check(!notification.IsVisible, "dismissed input notifications stay dismissed during polling");
            Update(new(CodexActivityKind.NeedsInput, Waiting: 1, AttentionToken: "second"));
            Check(notification.IsVisible, "a new input occurrence reuses and reopens the small notification");
            Update(new(CodexActivityKind.Working, Running: 1));
            Check(!notification.IsVisible && !main.CodexStatus.NeedsInput, "resolved input immediately clears the notification and waiting lamp");
            Update(new(CodexActivityKind.Offline, Waiting: 1, AttentionToken: "offline"));
            Check(!notification.IsVisible, "offline snapshots cannot open an input notification");

            var id = Guid.NewGuid().ToString("D");
            var issue = new CodexSessionIssue(id, id, CodexActivityKind.Paused, DateTimeOffset.UtcNow, "first");
            Update(new(CodexActivityKind.Paused, SessionIssues: [issue])); await Settle();
            Check(notification.IsVisible && notification.Title == L.F("会话已中断", "Chat interrupted"), "new interruptions use the small notification");
            Capture("notification-interrupted-light");
            Method("IgnoreAllSessionIssues").Invoke(main, null);
            Check(!notification.IsVisible, "ignoring an issue dismisses its active notification");
            Update(new(CodexActivityKind.Paused, SessionIssues: [issue]));
            Check(!notification.IsVisible, "ignored interruptions do not re-notify on refresh");
            Update(new(CodexActivityKind.Paused, SessionIssues: [issue with { Occurrence = "new" }]));
            Check(notification.IsVisible, "a new interruption occurrence notifies again");
            Update(new(CodexActivityKind.Idle));
            Check(!notification.IsVisible, "resolved session issues dismiss their notification");

            foreach (var account in accounts)
            {
                var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(account.Snapshot))!;
                foreach (var window in snapshot.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = account.Record.IsCurrent ? 100 : 50;
                account.SetSnapshot(snapshot);
            }
            settings.NotifyLowQuota = false; settings.NotifyReset = false; settings.NotifyCardExpiry = false; settings.NotifyExhausted = false;
            Field("demo").SetValue(main, false);
            try
            {
                Method("CheckNotifications").Invoke(main, null);
                Check(!notification.IsVisible, "disabled exhaustion notifications are respected");
                settings.NotifyExhausted = true;
                Method("CheckNotifications").Invoke(main, null);
            }
            finally { Field("demo").SetValue(main, true); }
            await Settle();
            Check(notification.IsVisible && notification.Title == L.F("额度已耗尽", "Quota exhausted"), "quota evaluation routes exhaustion to a small notification");
            Capture("notification-exhausted-light");
            Check(!Application.Current.Windows.OfType<TaskbarFlyout>().Any(window => window.IsVisible), "interruption and quota alerts never expand the account panel");

            var timer = (DispatcherTimer)typeof(AttentionNotificationWindow).GetField("dismissTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(notification)!;
            timer.Interval = TimeSpan.FromMilliseconds(80);
            notification.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseEnterEvent });
            Check(!timer.IsEnabled, "hover pauses automatic dismissal");
            notification.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = UIElement.MouseLeaveEvent });
            await Task.Delay(150);
            Check(!notification.IsVisible && !timer.IsEnabled, "leaving restarts the timer and hides the notification");
            timer.Interval = TimeSpan.FromSeconds(8);
            settings.Theme = "dark"; Method("ApplySettings").Invoke(main, null);
            Update(new(CodexActivityKind.NeedsInput, Waiting: 1, AttentionToken: "dark")); await Settle(); Capture("notification-input-dark");
            Console.WriteLine("PASS compact input interruption and exhaustion notifications, deduplication, resolution, settings, focus and dismissal lifecycle");
        }
        finally
        {
            Field("demo").SetValue(main, true);
            foreach (var (account, snapshot) in snapshots) account.SetSnapshot(snapshot);
            var saved = JsonSerializer.Deserialize<WidgetSettings>(originalSettings)!;
            foreach (var property in typeof(WidgetSettings).GetProperties().Where(property => property.CanWrite)) property.SetValue(settings, property.GetValue(saved));
            activity.SetValue(main, originalActivity); Method("ApplySettings").Invoke(main, null);
            L.Apply(originalLanguage);
            var notification = (AttentionNotificationWindow?)Field("attentionNotification").GetValue(main);
            notification?.Close(); Field("attentionNotification").SetValue(main, null);
        }
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
