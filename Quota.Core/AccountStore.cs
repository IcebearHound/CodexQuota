using System.Security.AccessControl;
using System.Security.Principal;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quota.Core;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public string? RecoveryNotice { get; private set; }
    public AccountStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public List<AccountRecord> LoadAccounts()
    {
        var modern = Path.Combine(Root, "accounts-v2.json");
        var hasModernStore = File.Exists(modern) || File.Exists(modern + ".bak");
        var accounts = Read<List<AccountRecord>>("accounts-v2.json");
        // Legacy data is an initial migration source, never a recovery copy for a newer list.
        if (accounts is null && !hasModernStore && File.Exists(Path.Combine(Root, "accounts.json"))) accounts = Read<List<AccountRecord>>("accounts.json");
        return (accounts ?? []).Where(a => a is not null && IsManagedId(a.Id)).DistinctBy(a => a.Id).Select(a => { a.Name = CleanName(a.Name); return a; }).ToList();
    }
    public void SaveAccounts(IEnumerable<AccountRecord> accounts) => Write("accounts-v2.json", accounts.Where(a => !a.IsCurrent).ToList());
    public CodexSessionIssueState? LoadSessionIssueState() => Read<CodexSessionIssueState>("session-issues.json");
    public void SaveSessionIssueState(CodexSessionIssueState state) => Write("session-issues.json", state);
    public CodexActivityJournal? LoadActivityJournal() => Read<CodexActivityJournal>("activity-state.json");
    public void SaveActivityJournal(CodexActivityJournal journal) => Write("activity-state.json", journal);
    public AlertState? LoadAlertState() => Read<AlertState>("notification-state.json");
    public void SaveAlertState(AlertState state) => Write("notification-state.json", state);
    public WidgetSettings LoadSettings()
    {
        var settings = Read<WidgetSettings>("settings.json") ?? new();
        if (settings.LayoutVersion < 2)
        {
            settings.Compact = true;
            settings.LayoutVersion = 2;
            if (settings.Width == 460 && settings.Height == 720) { settings.Width = 400; settings.Height = 442; }
        }
        if (settings.Theme is not ("system" or "dark" or "light")) settings.Theme = "system";
        if (settings.Palette is not ("mint" or "ocean" or "violet" or "amber")) settings.Palette = "mint";
        if (settings.Material is not ("solid" or "glass")) settings.Material = "solid";
        if (settings.Language is not ("zh-CN" or "en")) settings.Language = "zh-CN";
        if (settings.TaskbarPlacement is not ("auto" or "left" or "right")) settings.TaskbarPlacement = "auto";
        settings.GuideVersion = Math.Max(0, settings.GuideVersion);
        settings.LowQuotaThreshold = double.IsFinite(settings.LowQuotaThreshold) ? Math.Clamp(settings.LowQuotaThreshold, 1, 50) : 5;
        settings.CardExpirations ??= [];
        settings.CardReminderDays ??= "3,1";
        if (settings.LayoutVersion < 3)
        {
            if (settings.Compact && settings.Width == 400 && settings.Height == 442) settings.Height = 464;
            settings.LayoutVersion = 3;
        }
        settings.DesktopWidgetEnabled = false;
        settings.LayoutVersion = Math.Max(settings.LayoutVersion, 5);
        if (settings.SimplifiedLayout is not ("ring" or "bar")) settings.SimplifiedLayout = "ring";
        if (settings.TaskbarVersion < 1) { settings.TaskbarWidgetEnabled = true; settings.TaskbarVersion = 1; }
        settings.TaskbarOffset = double.IsFinite(settings.TaskbarOffset) ? Math.Clamp(settings.TaskbarOffset, 0, 10000) : 8;
        if (!new[] { 1, 5, 15, 30 }.Contains(settings.RefreshMinutes)) settings.RefreshMinutes = 5;
        settings.Width = double.IsFinite(settings.Width) ? Math.Clamp(settings.Width, 340, 900) : 400;
        settings.Height = double.IsFinite(settings.Height) ? Math.Clamp(settings.Height, 260, 1400) : 442;
        if (settings.Left is { } l && (!double.IsFinite(l) || Math.Abs(l) > 1_000_000)) settings.Left = null;
        if (settings.Top is { } t && (!double.IsFinite(t) || Math.Abs(t) > 1_000_000)) settings.Top = null;
        if (settings.WidgetLeft is { } widgetLeft && (!double.IsFinite(widgetLeft) || Math.Abs(widgetLeft) > 1_000_000)) settings.WidgetLeft = null;
        if (settings.WidgetTop is { } widgetTop && (!double.IsFinite(widgetTop) || Math.Abs(widgetTop) > 1_000_000)) settings.WidgetTop = null;
        if (settings.WidgetPixelLeft is < -1_000_000 or > 1_000_000) settings.WidgetPixelLeft = null;
        if (settings.WidgetPixelTop is < -1_000_000 or > 1_000_000) settings.WidgetPixelTop = null;
        return settings;
    }
    public void SaveSettings(WidgetSettings settings) => Write("settings.json", settings);
    public UsageSnapshot? LoadCache(string id)
    {
        ValidateId(id); var snapshot = Read<UsageSnapshot>($"cache/{id}.json");
        if (snapshot is null || snapshot.FetchedAt == default) return null;
        snapshot.Buckets = (snapshot.Buckets ?? []).Where(b => b is not null).ToList();
        snapshot.ResetCredits = (snapshot.ResetCredits ?? []).Where(credit => credit is not null && !string.IsNullOrWhiteSpace(credit.Id)).DistinctBy(credit => credit.Id).ToList();
        foreach (var credit in snapshot.ResetCredits)
            if (credit.ExpiresAt is < 0 or >= 253402300799) { credit.ExpiresAt = null; credit.ExpiryKnown = false; }
        foreach (var bucket in snapshot.Buckets) { bucket.Name ??= "Codex"; bucket.Windows = (bucket.Windows ?? []).Where(w => w is not null && double.IsFinite(w.UsedPercent) && w.UsedPercent >= 0).ToList(); }
        return snapshot;
    }
    public void SaveCache(string id, UsageSnapshot snapshot) { ValidateId(id); Write($"cache/{id}.json", snapshot); }
    public string ProfilePath(string id)
    {
        if (!IsManagedId(id)) throw new ArgumentException("无效账号 ID");
        var parent = Path.Combine(Root, "profiles");
        if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("账号目录不能是链接或联接目录。");
        return Path.Combine(parent, id);
    }
    public string CreateProfile(string id)
    {
        var path = ProfilePath(id); Directory.CreateDirectory(path);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("账号目录不能是链接或联接目录。");
        RestrictDirectory(path);
        File.WriteAllText(Path.Combine(path, "config.toml"), "cli_auth_credentials_store = \"file\"\n", new UTF8Encoding(false));
        return path;
    }
    internal static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
            acl.SetOwner(user);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
    }
    public void DeleteProfile(string id)
    {
        var path = ProfilePath(id);
        if (Directory.Exists(path))
        {
            // Never follow junctions or symlinks while deleting a profile.
            DeleteDirectoryWithoutLinks(new DirectoryInfo(path));
        }
        var cache = Path.Combine(Root, "cache", id + ".json");
        if (File.Exists(cache)) File.Delete(cache);
        if (File.Exists(cache + ".bak")) File.Delete(cache + ".bak");
    }
    private static void DeleteDirectoryWithoutLinks(DirectoryInfo directory)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) { directory.Delete(); return; }
        foreach (var file in directory.GetFiles()) file.Delete();
        foreach (var child in directory.GetDirectories()) DeleteDirectoryWithoutLinks(child);
        directory.Delete();
    }
    public static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        var result = new StringBuilder(40);
        var elements = StringInfo.GetTextElementEnumerator(cleaned);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (result.Length + element.Length > 40) break;
            foreach (var rune in element.EnumerateRunes()) result.Append(rune.ToString());
        }
        return result.Length == 0 ? "未命名账号" : result.ToString();
    }
    private static bool IsManagedId(string? id) => id is not null && Guid.TryParseExact(id, "N", out _);
    private static void ValidateId(string id) { if (id != "current" && !IsManagedId(id)) throw new ArgumentException("无效账号 ID"); }
    private T? Read<T>(string relative)
    {
        var path = Path.Combine(Root, relative);
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try { var result = JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), JsonOptions); if (result is null) { RecoveryNotice = "部分本地数据无效，已尝试恢复备份。"; continue; } if (candidate.EndsWith(".bak", StringComparison.Ordinal)) RecoveryNotice = "配置已从备份恢复。"; return result; }
            catch (JsonException) { RecoveryNotice = "部分本地配置损坏，已尝试恢复备份。"; }
            catch (IOException) { RecoveryNotice = "部分本地配置暂时无法读取。"; }
            catch (UnauthorizedAccessException) { RecoveryNotice = "部分本地配置没有读取权限。"; }
        }
        return default;
    }
    private void Write<T>(string relative, T value)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                // Do not replace the last good backup with an invalid primary after recovery.
                var backup = IsReadable<T>(path) ? path + ".bak" : null;
                File.Replace(temp, path, backup, true);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool IsReadable<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) is not null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }
}
