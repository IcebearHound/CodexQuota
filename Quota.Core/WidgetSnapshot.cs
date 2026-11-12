using System.Text.Json;

namespace Quota.Core;
public sealed class WidgetAccount
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Plan { get; set; } = "";
    public string? SourceId { get; set; }
    public bool IsCurrent { get; set; }
    public double? Value { get; set; }
    public double? Remaining { get; set; }
    public string Reset { get; set; } = "暂无额度";
    public string Status { get; set; } = "等待更新";
    public string WindowLabel { get; set; } = "";
}
public sealed class WidgetSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool ShowUsed { get; set; }
    public string Language { get; set; } = "zh-CN";
    public double LowThreshold { get; set; } = 5;
    public List<WidgetAccount> Accounts { get; set; } = [];
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget");
    public static WidgetSnapshot Read(string directory)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<WidgetSnapshot>(File.ReadAllText(Path.Combine(directory, "widget-snapshot.json"))) ?? new();
            snapshot.Accounts = (snapshot.Accounts ?? []).Where(account => account is not null).ToList();
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "widget-snapshot.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
