using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Quota.Core;

namespace Quota.Desktop;

public sealed class SessionIssuesWindow : Window
{
    private readonly StackPanel rows = new() { Margin = new Thickness(10, 0, 10, 10) };
    private readonly StackPanel toolbar = new() { Margin = new Thickness(14, 4, 14, 10) };
    private readonly Action<CodexSessionIssue> open;
    private readonly Action<CodexSessionIssue, CodexIssueDisposition> acknowledge;
    private readonly Action ignoreAll;
    private readonly Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CodexSessionIssue> issues = Array.Empty<CodexSessionIssue>();
    private string language = "";
    private DateTime titleIndexWrite;

    public SessionIssuesWindow(Window owner, Action<CodexSessionIssue> open, Action<CodexSessionIssue, CodexIssueDisposition> acknowledge, Action ignoreAll)
    {
        this.open = open; this.acknowledge = acknowledge; this.ignoreAll = ignoreAll;
        DialogLayout.Configure(this, owner, L.F("异常会话", "Session issues"), 420);
        ShowInTaskbar = true;
        DialogLayout.Frame(this, rows, toolbar);
        MaxHeight = Math.Min(460, Math.Max(1, SystemParameters.WorkArea.Height - 32));
        L.LanguageChanged += LanguageChanged;
        Closed += (_, _) => L.LanguageChanged -= LanguageChanged;
        Render();
    }

    public void SetIssues(IReadOnlyList<CodexSessionIssue> value)
    {
        var titlesChanged = RefreshTitles();
        if (issues.SequenceEqual(value) && language == L.Language && !titlesChanged) return;
        issues = value.ToArray(); Render();
    }
    private void LanguageChanged(object? sender, EventArgs args) => Render();

    private void Render()
    {
        language = L.Language; Title = L.F("异常会话", "Session issues");
        toolbar.Children.Clear(); rows.Children.Clear();
        var tools = new Grid(); tools.ColumnDefinitions.Add(new()); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var count = DialogLayout.Text(L.F("{0} 个异常会话", "{0} session issues", issues.Count), 12, true);
        count.Margin = new Thickness(0); count.VerticalAlignment = VerticalAlignment.Center; tools.Children.Add(count);
        var clear = SettingsControls.Action(L.F("全部忽略", "Ignore all"), "eye-off", ignoreAll);
        clear.IsEnabled = issues.Count > 0; clear.Margin = new Thickness(8, 0, 0, 0); clear.Padding = new Thickness(9, 6, 9, 6);
        Grid.SetColumn(clear, 1); tools.Children.Add(clear); toolbar.Children.Add(tools);
        if (issues.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(12, 16, 12, 12) };
            var icon = new AppIcon { Kind = "check", Width = 28, Height = 28, Margin = new Thickness(0, 0, 0, 14) };
            icon.SetResourceReference(AppIcon.ForegroundProperty, "Accent"); empty.Children.Add(icon);
            var text = DialogLayout.Text(L.F("暂无异常会话", "No session issues"), 16); text.TextAlignment = TextAlignment.Center; empty.Children.Add(text);
            var hint = DialogLayout.Text(L.F("新发生的错误或中断会出现在这里。", "New errors and interruptions will appear here."), 12, true); hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            rows.Children.Add(empty); return;
        }
        foreach (var issue in issues)
        {
            var row = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var glyph = new AppIcon { Kind = issue.Kind == CodexActivityKind.Error ? "x" : "info", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
            glyph.SetResourceReference(AppIcon.ForegroundProperty, "Error"); row.Children.Add(glyph);
            var title = issue.Title is { Length: > 0 } catalogTitle ? catalogTitle : issue.ThreadId is { } id && titles.TryGetValue(id, out var name) ? name
                : issue.ThreadId is { } thread ? L.F("会话 {0}", "Chat {0}", thread[..Math.Min(8, thread.Length)]) : L.F("无法定位的会话", "Unidentified chat");
            var text = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var heading = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            heading.ToolTip = issue.ThreadId is { } identifier ? title + Environment.NewLine + identifier : title; text.Children.Add(heading);
            var kind = issue.Kind == CodexActivityKind.Error ? L.F("错误", "Error") : L.F("中断", "Interrupted");
            var time = issue.OccurredAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? L.F("时间未知", "Time unknown");
            var detail = DialogLayout.Text(kind + " · " + time, 10, true); detail.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(detail);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var navigate = IconAction(L.F("打开会话", "Open chat"), "chevron-right", () => open(issue));
            navigate.IsEnabled = CodexThreadLink.Create(issue.ThreadId) is not null; actions.Children.Add(navigate);
            var ignore = IconAction(L.F("忽略", "Ignore"), "eye-off", () => acknowledge(issue, CodexIssueDisposition.Ignored));
            ignore.ToolTip = L.F("忽略本次异常，再次发生时重新提醒。", "Ignore this occurrence and alert again if it happens again."); actions.Children.Add(ignore);
            Grid.SetColumn(actions, 2); row.Children.Add(actions);
            var item = new Border { Child = row, Padding = new Thickness(8, 10, 8, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
            item.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); rows.Children.Add(item);
        }
    }

    private Button IconAction(string label, string icon, Action action)
    {
        var button = new Button { Content = new AppIcon { Kind = icon, Width = 15, Height = 15 }, Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, Margin = new Thickness(2, 0, 0, 0), ToolTip = label };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }

    private bool RefreshTitles()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var path = Path.Combine(home, "session_index.jsonl");
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (!File.Exists(path))
            {
                var changed = titles.Count > 0; titles.Clear(); titleIndexWrite = default; return changed;
            }
            if (modified == titleIndexWrite) return false;
            var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Position = Math.Max(0, file.Length - 2 * 1024 * 1024);
            using var reader = new StreamReader(file);
            if (file.Position > 0) reader.ReadLine();
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var record = JsonDocument.Parse(line);
                    var value = record.RootElement;
                    if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        && CodexThreadLink.NormalizeId(id.GetString()) is { } key
                        && value.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } title)
                        updated[key] = title.Replace('\r', ' ').Replace('\n', ' ');
                }
                catch (JsonException) { }
            }
            titles.Clear(); foreach (var entry in updated) titles.Add(entry.Key, entry.Value);
            titleIndexWrite = modified; return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
