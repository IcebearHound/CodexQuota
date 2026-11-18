using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Quota.Core;

namespace Quota.Desktop;

internal sealed class UpdatePromptWindow : Window
{
    internal UpdatePromptWindow(Window owner, Version current, AppUpdate update, AppUpdateState state,
        Func<Task> install, Action skip, Action cancel)
    {
        DialogLayout.Configure(this, owner, L.F("发现新版本", "Update available"), 476);
        ShowActivated = false;
        Topmost = Owner?.Topmost == true;
        var content = new StackPanel { Margin = new Thickness(22, 17, 22, 2) };

        var version = DialogLayout.Text($"Codex Quota {update.Version}", 24);
        version.FontWeight = FontWeights.SemiBold;
        version.Margin = new Thickness(0, 0, 0, 5);
        content.Children.Add(version);
        var metadata = L.F("当前版本 {0}  ·  下载 {1:N1} MB", "Installed: {0}  ·  Download: {1:N1} MB",
            current, update.Size / (1024d * 1024));
        if (update.PublishedAt is { } published)
        {
            var culture = CultureInfo.GetCultureInfo(L.Language);
            metadata += "  ·  " + published.ToLocalTime().ToString("d", culture);
        }
        content.Children.Add(DialogLayout.Text(metadata, 12, true));

        var notes = new StackPanel();
        var label = DialogLayout.Text(L.F("更新摘要", "What’s new"), 13);
        label.FontWeight = FontWeights.SemiBold;
        notes.Children.Add(label);
        var summary = Summarize(update.ReleaseNotes);
        var description = DialogLayout.Text("", 13);
        description.Text = summary.Length > 0 ? summary : L.F("此版本未提供更新摘要。", "This release does not include release notes.");
        description.LineHeight = 20;
        description.Margin = new Thickness(0);
        notes.Children.Add(description);
        var noteCard = new Border { Child = notes, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 11, 13, 12), Margin = new Thickness(0, 5, 0, 6) };
        noteCard.SetResourceReference(Border.BackgroundProperty, "Surface");
        noteCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        content.Children.Add(noteCard);

        var details = new Button { Content = L.F("在 GitHub 查看完整发布说明 ↗", "Full release notes on GitHub ↗"),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, 0, 10) };
        details.SetResourceReference(StyleProperty, "QuietButton");
        details.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(update.ReleaseUri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception)
            {
                DialogLayout.Alert(this, L.F("无法打开浏览器", "Couldn’t open the browser"), update.ReleaseUri.AbsoluteUri);
            }
        };
        content.Children.Add(details);

        Binding Bind(string property) => new(property) { Source = state };
        void VisibleWhen(FrameworkElement element, string property)
            => element.SetBinding(VisibilityProperty, new Binding(property) { Source = state, Converter = new BoolVisibility() });

        var footer = new StackPanel { Margin = new Thickness(22, 8, 22, 17) };
        var status = DialogLayout.Text("", 12, true);
        status.SetBinding(TextBlock.TextProperty, Bind(nameof(AppUpdateState.PromptMessage)));
        footer.Children.Add(status);
        var progress = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 0, 0, 9) };
        System.Windows.Automation.AutomationProperties.SetName(progress, L.F("下载进度", "Download progress"));
        progress.SetBinding(ProgressBar.ValueProperty, Bind(nameof(AppUpdateState.Percent)));
        VisibleWhen(progress, nameof(AppUpdateState.CanCancel));
        footer.Children.Add(progress);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var skipButton = new Button { Content = L.F("跳过此版本", "Skip this version"), Margin = new Thickness(0, 0, 8, 0) };
        skipButton.Click += (_, _) => { skip(); Close(); };
        VisibleWhen(skipButton, nameof(AppUpdateState.CanCheck));
        actions.Children.Add(skipButton);
        var updateButton = new Button { Content = L.F("更新并重启", "Update and restart"), FontWeight = FontWeights.SemiBold };
        updateButton.SetResourceReference(Control.ForegroundProperty, "Accent");
        updateButton.SetResourceReference(Control.BorderBrushProperty, "Accent");
        updateButton.Click += async (_, _) => await install();
        VisibleWhen(updateButton, nameof(AppUpdateState.CanInstall));
        actions.Children.Add(updateButton);
        var cancelButton = new Button { Content = L.F("取消下载", "Cancel download") };
        cancelButton.Click += (_, _) => cancel();
        VisibleWhen(cancelButton, nameof(AppUpdateState.CanCancel));
        actions.Children.Add(cancelButton);
        footer.Children.Add(actions);
        DialogLayout.Frame(this, content, null, footer);
    }

    private static string Summarize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        const int limit = 460;
        // Only a bounded plain-text excerpt is rendered; release markup and embedded URLs are never loaded.
        using var reader = new StringReader(markdown[..Math.Min(markdown.Length, 8192)]);
        var lines = new List<string>();
        var remaining = limit;
        var fenced = false;
        while (reader.ReadLine() is { } value)
        {
            var line = value.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            { fenced = !fenced; continue; }
            if (fenced || line.Length == 0 || line[0] is '#' or '<' || line is "---" or "***") continue;
            line = Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                line = "• " + line[2..];
            line = line.Replace("`", "").Replace("**", "").Replace("__", "").Trim();
            if (line.Length == 0) continue;
            if (lines.Count == 5) { lines.Add("…"); break; }
            if (line.Length > remaining)
            {
                var end = remaining > 0 && char.IsHighSurrogate(line[remaining - 1]) ? remaining - 1 : remaining;
                lines.Add(line[..end].TrimEnd() + "…");
                break;
            }
            lines.Add(line);
            remaining -= line.Length;
        }
        return string.Join(Environment.NewLine, lines);
    }
}
