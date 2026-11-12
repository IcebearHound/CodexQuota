using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Quota.Core;

public sealed class CodexException(string message) : Exception(message);

public sealed class CodexClient : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<int, byte> OwnedProcesses = new();
    internal static bool OwnsProcess(int id) => OwnedProcesses.ContainsKey(id);
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private long nextId;
    private bool disposed;
    private readonly EventHandler onHostExit;
    public Task Completion => reader;
    public event Action<string, JsonElement>? Notification;
    public CodexClient(string executable, string? profile, IEnumerable<string>? launchArguments = null)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in launchArguments ?? ["app-server"]) start.ArgumentList.Add(arg);
        if (profile is not null)
        {
            start.Environment["CODEX_HOME"] = profile;
            foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID" }) start.Environment.Remove(key);
        }
        process = new Process { StartInfo = start };
        try { process.Start(); OwnedProcesses[process.Id] = 0; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { process.Dispose(); throw new CodexException("无法启动 Codex，请在设置中选择 codex.exe。"); }
        onHostExit = (_, _) => StopProcess();
        AppDomain.CurrentDomain.ProcessExit += onHostExit;
        // Drain stderr without saving potentially sensitive diagnostics.
        _ = DrainErrorsAsync();
        reader = ReadLoopAsync();
    }
    private async Task DrainErrorsAsync() { try { while (await process.StandardError.ReadLineAsync(lifetime.Token) is not null) { } } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { } }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 4_000_000) throw new CodexException("Codex 响应过大。");
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) && pending.TryRemove(n, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error)) completion.TrySetException(new CodexException(TranslateError(error)));
                        else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new CodexException("Codex 返回了无效响应。"));
                    }
                    else if (root.Str("method") is { } method && root.TryGetProperty("params", out var p)) Notification?.Invoke(method, p.Clone());
                }
            }
            FailPending(new CodexException("Codex 连接已结束，请重试。"));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or CodexException) { FailPending(new CodexException("Codex 连接中断，请重试。")); }
    }
    private void FailPending(Exception error) { foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error); }
    private static string TranslateError(JsonElement error)
    {
        if (error.Int("code") == -32601) return "此 Codex 版本不支持所需功能，请更新 Codex 后重试。";
        var message = error.Str("message") ?? "";
        if (message.Contains("not logged", StringComparison.OrdinalIgnoreCase) || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("401", StringComparison.OrdinalIgnoreCase)) return "登录已失效，请重新登录后重试。";
        return "Codex 未能完成请求。请检查登录或更新 Codex 后重试。";
    }
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(25));
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await writer.WaitAsync(deadline.Token);
            try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
            finally { writer.Release(); }
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested) { throw new CodexException("读取超时。保留上次额度，稍后可重试。"); }
        catch (IOException) { throw new CodexException("Codex 连接中断，请重试。"); }
        finally { pending.TryRemove(id, out _); }
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new { clientInfo = new { name = "codex_quota_widget", title = "Codex Quota", version = "2.0.0" } }, ct);
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
        await process.StandardInput.FlushAsync(ct);
    }
    public async Task<UsageSnapshot> ReadUsageAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var account = await RequestAsync("account/read", new { refreshToken = false }, ct);
        if (!account.TryGetProperty("account", out var a) || a.ValueKind == JsonValueKind.Null) throw new CodexException("账号尚未登录，或登录已失效。请重新登录。");
        if (a.Str("type") == "apiKey") throw new CodexException("当前使用 API Key。订阅额度仅适用于 ChatGPT 登录账号。");
        var usage = await RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = false }, ct);
        if (account.TryGetProperty("workspaceRouting", out var routing) && routing.Str("chatgptAccountId") is { } expectedId && usage.Str("accountId") is { } actualId && expectedId != actualId)
            throw new CodexException("读取时账号发生变化，请刷新后重试。");
        return UsageSnapshot.Parse(usage, account);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel(); FailPending(new OperationCanceledException());
        StopProcess();
        try { await reader; } catch (OperationCanceledException) { }
        AppDomain.CurrentDomain.ProcessExit -= onHostExit;
        OwnedProcesses.TryRemove(process.Id, out _);
        process.Dispose(); lifetime.Dispose();
    }
    private void StopProcess()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
}
