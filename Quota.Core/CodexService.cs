using System.Diagnostics;
using System.Text.Json;

namespace Quota.Core;

public sealed class CodexService(AccountStore store, Func<string, string?, CodexClient>? factory = null, Action<Uri>? openBrowser = null)
{
    private CodexClient CreateClient(string cli, string? profile) => factory is null ? new CodexClient(cli, profile) : factory(cli, profile);
    public async Task<UsageSnapshot> RefreshAsync(string cli, AccountRecord account, CancellationToken ct)
    {
        if (!account.IsCurrent && !File.Exists(Path.Combine(store.ProfilePath(account.Id), "config.toml"))) throw new CodexException("此账号的本地登录目录已丢失，请重新登录。");
        await using var client = CreateClient(cli, account.IsCurrent ? null : store.ProfilePath(account.Id));
        return await client.ReadUsageAsync(ct);
    }
    public async Task<string?> LoginAsync(string cli, string profileId, Action<string> progress, CancellationToken ct)
    {
        var path = store.ProfilePath(profileId);
        await using var client = CreateClient(cli, path);
        await client.InitializeAsync(ct);
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += (method, data) => { if (method == "account/login/completed" && data.ValueKind == JsonValueKind.Object) completed.TrySetResult(data); };
        var start = await client.RequestAsync("account/login/start", new { type = "chatgpt" }, ct);
        var loginId = start.Str("loginId");
        try
        {
            var authUrl = start.Str("authUrl");
            if (authUrl is null || !Uri.TryCreate(authUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host == "auth.openai.com" || uri.Host == "chatgpt.com")) throw new CodexException("Codex 未返回有效的官方登录地址，请更新 Codex。 ");
            try { if (openBrowser is null) Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); else openBrowser(uri); }
            catch (System.ComponentModel.Win32Exception) { throw new CodexException("无法打开默认浏览器，请检查 Windows 默认浏览器设置。"); }
            progress("请在浏览器中完成登录，窗口会自动继续。");
            JsonElement result;
            using var loginDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loginDeadline.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                var login = completed.Task.WaitAsync(loginDeadline.Token);
                if (await Task.WhenAny(login, client.Completion) == client.Completion && !completed.Task.IsCompleted) throw new CodexException("登录连接已结束，请重试。");
                result = await login;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CodexException("登录等待已超时，请重新尝试。"); }
            finally { loginDeadline.Cancel(); }
            if (result.Bool("success") != true) throw new CodexException("登录未完成。请重新登录并确认授权。");
            if (loginId is not null && result.Str("loginId") != loginId) throw new CodexException("登录会话不匹配，请重新尝试。");
            var account = await client.RequestAsync("account/read", new { refreshToken = false }, ct);
            if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("account", out var data) || data.Str("type") != "chatgpt") throw new CodexException("未能验证登录账号，请重试。");
            return data.Str("email");
        }
        catch
        {
            if (loginId is not null) { try { await client.RequestAsync("account/login/cancel", new { loginId }, CancellationToken.None, TimeSpan.FromSeconds(2)); } catch (Exception e) when (e is CodexException or OperationCanceledException or ObjectDisposedException) { } }
            throw;
        }
    }
}
