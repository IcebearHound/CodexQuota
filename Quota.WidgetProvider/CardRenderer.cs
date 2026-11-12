using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quota.Core;

namespace Quota.WidgetProvider;
public static class CardRenderer
{
    public static string Render(WidgetSnapshot snapshot, string size, bool? usedOverride = null, int page = 0, string? notice = null)
    {
        var used = usedOverride ?? snapshot.ShowUsed;
        string T(string zh, string en) => snapshot.Language == "en" ? en : zh;
        var accounts = snapshot.Accounts.OrderByDescending(account => account.IsCurrent).ToArray();
        var pageSize = size == "small" ? 2 : size == "large" ? 6 : 4;
        var pageCount = Math.Max(1, (accounts.Length + pageSize - 1) / pageSize);
        var visible = accounts.Skip(Math.Clamp(page, 0, pageCount - 1) * pageSize).Take(pageSize).ToArray();
        var body = new JsonArray();
        if (accounts.Length == 0) body.Add(Text(T("打开 Codex Quota 连接账号", "Open Codex Quota to connect accounts"), "default"));
        else if (size == "small")
        {
            var columns = new JsonArray();
            foreach (var account in visible)
            {
                var column = Column(new JsonArray(Text(account.Name, "small"), Ring(account, used, 76, snapshot.LowThreshold), Text(account.Reset, "small")), "stretch");
                if (!account.IsCurrent && account.SourceId is not null) column["selectAction"] = Action("switch", account.SourceId);
                columns.Add(column);
            }
            body.Add(new JsonObject { ["type"] = "ColumnSet", ["columns"] = columns });
        }
        else
        {
            var first = visible[0];
            var left = new JsonArray(Text(first.Name, "small"), Ring(first, used, 110, snapshot.LowThreshold), Text(first.Reset, "small"));
            var right = new JsonArray();
            foreach (var account in visible.Skip(1))
            {
                var value = Value(account, used);
                var row = new JsonObject { ["type"] = "Container", ["spacing"] = "small", ["items"] = new JsonArray(Text(account.Name + "  " + (value is null ? "—" : $"{value:0}%"), "small"), Bar(value), Text(account.Reset, "small")) };
                if (account.Remaining <= snapshot.LowThreshold)
                {
                    ((JsonObject)((JsonArray)row["items"]!)[0]!)["color"] = "attention";
                    ((JsonArray)row["items"]!).Add(new JsonObject { ["type"] = "TextBlock", ["text"] = account.Remaining <= 0 ? T("额度已用尽", "Quota exhausted") : T("额度即将用尽", "Quota running low"), ["color"] = "attention", ["size"] = "small", ["spacing"] = "none" });
                }
                if (!account.IsCurrent && account.SourceId is not null) row["selectAction"] = Action("switch", account.SourceId);
                right.Add(row);
            }
            if (right.Count == 0) right.Add(Text(first.Plan, "default"));
            var focus = Column(left, "120px");
            if (!first.IsCurrent && first.SourceId is not null) focus["selectAction"] = Action("switch", first.SourceId);
            body.Add(new JsonObject { ["type"] = "ColumnSet", ["columns"] = new JsonArray(focus, Column(right, "stretch")) });
        }
        body.Add(Text(used ? T("已用量", "Used") : T("剩余用量", "Remaining"), "small"));
        if (notice is not null) body.Add(Text(notice, "small"));
        var actions = new JsonArray(
            new JsonObject { ["type"] = "Action.Execute", ["title"] = T("刷新", "Refresh"), ["verb"] = "refresh" },
            new JsonObject { ["type"] = "Action.Execute", ["title"] = used ? T("显示剩余", "Show remaining") : T("显示已用", "Show used"), ["verb"] = "toggle" },
            new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = T("打开面板", "Open panel"), ["url"] = "codexquota://open" });
        if (pageCount > 1) actions.Add(new JsonObject { ["type"] = "Action.Execute", ["title"] = T("下一页", "Next"), ["verb"] = "next" });
        return new JsonObject { ["type"] = "AdaptiveCard", ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["version"] = "1.5", ["body"] = body, ["actions"] = actions }.ToJsonString();
    }
    private static JsonObject Text(string value, string size) => new() { ["type"] = "TextBlock", ["text"] = value, ["size"] = size, ["wrap"] = false, ["maxLines"] = 1, ["spacing"] = "small" };
    private static JsonObject Column(JsonArray items, string width) => new() { ["type"] = "Column", ["width"] = width, ["items"] = items };
    private static JsonObject Action(string verb, string id) => new() { ["type"] = "Action.Execute", ["title"] = "Switch", ["verb"] = verb, ["data"] = new JsonObject { ["sourceId"] = id } };
    private static double? Value(WidgetAccount account, bool used) => account.Remaining is { } remaining ? Math.Clamp(used ? 100 - remaining : remaining, 0, 100) : null;
    private static JsonObject Bar(double? value)
    {
        var fill = (int)Math.Clamp(value ?? 0, 0, 100);
        var columns = new JsonArray();
        if (fill > 0) columns.Add(new JsonObject { ["type"] = "Column", ["width"] = fill, ["items"] = new JsonArray(), ["style"] = "accent", ["minHeight"] = "4px" });
        if (fill < 100) columns.Add(new JsonObject { ["type"] = "Column", ["width"] = 100 - fill, ["items"] = new JsonArray(), ["style"] = "emphasis", ["minHeight"] = "4px" });
        return new JsonObject { ["type"] = "ColumnSet", ["columns"] = columns, ["spacing"] = "small" };
    }
    private static JsonObject Ring(WidgetAccount account, bool used, int size, double threshold)
    {
        using var bitmap = new Bitmap(256, 256);
        using var drawing = Graphics.FromImage(bitmap);
        drawing.SmoothingMode = SmoothingMode.AntiAlias;
        drawing.Clear(Color.Transparent);
        using var track = new Pen(Color.FromArgb(80, 130, 150, 145), 14);
        var color = account.Remaining <= threshold ? Color.FromArgb(219, 98, 104) : Color.FromArgb(64, 175, 136);
        using var accent = new Pen(color, 14) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        drawing.DrawEllipse(track, 14, 14, 228, 228);
        var value = Value(account, used);
        if (value > 0) drawing.DrawArc(accent, 14, 14, 228, 228, -90, (float)(value.Value * 3.6));
        using var font = new Font("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(color);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        drawing.DrawString(value is null ? "—" : $"{value:0}%", font, text, new RectangleF(0, 0, 256, 256), centered);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return new() { ["type"] = "Image", ["url"] = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()), ["width"] = size + "px", ["height"] = size + "px", ["altText"] = account.Name + " " + (value is null ? "暂无数据" : $"{value:0}%") };
    }
}
