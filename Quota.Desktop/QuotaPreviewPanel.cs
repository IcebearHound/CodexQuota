using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Quota.Core;

namespace Quota.Desktop;

public sealed class QuotaPreviewPanel : StackPanel
{
    private StackPanel body => this;
    private bool showUsed;
    private bool Narrow => ActualWidth < 600;
    private bool wasNarrow = true;

    public QuotaPreviewPanel(bool used = false)
    {
        showUsed = used;
        Loaded += (_, _) => Build();
        SizeChanged += (_, _) => { if (Narrow != wasNarrow) { wasNarrow = Narrow; Build(); } };
        Build();
    }
    internal void RefreshContent() => Build();

    private void Build()
    {
        body.Children.Clear();
        var sample = new Grid();
        if (Narrow) { sample.RowDefinitions.Add(new() { Height = GridLength.Auto }); sample.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        else { sample.ColumnDefinitions.Add(new() { Width = new GridLength(200) }); sample.ColumnDefinitions.Add(new()); }
        var ringPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var ringGrid = new Grid { Width = 138, Height = 138, Margin = new Thickness(0, 4, 0, 8) };
        var ring = new QuotaRing { Value = showUsed ? 28 : 72, StrokeThickness = 8 };
        ring.SetResourceReference(QuotaRing.ForegroundProperty, "Accent"); ring.SetResourceReference(QuotaRing.TrackProperty, "Track");
        ringGrid.Children.Add(ring);
        if (SystemParameters.ClientAreaAnimation) ring.BeginAnimation(QuotaRing.ValueProperty, new DoubleAnimation(0, ring.Value, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var value = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var number = Text(showUsed ? "28%" : "72%", 30, false, 2); number.FontWeight = FontWeights.SemiBold; number.HorizontalAlignment = HorizontalAlignment.Center; value.Children.Add(number);
        var caption = Text(showUsed ? L.T("已用") : L.T("剩余"), 11, true, 0); caption.HorizontalAlignment = HorizontalAlignment.Center; value.Children.Add(caption); ringGrid.Children.Add(value);
        ringPanel.Children.Add(ringGrid); ringPanel.Children.Add(Text(L.F("下次重置  2h 18m", "Next reset  2h 18m"), 11, true, 0)); sample.Children.Add(ringPanel);
        var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = Narrow ? new Thickness(0, 24, 0, 0) : new Thickness(12, 0, 0, 0) };
        bars.Children.Add(Text(L.F("示例账号", "Example accounts"), 11, true));
        for (var i = 0; i < 3; i++)
        {
            var remaining = new[] { 88, 46, 5 }[i];
            bars.Children.Add(Text(new[] { "Personal", "Workspace", "Research" }[i] + "   " + (showUsed ? 100 - remaining : remaining) + "%", 12, false, 5));
            var bar = new ProgressBar { Value = showUsed ? 100 - remaining : remaining, Maximum = 100, Margin = new Thickness(0, 0, 0, 5) };
            if (i == 2) bar.Foreground = new SolidColorBrush(Color.FromRgb(213, 161, 62)); bars.Children.Add(bar);
            bars.Children.Add(Text(new[] { "4h 02m", "1d 06h", "38m" }[i], 10, true, i == 2 ? 0 : 14));
        }
        if (Narrow) Grid.SetRow(bars, 1); else Grid.SetColumn(bars, 1);
        sample.Children.Add(bars); body.Children.Add(Card(sample, 20));
        var toggle = new CheckBox { Content = L.F("试试切换为已用额度", "Preview used quota"), IsChecked = showUsed, Margin = new Thickness(0, 14, 0, 16) };
        toggle.Checked += (_, _) => { showUsed = true; Build(); }; toggle.Unchecked += (_, _) => { showUsed = false; Build(); }; body.Children.Add(toggle);
        Fact("refresh-cw", L.F("每个窗口独立重置", "Independent reset windows"), L.F("短期与每周额度分别计时。主圆环突出当前受限窗口，详情可查看各窗口。", "Short-term and weekly limits reset independently. The main ring highlights the limiting window. Open account details for every window."));
        Fact("link-2", L.F("同邮箱自动合并", "Accounts merge by email"), L.F("本机、CC Switch 和原生登录的同邮箱账号合并展示，并保留各工作区的额度。", "Accounts from local Codex, CC Switch and browser sign-in merge by email, retaining workspace limits."));
        body.Children.Add(Text(L.F("在设置中切换剩余 / 已用显示，并配置低额度、额度重置和重置卡到期通知。上方数值为演示。", "Settings control remaining / used display and alerts for low quota, resets and reset-credit expiry. Values above are examples."), 11, true, 0));
    }

    private void Fact(string icon, string title, string detail)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new());
        var glyph = Glyph(icon); glyph.VerticalAlignment = VerticalAlignment.Top; glyph.Margin = new Thickness(0, 2, 0, 0); row.Children.Add(glyph);
        var content = new StackPanel(); content.Children.Add(Text(title, 13, false, 5)); content.Children.Add(Text(detail, 12, true, 0)); Grid.SetColumn(content, 1); row.Children.Add(content); body.Children.Add(row);
    }
    private static AppIcon Glyph(string kind) => new() { Kind = kind, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Text(string text, int size = 13, bool muted = false, double bottom = 10)
    {
        var block = DialogLayout.Text(text, size, muted); block.Margin = new Thickness(0, 0, 0, bottom); return block;
    }
    private Border Card(UIElement content, double padding)
    {
        var card = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(WindowSurface.CornerRadius), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return card;
    }
}
