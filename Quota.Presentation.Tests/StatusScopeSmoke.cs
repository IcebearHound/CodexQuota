using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quota.Core;
using Quota.Desktop;

internal static class StatusScopeSmoke
{
    public static async Task Run(MainWindow main, string[]? args = null)
    {
        var accounts = (ObservableCollection<AccountViewModel>)typeof(MainWindow).GetField("accounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var settings = (WidgetSettings)typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var activity = typeof(MainWindow).GetField("activity", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var update = typeof(MainWindow).GetMethod("UpdateCodexStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalActivity = activity.GetValue(main);
        var originalLanguage = L.Language;
        var current = accounts.Single(account => account.Record.IsCurrent);
        var original = current.Snapshot!;
        var inactiveSnapshot = Copy(original); inactiveSnapshot.AccountId = "inactive-workspace";
        foreach (var window in inactiveSnapshot.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100;
        var inactive = new AccountViewModel(new() { Name = "Inactive workspace", Email = original.Email }); inactive.SetSnapshot(inactiveSnapshot);
        accounts.Add(inactive);
        var strip = new TaskbarWidget(main, groups, null);
        try
        {
            activity.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Working, Running: 1));
            update.Invoke(main, null);
            Check(groups.Single(group => group.Sources.Contains(current)).FocusWindow!.Remaining == 0, "fixture merges an exhausted inactive workspace");
            Check(main.CodexStatus.Signal == CodexSignal.Working, "inactive workspace quota cannot warn about the active Codex account");
            inactive.SetError("登录已失效，请重新登录后重试。"); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Working, "inactive credential errors stay outside the active runtime lamp");
            var low = Copy(original);
            foreach (var window in low.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100 - settings.LowQuotaThreshold / 2;
            current.SetSnapshot(low); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Low, "active account quota produces its low warning");
            var exhausted = Copy(original);
            foreach (var window in exhausted.Buckets.SelectMany(bucket => bucket.Windows)) window.UsedPercent = 100;
            current.SetSnapshot(exhausted); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Exhausted, "active account exhaustion produces its red warning");
            activity.SetValue(main, new CodexActivitySnapshot(CodexActivityKind.Offline)); update.Invoke(main, null);
            Check(main.CodexStatus.Signal == CodexSignal.Offline, "offline status retains gray precedence");
            Console.WriteLine("PASS runtime quota warnings use only the active Codex account across merged workspaces");
            var lamps = new[] { (StatusLamp)main.FindName("RuntimeLamp"), (StatusLamp)main.FindName("SimplifiedRuntimeLamp"),
                (StatusLamp)strip.FindName("RuntimeLamp"), (StatusLamp)strip.FindName("FullRuntimeLamp") };
            void CheckTooltips()
            {
                foreach (var lamp in lamps)
                    Check(ToolTipService.GetIsEnabled(lamp) && lamp.ToolTip is ToolTip { Content: TextBlock text } && text.Text == main.CodexStatus.Tooltip
                        && text.TextWrapping == TextWrapping.Wrap && AutomationProperties.GetHelpText(lamp) == text.Text,
                        "lamps expose their current localized status through a wrapping tooltip");
            }
            var working = new CodexActivitySnapshot(CodexActivityKind.Working, Running: 1);
            var issue = new CodexSessionIssue("status-scope", Guid.NewGuid().ToString("D"), CodexActivityKind.Error, DateTimeOffset.UtcNow, "status-scope/error");
            var chatError = new CodexActivitySnapshot(CodexActivityKind.Error, SessionIssues: [issue]);
            var status = main.CodexStatus;
            const string timeout = "读取超时。保留上次额度，稍后可重试。";
            const string expired = "登录已失效，请重新登录后重试。";
            foreach (var language in new[] { "en", "zh-CN" })
            {
                L.Apply(language);
                var quotaLabel = L.F("当前账号额度读取异常", "Current account quota read error");
                var chatLabel = L.F("Codex 会话出错。", "A Codex chat encountered an error.");
                var chatLink = L.F("单击查看 1 个异常会话。", "Click to view 1 session issue.");
                activity.SetValue(main, working); current.SetError(timeout); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Error && status.Tooltip.StartsWith(quotaLabel) && status.Tooltip.Contains(L.Error(timeout))
                    && !status.Tooltip.Contains(chatLabel) && !status.CanOpenInterruptedTask, "quota-only errors show the account reason without a session link");
                CheckTooltips();

                current.SetError(expired); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Error && status.Tooltip.Contains(L.Error(expired)) && !status.Tooltip.Contains(L.Error(timeout)),
                    "a new account error reason refreshes the tooltip while the lamp remains red");
                CheckTooltips();

                activity.SetValue(main, chatError); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Error && status.Tooltip.StartsWith(quotaLabel) && status.Tooltip.Contains(Environment.NewLine + Environment.NewLine + chatLabel)
                    && status.Tooltip.EndsWith(chatLink), "simultaneous errors show both sources in separate sections");
                CheckTooltips();

                activity.SetValue(main, working); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Error && status.Tooltip.StartsWith(quotaLabel) && !status.Tooltip.Contains(chatLabel)
                    && !status.CanOpenInterruptedTask, "session recovery while the quota error persists removes only the session message and link");
                CheckTooltips();

                activity.SetValue(main, chatError);
                current.SetSnapshot(original); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Error && status.Message == chatLabel && status.Tooltip.EndsWith(chatLink) && status.CanOpenInterruptedTask,
                    "quota recovery while a chat error persists removes only the quota message");
                CheckTooltips();

                activity.SetValue(main, working); update.Invoke(main, null);
                Check(status.Signal == CodexSignal.Working && status.Tooltip == status.Message, "successful activity clears the session error message");
                CheckTooltips();
            }
            activity.SetValue(main, chatError); current.SetError(expired); update.Invoke(main, null);
            var target = lamps[0]; var tooltip = (ToolTip)target.ToolTip;
            tooltip.PlacementTarget = target; tooltip.Placement = PlacementMode.Bottom; tooltip.IsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var border = (Border)VisualTreeHelper.GetChild(tooltip, 0);
            Check(tooltip.IsOpen && border.CornerRadius.TopLeft == 12, "status hover uses the shared rounded tooltip design");
            if (args is not null && Array.IndexOf(args, "--preview-dir") is var index && index >= 0 && index + 1 < args.Length)
            {
                Directory.CreateDirectory(args[index + 1]);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth), (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(border); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(args[index + 1], "status-lamp-errors.png")); encoder.Save(output);
            }
            tooltip.IsOpen = false;
            Console.WriteLine("PASS main, simplified and taskbar lamps distinguish localized quota and chat errors while sharing the styled tooltip");
        }
        finally
        {
            strip.Close(); current.SetSnapshot(original); accounts.Remove(inactive); activity.SetValue(main, originalActivity);
            L.Apply(originalLanguage); update.Invoke(main, null);
        }
    }
    private static UsageSnapshot Copy(UsageSnapshot source) => JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(source))!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
