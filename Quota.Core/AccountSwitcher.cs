using System.Text;
using System.Text.Json;

namespace Quota.Core;

public sealed class AccountSwitcher(AccountStore store)
{
    public static string CurrentHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } path
        ? Path.GetFullPath(path) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public async Task<UsageSnapshot?> SwitchAsync(string cli, AccountRecord target, CancellationToken cancellationToken)
    {
        if (target.IsCurrent) return null;
        var usage = await new CodexService(store).RefreshAsync(cli, target, cancellationToken);
        var authentication = Path.Combine(store.ProfilePath(target.Id), "auth.json");
        await CommitAuthenticationAsync(authentication, CurrentHome, store.Root, usage.AccountId, cancellationToken);
        return usage;
    }

    public static async Task CommitAuthenticationAsync(string sourceFile, string targetHome, string backupDirectory, string? expectedAccountId, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(sourceFile, cancellationToken);
        using var document = JsonDocument.Parse(content);
        var auth = document.RootElement;
        if (auth.Str("auth_mode") != "chatgpt" || !auth.TryGetProperty("tokens", out var tokens) || string.IsNullOrWhiteSpace(tokens.Str("access_token")) || string.IsNullOrWhiteSpace(tokens.Str("refresh_token")) || string.IsNullOrWhiteSpace(tokens.Str("id_token")))
            throw new CodexException("账号认证尚未就绪，请重新登录后切换。");
        if (expectedAccountId is not null && tokens.Str("account_id") != expectedAccountId) throw new CodexException("账号认证与额度身份不一致，请刷新后重试。");

        Directory.CreateDirectory(targetHome);
        Directory.CreateDirectory(backupDirectory);
        AccountStore.RestrictDirectory(backupDirectory);
        var targetFile = Path.Combine(targetHome, "auth.json");
        var temporary = Path.Combine(targetHome, "quota-auth-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = Path.Combine(backupDirectory, "previous-codex-auth.json");
        using var transaction = new FileStream(Path.Combine(targetHome, "auth.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(targetFile))
            {
                var backupTemporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.Copy(targetFile, backupTemporary); File.Move(backupTemporary, backup, true); }
                finally { if (File.Exists(backupTemporary)) File.Delete(backupTemporary); }
            }
            File.Move(temporary, targetFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
