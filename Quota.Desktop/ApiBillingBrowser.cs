using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Quota.Core;

namespace Quota.Desktop;

internal static class ApiBillingBrowser
{
    private const string Origin = "https://platform.openai.com";
    private const string Billing = Origin + "/settings/organization/billing/";
    internal const string Overview = Billing + "overview";
    private const string Grants = Billing + "credit-grants";
    private const string General = Origin + "/settings/organization/general";

    // Read only rendered billing fields. No requests to undocumented endpoints, tokens or cookies.
    private const string ReadPage = """
        (() => {
            const main = document.querySelector('main');
            if (location.origin !== 'https://platform.openai.com' || !main ||
                !/^\/settings\/organization\/(general|billing\/(overview|credit-grants))\/?$/.test(location.pathname)) return null;
            const clean = text => (text || '').replace(/\u00a0|\u202f/g, ' ').trim();
            const sidebar = main.querySelector('aside') || document.querySelector('#mobile-navigation');
            const organization = [...(sidebar || main).querySelectorAll('button[aria-haspopup]')]
                .flatMap(button => [...button.querySelectorAll('*')])
                .find(node => node.children.length === 0 && /^(Organization|组织)$/.test(clean(node.innerText)));
            const name = clean(organization?.previousElementSibling?.innerText) ||
                clean(organization?.parentElement?.innerText.split('\n').find(s => !/^(Organization|组织)$/.test(clean(s))));
            const redeem = main.querySelector('a[href^="https://chatgpt.com/redeem"][href*="api_organization_id="]');
            const idField = location.pathname.endsWith('general') &&
                main.querySelector('input[readonly][aria-label="Organization ID"], input[readonly][aria-label="组织 ID"]');
            const tables = [...main.querySelectorAll('table')].map(table => [...table.rows].map(row => [...row.cells].map(cell => clean(cell.innerText))));
            const grants = tables.find(rows => rows[0]?.[0] === 'Received' && rows[0]?.[2] === 'Balance' ||
                rows[0]?.[0] === '领取日期' && rows[0]?.[2] === '余额');
            return {
                OrganizationId: redeem ? new URL(redeem.href).searchParams.get('api_organization_id') : idField?.value || null,
                OrganizationName: name || null,
                Text: location.pathname.endsWith('overview') ? clean(main.innerText).slice(0, 16000) : null,
                GrantRows: grants || null
            };
        })()
        """;

    internal static Task<CoreWebView2Environment> OpenProfileAsync(AccountStore store, string id) =>
        CoreWebView2Environment.CreateAsync(userDataFolder: store.CreateApiBrowserProfile(id),
            options: new() { Language = "en-US", AllowSingleSignOnUsingOSPrimaryAccount = false });

    internal static void Configure(CoreWebView2 browser)
    {
        browser.Settings.IsPasswordAutosaveEnabled = false;
        browser.Settings.IsGeneralAutofillEnabled = false;
        browser.Settings.IsWebMessageEnabled = false;
        browser.Settings.AreHostObjectsAllowed = false;
        browser.Settings.AreDevToolsEnabled = false;
        browser.DownloadStarting += (_, args) => args.Cancel = true;
        browser.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
    }

    internal static async Task<(ApiBillingPage Page, ApiCreditReading? Reading)> ReadAsync(CoreWebView2 browser,
        string? expectedOrg, CancellationToken cancellationToken, bool navigate = true, Func<string, bool>? alreadyConnected = null)
    {
        if (navigate) await NavigateAsync(browser, Overview, cancellationToken);
        // The organization switcher and wallet may appear before the trial balance finishes
        // loading. Only a parsed amount wins immediately; an incomplete page is not a zero balance.
        var overview = await WaitForPageAsync(browser, Overview, page => page.OrganizationName is not null && ApiCredits.HasTrialBalance(page.Text),
            cancellationToken, 44);
        if (overview is null && IsPage(browser.Source, Overview))
        {
            overview = await PeekAsync(browser, Overview, cancellationToken);
            if (overview?.Text?.Contains("Billing history", StringComparison.OrdinalIgnoreCase) != true &&
                overview?.Text?.Contains("账单历史", StringComparison.Ordinal) != true)
                throw new ApiBillingException(L.F("API 账单尚未加载完整，请稍后刷新。", "API billing has not loaded yet. Try refreshing shortly."));
        }
        if (overview is { } identifiedByName && !ApiCredits.IsOrganizationId(overview.OrganizationId))
        {
            await NavigateAsync(browser, General, cancellationToken);
            var general = await WaitForPageAsync(browser, General, page => ApiCredits.IsOrganizationId(page.OrganizationId), cancellationToken, 12);
            if (general?.OrganizationName != identifiedByName.OrganizationName) overview = null;
            else overview = identifiedByName with { OrganizationId = general?.OrganizationId };
        }
        if (overview is null || !ApiCredits.IsOrganizationId(overview.OrganizationId))
            throw new ApiBillingException(L.F("请登录 OpenAI Platform，并以组织所有者身份打开账单概览。", "Sign in to OpenAI Platform and open Billing overview as an organization owner."));
        if (expectedOrg is not null && overview.OrganizationId != expectedOrg)
            throw new ApiBillingException(L.F("当前组织与此 API 账号不符，请重新登录并选择原组织。", "The selected organization does not match this API account. Reconnect and select the original organization."));
        if (alreadyConnected?.Invoke(overview.OrganizationId!) == true)
            throw new ApiBillingException(L.F("这个 API 组织已连接。可在网页左侧切换到另一个组织。", "This API organization is already connected. To add another, switch organizations in the page sidebar."));
        var reading = ApiCredits.Read(overview);
        if (reading is null) return (overview, null);
        // The overview is authoritative for USD. The grant table is only used to enrich
        // the reading with a denominator or expiry details that can be reconciled.
        using var details = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        details.CancelAfter(TimeSpan.FromSeconds(9));
        try
        {
            await NavigateAsync(browser, Grants, details.Token);
            var grants = await WaitForPageAsync(browser, Grants, page => page.GrantRows is not null && page.OrganizationName is not null,
                details.Token, 12);
            return (overview, ApiCredits.Read(overview, grants));
        }
        catch (ApiBillingException) { return (overview, reading); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (overview, reading); }
    }

