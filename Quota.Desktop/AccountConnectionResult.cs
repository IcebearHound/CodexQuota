using System;
using System.Linq;

namespace Quota.Desktop;

public enum AccountConnectionOutcome { Saved, Canceled, Busy, CliMissing, NoAccounts, Failed, Demo }

public sealed record AccountConnectionResult(AccountConnectionOutcome Outcome, string? Error = null, string[]? Notices = null)
{
    public string Description => Summary + (Notices is { Length: > 0 } ? Environment.NewLine + string.Join(Environment.NewLine, Notices.Distinct().Select(L.T)) : "");
    private string Summary => Outcome switch
    {
        AccountConnectionOutcome.Saved => L.F("账号认证已保存。可以继续添加账号，或查看额度。", "Account credentials saved. Add another account or view quota."),
        AccountConnectionOutcome.Canceled => L.F("已取消本次连接。可以重试，也可以稍后在设置中连接。", "Connection canceled. Try again or connect later in Settings."),
        AccountConnectionOutcome.Busy => L.F("账号正在更新或切换，请等待完成后重试。", "An account update or switch is in progress. Wait for it to finish and try again."),
        AccountConnectionOutcome.CliMissing => L.F("请先在设置的通用页选择 Codex 程序，然后重新登录。", "Select the Codex executable in Settings → General, then try signing in again."),
        AccountConnectionOutcome.NoAccounts => L.F("请先在 CC Switch 中登录 Codex，再回来同步账号。也可以直接选择原生登录。", "Sign in to Codex in CC Switch, then sync again. You can also sign in here with Codex."),
        AccountConnectionOutcome.Demo => L.F("演示模式下可体验导览。请在正常启动后连接真实账号。", "Explore the guide in demo mode. Connect real accounts after starting normally."),
        _ => Error is { Length: > 0 } ? L.Error(Error) : L.F("连接未完成，请检查本地文件权限后重试。", "The connection could not be completed. Check local file permissions and try again.")
    };
}
