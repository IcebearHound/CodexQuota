using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quota.Core;

public sealed class ImportCandidate
{
    public string Key { get; }
    public string? Email { get; }
    public string Source { get; }
    public string Name { get; }
    public bool HasAccessToken { get; }
    private readonly JsonElement authentication;
    internal long UpdatedAt { get; }

    internal ImportCandidate(string key, string? email, string source, JsonElement authentication, long updatedAt, bool hasAccessToken, string? name)
    {
        Key = key; Email = email; Source = source; this.authentication = authentication;
        UpdatedAt = updatedAt; HasAccessToken = hasAccessToken;
        Name = AccountStore.CleanName(name ?? email ?? "ChatGPT");
    }
    internal void WriteTo(string profilePath)
    {
        // The caller creates the restricted profile first. Only the Codex-native fields are copied.
        var path = Path.Combine(profilePath, "auth.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        authentication.WriteTo(writer); writer.Flush(); stream.Flush(true);
    }
}

public sealed class ImportScan
{
    public List<ImportCandidate> Accounts { get; } = [];
    public List<string> Notices { get; } = [];
}

public static class CcSwitchImport
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-switch");

    public static ImportScan Scan(string location)
    {
        var scan = new ImportScan();
        var files = Directory.Exists(location)
            ? new[] { Path.Combine(location, "codex_oauth_auth.json"), Path.Combine(location, "codex-login-stash.json") }
            : new[] { location };
        var found = false;
        foreach (var path in files)
        {
            if (!File.Exists(path)) continue;
            found = true;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 16_000_000) { scan.Notices.Add("认证文件超过读取大小限制。"); continue; }
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in accounts.EnumerateObject())
                    {
                        var candidate = ManagedAccount(entry.Value);
                        if (candidate is not null) scan.Accounts.Add(candidate);
                        else scan.Notices.Add("有账号缺少可导入的身份或刷新凭据，需要在 CC Switch 中重新认证。");
                    }
                }
                else if (root.TryGetProperty("logins", out var logins) && logins.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in logins.EnumerateObject())
                    {
                        var candidate = NativeAccount(entry.Value);
                        if (candidate is not null) scan.Accounts.Add(candidate);
                        else scan.Notices.Add("有账号缺少可导入的身份或刷新凭据，需要在 CC Switch 中重新认证。");
                    }
                }
                else if (root.TryGetProperty("tokens", out _))
                {
                    if (NativeAccount(root) is { } candidate) scan.Accounts.Add(candidate);
                    else scan.Notices.Add("有账号缺少可导入的身份或刷新凭据，需要在 CC Switch 中重新认证。");
                }
                else scan.Notices.Add("所选文件没有受支持的 Codex 认证记录。");
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                scan.Notices.Add("认证文件暂时无法读取。请确认位置，或等待 CC Switch 完成保存后重试。");
            }
        }
        if (!found) scan.Notices.Add("没有找到 CC Switch 的 Codex 认证文件，请选择其数据目录中的 JSON 文件。");

        var unique = scan.Accounts.GroupBy(account => account.Key)
            .Select(group => group.OrderByDescending(account => account.UpdatedAt).ThenByDescending(account => account.HasAccessToken).First()).ToList();
        scan.Accounts.Clear(); scan.Accounts.AddRange(unique);
        return scan;
    }

    public static List<AccountRecord> Import(AccountStore store, IEnumerable<AccountRecord> existing, IEnumerable<ImportCandidate> selected)
    {
        var records = existing.Where(record => !record.IsCurrent).ToList();
        var keys = records.Where(record => record.ImportKey is not null).Select(record => record.ImportKey!).ToHashSet(StringComparer.Ordinal);
        var added = new List<AccountRecord>();
        try
        {
            foreach (var candidate in selected)
            {
                if (!keys.Add(candidate.Key)) continue;
                var record = new AccountRecord { Name = candidate.Name, Email = candidate.Email, ImportKey = candidate.Key };
                added.Add(record);
                var profile = store.CreateProfile(record.Id);
                candidate.WriteTo(profile);
            }
            if (added.Count > 0) store.SaveAccounts(records.Concat(added));
            return added;
        }
        catch
        {
            foreach (var record in added)
            {
                try { store.DeleteProfile(record.Id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw new IOException("导入未能保存，请检查组件数据目录的写入权限后重试。");
        }
    }

    private static ImportCandidate? ManagedAccount(JsonElement entry)
    {
        var identityToken = entry.Str("id_token");
        var refreshToken = entry.Str("refresh_token");
        var claims = DecodeClaims(identityToken);
        if (string.IsNullOrWhiteSpace(refreshToken) || claims is null) return null;
        var workspace = entry.Str("chatgpt_account_id") ?? Workspace(claims.Value);
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var milliseconds = entry.Long("token_updated_at_ms") ?? 0;
        // CC Switch keeps access tokens in memory. An old refresh timestamp asks Codex to renew the imported session.
        var auth = NativeAuthentication(identityToken!, "", refreshToken, workspace, DateTimeOffset.UnixEpoch.ToString("O"));
        return Candidate(claims.Value, workspace, entry.Str("email"), "CC Switch OAuth", auth, milliseconds, false, entry.Str("name") ?? entry.Str("label") ?? entry.Str("display_name"));
    }

    private static ImportCandidate? NativeAccount(JsonElement auth)
    {
        if (auth.ValueKind != JsonValueKind.Object || !auth.TryGetProperty("tokens", out var tokens)) return null;
        var identityToken = tokens.Str("id_token"); var refreshToken = tokens.Str("refresh_token");
        var claims = DecodeClaims(identityToken);
        if (string.IsNullOrWhiteSpace(refreshToken) || claims is null) return null;
        var workspace = tokens.Str("account_id") ?? Workspace(claims.Value);
        if (string.IsNullOrWhiteSpace(workspace)) return null;
        var accessToken = tokens.Str("access_token") ?? "";
        var updatedAt = DateTimeOffset.TryParse(auth.Str("last_refresh"), out var time) ? time.ToUnixTimeMilliseconds() : 0;
        var timestamp = accessToken.Length == 0 ? DateTimeOffset.UnixEpoch.ToString("O") : auth.Str("last_refresh") ?? DateTimeOffset.UnixEpoch.ToString("O");
        var native = NativeAuthentication(identityToken!, accessToken, refreshToken, workspace, timestamp);
        return Candidate(claims.Value, workspace, null, "CC Switch 登录暂存", native, updatedAt, accessToken.Length > 0, auth.Str("name") ?? auth.Str("label"));
    }

    private static ImportCandidate? Candidate(JsonElement claims, string workspace, string? email, string source, JsonElement auth, long updated, bool access, string? name)
    {
        var subject = claims.Str("sub");
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\n" + workspace)));
        return new(key, email ?? claims.Str("email"), source, auth, updated, access, name);
    }

    public static List<AccountRecord> Synchronize(AccountStore store, IEnumerable<AccountRecord> existing, IEnumerable<ImportCandidate> candidates)
    {
        var records = existing.Where(record => !record.IsCurrent).ToList();
        var staged = new List<AccountRecord>();
        var replaced = new List<AccountRecord>();
        try
        {
            foreach (var candidate in candidates.DistinctBy(candidate => candidate.Key))
            {
                var previous = records.FirstOrDefault(record => record.ImportKey == candidate.Key);
                var next = new AccountRecord { ImportKey = candidate.Key, Email = candidate.Email, Name = previous?.CustomName == true ? previous.Name : candidate.Name, CustomName = previous?.CustomName ?? false };
                staged.Add(next);
                candidate.WriteTo(store.CreateProfile(next.Id));
                if (previous is not null)
                {
                    if (store.LoadCache(previous.Id) is { } cache) store.SaveCache(next.Id, cache);
                    records.Remove(previous); replaced.Add(previous);
                }
                records.Add(next);
            }
            store.SaveAccounts(records);
        }
        catch
        {
            foreach (var record in staged) { try { store.DeleteProfile(record.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            throw;
        }
        foreach (var record in replaced) { try { store.DeleteProfile(record.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return records;
    }

    private static JsonElement NativeAuthentication(string identity, string access, string refresh, string workspace, string timestamp)
        => JsonSerializer.SerializeToElement(new
        {
            auth_mode = "chatgpt",
            OPENAI_API_KEY = (string?)null,
            tokens = new { id_token = identity, access_token = access, refresh_token = refresh, account_id = workspace },
            last_refresh = timestamp
        });

    private static JsonElement? DecodeClaims(string? token)
    {
        if (token is null) return null;
        var segments = token.Split('.');
        if (segments.Length != 3 || segments[1].Length > 100_000) return null;
        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    private static string? Workspace(JsonElement claims)
        => claims.TryGetProperty("https://api.openai.com/auth", out var auth) ? auth.Str("chatgpt_account_id") : null;
}