    internal static async Task<(ApiBillingPage Page, ApiCreditReading? Reading)> WaitForConnectionAsync(CoreWebView2 browser,
        string? expectedOrg, Func<string, bool> alreadyConnected, Action<string> status, CancellationToken cancel)
    {
        var revision = 0;
        var reading = false;
        var redirectedFromLanding = false;
        (int Revision, string Org)? tried = null;
        void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs args) { if (!reading) revision++; }
        browser.NavigationCompleted += Navigated;
        try
        {
            browser.Navigate(Overview);
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                // The usual sign-in return is Billing overview itself. Some providers return
                // to the Platform home instead. Follow that trusted landing once, not a login page.
                if (!redirectedFromLanding && IsPlatformLanding(browser.Source))
                {
                    redirectedFromLanding = true;
                    browser.Navigate(Overview);
                }
                var preview = await PeekAsync(browser, Overview, cancel);
                if (preview?.OrganizationName is { Length: > 0 } name)
                {
                    var key = (revision, preview.OrganizationId ?? name);
                    if (key != tried)
                    {
                        tried = key;
                        reading = true;
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        attempt.CancelAfter(TimeSpan.FromSeconds(42));
                        status(L.F("登录已识别，正在读取 API 赠送额度…", "Signed in. Reading the API promotional balance…"));
                        try { return await ReadAsync(browser, expectedOrg, attempt.Token, navigate: false, alreadyConnected); }
                        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                        {
                            status(L.F("读取超时。可刷新账单页面，或切换组织后自动重试。", "The read timed out. Reload billing or switch organizations to retry."));
                        }
                        catch (ApiBillingException error) when (!cancel.IsCancellationRequested) { status(error.Message); }
                        finally
                        {
                            // If resolving the identity visited General, return to Billing without
                            // creating another attempt for the same organization.
                            if (!cancel.IsCancellationRequested && IsPage(browser.Source, General))
                            {
                                try { await NavigateAsync(browser, Overview, attempt.Token); }
                                catch (Exception error) when (!cancel.IsCancellationRequested && error is (ApiBillingException or OperationCanceledException)) { }
                            }
                            reading = false;
                        }
                    }
                }
                await Task.Delay(500, cancel);
            }
        }
        finally { browser.NavigationCompleted -= Navigated; }
    }

    private static bool IsPlatformLanding(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.GetLeftPart(UriPartial.Authority) == Origin &&
        url.AbsolutePath.TrimEnd('/') is "" or "/overview" or "/playground";

    private static bool IsPage(string? uri, string expected) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsDefaultPort &&
        string.Equals(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/'), expected, StringComparison.Ordinal);

    internal static bool IsBillingPage(string? uri) => IsPage(uri, Overview) || IsPage(uri, Grants);
    internal static bool IsReadPage(string? uri) => IsBillingPage(uri) || IsPage(uri, General);

    private static async Task NavigateAsync(CoreWebView2 browser, string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Started(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId is null && IsPage(args.Uri, url)) navigationId = args.NavigationId;
        }
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId == args.NavigationId) completed.TrySetResult(args);
        }
        browser.NavigationStarting += Started;
        browser.NavigationCompleted += Finished;
        try
        {
            browser.Navigate(url);
            var result = await completed.Task.WaitAsync(cancellationToken);
            if (!result.IsSuccess && result.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                throw new ApiBillingException(L.F("账单页面未能加载，请检查网络后刷新。", "The billing page could not load. Check your connection and refresh."));
        }
        finally { browser.NavigationStarting -= Started; browser.NavigationCompleted -= Finished; }
    }

    private static async Task<ApiBillingPage?> WaitForPageAsync(CoreWebView2 browser, string url,
        Func<ApiBillingPage, bool> ready, CancellationToken cancellationToken, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPage(browser.Source, url)) return null;
            if (await PeekAsync(browser, url, cancellationToken) is { } page && ready(page)) return page;
            await Task.Delay(500, cancellationToken);
        }
        return null;
    }

    private static async Task<ApiBillingPage?> PeekAsync(CoreWebView2 browser, string url, CancellationToken token)
    {
        if (!IsPage(browser.Source, url)) return null;
        var json = await browser.ExecuteScriptAsync(ReadPage).WaitAsync(token);
        return IsPage(browser.Source, url) && json.Length < 100_000 ? JsonSerializer.Deserialize<ApiBillingPage>(json) : null;
    }

    internal static bool IsExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or COMException or WebView2RuntimeNotFoundException or InvalidOperationException or JsonException;

    internal static string ErrorMessage(Exception exception) => exception switch
    {
        ApiBillingException => exception.Message,
        WebView2RuntimeNotFoundException => L.F("请安装 Microsoft Edge WebView2 运行时后重试。", "Install Microsoft Edge WebView2 Runtime, then try again."),
        _ => L.F("未能读取 API 账单，请检查网络或重新连接。", "Could not read API billing. Check your connection or reconnect.")
    };
}

internal sealed class ApiBillingException(string message) : IOException(message);
