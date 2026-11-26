using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Quota.Core;
using Quota.Desktop;

internal static class ApiCreditsSmoke
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task Run(GuideWindow guide, MainWindow main, AccountStore store, string[] args)
    {
        await CheckEmbeddedBilling(main, store);
        var previewIndex = Array.IndexOf(args, "--preview-dir");
        var preview = previewIndex >= 0 ? args[previewIndex + 1] : null;
        guide.ShowPage(2); await Settle(guide);
        var toggle = Descendants<CheckBox>(guide).Single(box => AutomationProperties.GetName(box) == "Monitor API promotional credits");
        Check(toggle.IsChecked == false && !store.LoadSettings().MonitorApiCredits, "API checks are off by default");
        Check(!Descendants<Button>(guide).Single(button => AutomationProperties.GetName(button) == "Connect API account").IsEnabled,
            "Connect requires monitoring opt-in");
        Capture(guide, preview, "api-guide-off.png");
        toggle.IsChecked = true;
        await Settle(guide);
        Check(store.LoadSettings().MonitorApiCredits, "Guide stores explicit opt-in");
        Check(Descendants<Button>(guide).Single(button => AutomationProperties.GetName(button) == "Connect API account").IsEnabled,
            "The optional guide chapter exposes connection after opting in");
        Capture(guide, preview, "api-guide-on.png");
        guide.Dismiss();
        var groups = (ObservableCollection<AccountGroupViewModel>)typeof(MainWindow).GetField("visibleAccounts", Instance)!.GetValue(main)!;
        var api = groups.Single(group => group.IsApi);
        var codex = groups.Single(group => group.Name == api.Name && !group.IsApi);
        Check(codex.CanSwitch && !api.CanSwitch && !api.IsCurrent && api.DisplayName.StartsWith("API · "), "An identically named API row never merges with or becomes a Codex account");
        Check(api.FocusWindow is { Percent: "$59.00", HasProgress: true } &&
            api.FocusWindow.ResetText.Contains(api.FocusWindow.CompactResetText), "API shows USD and the day of expiry");
        api.ShowUsed = true;
        Check(api.FocusWindow!.Percent == "$41.00", "Used amounts follow the quota display setting"); api.ShowUsed = false;
        var apiSource = api.Sources.Single();
        var reading = new ApiCreditReading(59, 59, DateOnly.FromDateTime(DateTime.Today.AddDays(8)), 59);
        apiSource.SetSnapshot(reading.ToSnapshot(apiSource.Record.ApiOrganizationId!));
        await CheckCycleAllowanceEditor(main, apiSource);
        Check(api.FocusWindow is { Value: 59, Percent: "$59.00", RingCaption: "Remaining · 59%" }, "The Pro100 scale is 59% even when only $59 was issued");
        Check(api.FocusWindow!.ApiCycleTooltip == "Full-cycle allowance: US$100", "The tooltip identifies the denominator");
        api.ShowUsed = true;
        Check(api.FocusWindow is { Value: 0, Percent: "$0.00" }, "A prorated first grant is not spending");
        api.ShowUsed = false;
        apiSource.SetSnapshot((reading with { GrantedUsd = null }).ToSnapshot(apiSource.Record.ApiOrganizationId!));
        Check(api.FocusWindow is { Value: 59, Percent: "$59.00", HasAlertMeasure: false }, "A fresh balance uses the saved scale when grant details are not available");
        await Settle(main);
        ((ScrollViewer)main.FindName("CompactPanel")!).ScrollToBottom();
        await Settle(main);
        Capture(main, preview, "api-main.png");
        Check(Descendants<QuotaRing>(main).Any(ring => Equals(ring.DataContext, api) && ring.Value == 59), "The rendered API ring uses 59, not 0 or a full ring");

        var settings = main.GuideSettings;
        var full = (ItemsControl)main.FindName("AccountList")!;
        var simplified = (ItemsControl)main.FindName("SimplifiedAccounts")!;
        var strip = new TaskbarWidget(main, groups, null);
        var flyout = new TaskbarFlyout(strip, main, groups);
        var flyoutCards = (ItemsControl)flyout.FindName("AccountCards")!;
        void Apply() { typeof(MainWindow).GetMethod("ApplyAccountVisibility", Instance)!.Invoke(main, null); strip.RefreshApiVisibility(); flyout.RefreshApiVisibility(); }
        try
        {
            strip.SelectDisplayedAccount(api);
            Check(ReferenceEquals(strip.DataContext, api) && flyoutCards.Items.Contains(api) && full.Items.Contains(api) && simplified.Items.Contains(api),
                "Each component can show the API entry");
            settings.ShowApiInMain = false; Apply();
            Check(!full.Items.Contains(api) && simplified.Items.Contains(api) && flyoutCards.Items.Contains(api), "Full panel toggle is independent");
            settings.ShowApiInSimplified = false; Apply();
            Check(!simplified.Items.Contains(api) && flyoutCards.Items.Contains(api), "Simplified toggle is independent");
            settings.ShowApiInTaskbar = false; Apply();
            Check(strip.DataContext is AccountGroupViewModel { IsApi: false } && flyoutCards.Items.Contains(api), "Taskbar strip toggle is independent");
            settings.ShowApiInFlyout = false; Apply();
            Check(!flyoutCards.Items.Contains(api) && groups.Contains(api), "Flyout toggle hides only the card, not its monitoring");
            (settings.ShowApiInMain, settings.ShowApiInSimplified, settings.ShowApiInTaskbar, settings.ShowApiInFlyout) = (true, true, true, true);
            Apply();
            typeof(MainWindow).GetMethod("SetApiMonitoring", Instance)!.Invoke(main, [false]);
            Check(groups.All(group => !group.IsApi) && !store.LoadSettings().MonitorApiCredits, "Disabling monitoring removes API entries from all surfaces");
            typeof(MainWindow).GetMethod("SetApiMonitoring", Instance)!.Invoke(main, [true]);
            Check(groups.Count(group => group.IsApi) == 1, "Re-enabling restores just one row per organization");
        }
        finally { flyout.Close(); strip.Close(); }
        Console.WriteLine("PASS API opt-in guide, distinct account, USD and expiry, and all four visibility controls");
    }

    private static async Task CheckCycleAllowanceEditor(MainWindow main, AccountViewModel apiSource)
    {
        var interaction = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            var editor = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "Full-cycle API allowance");
            var field = Descendants<TextBox>(editor).Single();
            var save = Descendants<Button>(editor).Single(button => Equals(button.Content, "Save"));
            field.Text = "0";
            Check(!save.IsEnabled, "Zero is rejected as a divisor");
            Descendants<Button>(editor).Single(button => Equals(button.Content, "Pro100")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(field.Text == "100" && save.IsEnabled, "The Pro100 preset selects $100");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.ApplicationIdle);
        typeof(MainWindow).GetMethod("EditApiCycleAllowance", Instance)!.Invoke(main, [apiSource]);
        await interaction;
    }

    private static async Task CheckEmbeddedBilling(MainWindow main, AccountStore store)
    {
        const string org = "org-billing-fixture";
        // Real WebView2 navigation with local in-memory billing documents, no login or external requests.
        var environment = await ApiBillingBrowser.OpenProfileAsync(store, Guid.NewGuid().ToString("N"));
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(main).EnsureHandle());
        controller.IsVisible = false;
        try
        {
            var browser = controller.CoreWebView2;
            ApiBillingBrowser.Configure(browser);
            browser.AddWebResourceRequestedFilter("https://platform.openai.com/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            var sidebar = """<aside><button aria-haspopup="menu"><span>P</span><div><span>Personal</span><span>Organization</span></div></button></aside>""";
            const string balance = "<h3>Free trial</h3><div>API credit balance</div><div>$59.00</div><div>Next expiration: $59.00 on Dec 4, 2026.View credit grants</div>";
            var signedIn = true;
            var delayed = false;
            var loginPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            browser.WebResourceRequested += (_, eventArgs) =>
            {
                var path = new Uri(eventArgs.Request.Uri).AbsolutePath;
                if (!signedIn && path == "/settings/organization/billing/overview")
                {
                    eventArgs.Response = environment.CreateWebResourceResponse(null, 302, "Found", "Location: https://platform.openai.com/login");
                    return;
                }
                if (path == "/login") loginPage.TrySetResult();
                string body = path switch
                {
                    "/settings/organization/billing/overview" when delayed =>
                        $"""<div id="trial"></div><a href="https://chatgpt.com/redeem?api_organization_id={org}">Redeem gift card</a><template id="ready">{balance}</template><script>setTimeout(() => document.querySelector('#trial').append(document.querySelector('#ready').content), 1600)</script>""",
                    "/settings/organization/billing/overview" => balance,
                    "/settings/organization/general" => $"""<input aria-label="Organization ID" readonly value="{org}">""",
                    "/settings/organization/billing/credit-grants" => "<table><tr><td>Received</td><td>State</td><td>Balance</td><td>Expires</td></tr><tr><td>Nov 25, 2026</td><td>Available</td><td>US$59.00 / US$100.00</td><td>Dec 04, 2026</td></tr></table>",
                    _ => ""
                };
                var html = new MemoryStream(Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><main>{sidebar}{body}</main>"));
                eventArgs.Response = environment.CreateWebResourceResponse(html, 200, "OK", "Content-Type: text/html; charset=utf-8");
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (page, data) = await ApiBillingBrowser.ReadAsync(browser, org, timeout.Token);
            Check(page.OrganizationId == org && data is { RemainingUsd: 59, GrantedUsd: 100, ExpiringUsd: 59 }, "Foreground and background reader can resolve the organization and grant arithmetic without a wallet link");
            try { await ApiBillingBrowser.ReadAsync(browser, "org-wrong-fixture", timeout.Token); throw new InvalidOperationException("A different organization's balance must be rejected"); }
            catch (ApiBillingException) { }

            using var loginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            signedIn = false;
            delayed = true;
            var autoConnect = ApiBillingBrowser.WaitForConnectionAsync(browser, org, _ => false, _ => { }, loginTimeout.Token);
            await loginPage.Task.WaitAsync(loginTimeout.Token);
            Check(!autoConnect.IsCompleted, "Being on the login page does not complete the connection");
            signedIn = true;
            browser.Navigate("https://platform.openai.com/overview"); // Simulated successful return from the login provider.
            var (connected, hydrated) = await autoConnect;
            Check(connected.OrganizationId == org && hydrated is { RemainingUsd: 59, ExpiringUsd: 59 } &&
                hydrated.NextExpiry == new DateOnly(2026, 12, 4), "Login returns automatically and waits for the delayed balance instead of reading the wallet shell");
        }
        finally { controller.Close(); }
        Console.WriteLine("PASS WebView2 automatic post-login billing, delayed balance, real expiry wording and cross-organization protection");
    }

    private static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Task.Delay(250);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(Window window, string? folder, string name)
    {
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(folder, name)); png.Save(file);
    }
}
