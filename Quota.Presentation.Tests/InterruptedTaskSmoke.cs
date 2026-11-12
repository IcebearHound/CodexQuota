using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class InterruptedTaskSmoke
{
    public static async Task Run(MainWindow main)
    {
        var activityField = typeof(MainWindow).GetField("activity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var opener = typeof(MainWindow).GetField("openThread", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = activityField.GetValue(main); var originalOpener = opener.GetValue(main); var language = L.Language;
        var update = typeof(MainWindow).GetMethod("UpdateCodexStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var latest = Guid.NewGuid().ToString("D"); var earlier = Guid.NewGuid().ToString("D");
        var first = new CodexSessionIssue(latest, latest, CodexActivityKind.Paused, DateTimeOffset.UtcNow, "first");
        var second = new CodexSessionIssue(earlier, earlier, CodexActivityKind.Paused, DateTimeOffset.UtcNow.AddHours(-1), "earlier");
        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var fixture = Path.Combine(Path.GetTempPath(), "session-list-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "session_index.jsonl"), "incomplete\n" + JsonSerializer.Serialize(new { id = latest, thread_name = "代码生成与结果核对" }) + "\n" + JsonSerializer.Serialize(new { id = earlier, thread_name = "多会话分析任务" }) + "\n");
        Environment.SetEnvironmentVariable("CODEX_HOME", fixture);
        var links = new List<Uri>(); opener.SetValue(main, (Action<Uri>)links.Add);
        var strip = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(strip, main, groups);
        SessionIssuesWindow? dialog = null;
        try
        {
            flyout.Left = SystemParameters.WorkArea.Left + 20; flyout.Top = SystemParameters.WorkArea.Top + 20; flyout.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            activityField.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Paused, Running: 1, SessionIssues: [first, second]));
            update.Invoke(main, null);
            var lamps = new[] { (StatusLamp)main.FindName("RuntimeLamp"),
                (StatusLamp)strip.FindName("RuntimeLamp"), (StatusLamp)strip.FindName("FullRuntimeLamp"), Descendants<StatusLamp>(flyout).Single() };
            var parentClicks = 0; ((Border)strip.FindName("Bar")).MouseLeftButtonUp += (_, _) => parentClicks++;
            foreach (var lamp in lamps)
            {
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
                lamp.RaiseEvent(click);
                Check(click.Handled && lamp.Focusable && lamp.Cursor == Cursors.Hand, "interruption click opens its list and stops parent actions");
                var peer = UIElementAutomationPeer.CreatePeerForElement(lamp)!;
                Check(peer.GetAutomationControlType() == AutomationControlType.Button, "interruption lamp exposes a button role");
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            dialog = Application.Current.Windows.OfType<SessionIssuesWindow>().Single();
            Check(Descendants<TextBlock>(dialog).Any(text => text.Text == "代码生成与结果核对"), "session index titles are shown after malformed lines");
            var arguments = Environment.GetCommandLineArgs(); var previewIndex = Array.IndexOf(arguments, "--preview-dir");
            if (previewIndex >= 0 && arguments.Length > previewIndex + 1)
            {
                dialog.UpdateLayout();
                var image = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(dialog);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(arguments[previewIndex + 1], "session-issues.png")); encoder.Save(stream);
            }
            Check(links.Count == 0 && parentClicks == 0 && main.CodexStatus.UnreadSessionCount == 2, "all surfaces open one list without launching a chat");
            Click(Buttons(dialog, L.F("打开会话", "Open chat")).First());
            Check(links.Single().AbsoluteUri == "codex://threads/" + latest, "a list row opens its own chat");
            Console.WriteLine("PASS main taskbar and flyout lamps open one actionable session issue list");
            Check(!Buttons(dialog, L.F("已读", "Mark read")).Any(), "session rows expose one ignore action");
            Click(Buttons(dialog, L.F("忽略", "Ignore")).First());
            Check(main.CodexStatus.UnreadSessionCount == 1 && main.CodexStatus.Signal == CodexSignal.Paused, "one ignore leaves the remaining issue active");
            Click(Buttons(dialog, L.F("忽略", "Ignore")).First());
            Check(main.CodexStatus.UnreadSessionCount == 0 && main.CodexStatus.SessionIssues.Count == 0 && main.CodexStatus.Signal == CodexSignal.Working && !main.CodexStatus.Flashing, "ignoring the other row releases interruption flashing");
            for (var index = 0; index < 5; index++) update.Invoke(main, null);
            Check(!main.CodexStatus.Flashing, "polling keeps ignored occurrences acknowledged");
            var fresh = first with { Occurrence = "second", OccurredAt = DateTimeOffset.UtcNow.AddSeconds(1) };
            activityField.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Paused, Running: 1, SessionIssues: [fresh, second])); update.Invoke(main, null);
            Check(main.CodexStatus.UnreadSessionCount == 1 && main.CodexStatus.Flashing, "a fresh interruption in the same chat becomes unread");
            Click(Buttons(dialog, L.F("全部忽略", "Ignore all")).Single());
            Check(main.CodexStatus.UnreadSessionCount == 0 && main.CodexStatus.Signal == CodexSignal.Working && !main.CodexStatus.Flashing, "bulk read clears outstanding interruption alerts");
            Console.WriteLine("PASS single ignore bulk ignore and repeated interruptions update all status lights");
            L.Apply("en"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(dialog.Title == "Session issues" && !Buttons(dialog, "Ignore all").Single().IsEnabled, "read state and disabled bulk action survive a language change");
            dialog.Width = 340; dialog.UpdateLayout();
            var scroll = Descendants<ScrollViewer>(dialog).Single();
            Check(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "small session issue lists avoid horizontal overflow");
            var unidentified = new CodexSessionIssue("legacy", null, CodexActivityKind.Paused, null, "legacy");
            activityField.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Paused, SessionIssues: [unidentified])); update.Invoke(main, null);
            Check(!Buttons(dialog, "Open chat").Single().IsEnabled, "unidentified legacy rows expose read and ignore without an invented chat target");
            Click(Buttons(dialog, "Ignore all").Single());
            Check(main.CodexStatus.Signal == CodexSignal.Idle && !main.CodexStatus.Flashing, "acknowledging interrupted work reports idle");
            Check(Descendants<TextBlock>(dialog).Any(text => text.Text == "No session issues"), "ignoring the final row shows the empty list");
            Console.WriteLine("PASS session list handles localization narrow layouts legacy IDs and empty state");
            activityField.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Working)); update.Invoke(main, null);
            Check(!main.CodexStatus.CanOpenInterruptedTask && !lamps[0].Focusable
                && UIElementAutomationPeer.CreatePeerForElement(lamps[0])!.GetPattern(PatternInterface.Invoke) is null, "resuming removes the interrupted-chat action");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalHome);
            dialog?.Close(); flyout.Close(); strip.Close(); opener.SetValue(main, originalOpener); L.Apply(language);
            activityField.SetValue(main, original); update.Invoke(main, null);
        }
    }
    private static IEnumerable<Button> Buttons(DependencyObject parent, string name) => Descendants<Button>(parent).Where(button => AutomationProperties.GetName(button) == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
