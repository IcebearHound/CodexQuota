using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Quota.Core;

namespace Quota.Desktop;

public sealed class StatusPreviewPanel : StackPanel
{
    internal readonly DispatcherTimer Playback = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private DispatcherTimer playback => Playback;
    private StackPanel body => this;
    private readonly CodexStatusViewModel previewStatus = new();
    private int statusIndex;
    private bool automatic = SystemParameters.ClientAreaAnimation, selectingStatus;
    private bool wasNarrow = true;
    private bool Narrow => ActualWidth < 600;
    private StackPanel? statusDetail;
    private Border? statusCard;
    private RadioButton[] statusChoices = [];
    private CheckBox? autoplay;
    private Window? owner;

    public StatusPreviewPanel()
    {
        playback.Tick += (_, _) => SelectStatus((statusIndex + 1) % 8);
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this);
            if (owner is not null) owner.StateChanged += OwnerStateChanged;
            Build(); UpdatePlayback();
        };
        Unloaded += (_, _) =>
        {
            playback.Stop();
            if (owner is not null) owner.StateChanged -= OwnerStateChanged;
            owner = null;
            previewStatus.Update(new(CodexActivityKind.Offline));
        };
        IsVisibleChanged += (_, _) => UpdatePlayback();
        SizeChanged += (_, _) => { if (Narrow != wasNarrow) { wasNarrow = Narrow; Build(); } };
        Build();
    }
    private void OwnerStateChanged(object? sender, EventArgs args) => UpdatePlayback();
    private static (string Name, string Pattern, string Detail, CodexActivityKind Kind, bool Exhausted, bool Error, bool Low) Scenario(int index) => CodexStatusExamples.Get(index);

    internal void RefreshContent() => Build();

    private void Build()
    {
        body.Children.Clear();
        var grid = new Grid();
        if (Narrow) { grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); }
        else { grid.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); grid.ColumnDefinitions.Add(new()); }
        var list = new UniformGrid { Columns = Narrow ? 2 : 1, Margin = Narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0, 0, 18, 0) };
        statusChoices = new RadioButton[8];
        for (var i = 0; i < 8; i++)
        {
            var selected = i; var scenario = Scenario(i);
            var label = SettingsControls.Label(scenario.Name, new[] { "activity", "check", "ellipsis", "bell", "minus", "x", "info", "monitor" }[i]);
            label.MaxWidth = Narrow ? Math.Max(80, ((ActualWidth > 0 ? ActualWidth : 344) - 54) / 2) : 170;
            var choice = new RadioButton
            {
                Content = label,
                IsChecked = i == statusIndex, GroupName = "guide-status", Style = (Style)FindResource("IntervalChoice"),
                Margin = new Thickness(0, 0, Narrow ? 6 : 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(choice, scenario.Name);
            choice.PreviewMouseLeftButtonDown += (_, _) => PausePlayback();
            choice.PreviewKeyDown += (_, args) => { if (args.Key is Key.Space or Key.Enter) PausePlayback(); };
            choice.Checked += (_, _) =>
            {
                if (selectingStatus) return;
                PausePlayback(); SelectStatus(selected);
                if (Narrow) statusCard?.BringIntoView();
            };
            statusChoices[i] = choice; list.Children.Add(choice);
        }
        if (Narrow) Grid.SetRow(list, 1);
        grid.Children.Add(list);
        statusDetail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        statusCard = Card(statusDetail, Narrow ? 18 : 24);
        if (!Narrow) Grid.SetColumn(statusCard, 1);
        grid.Children.Add(statusCard); body.Children.Add(grid);
        autoplay = new CheckBox { Content = L.F("自动轮播演示", "Play states automatically"), IsChecked = automatic, Margin = new Thickness(0, 15, 0, 12), IsEnabled = SystemParameters.ClientAreaAnimation };
        autoplay.Checked += (_, _) => { automatic = true; UpdatePlayback(); }; autoplay.Unchecked += (_, _) => { automatic = false; UpdatePlayback(); }; body.Children.Add(autoplay);
        body.Children.Add(Text(L.F("状态根据本机 Codex 进程与会话事件判断。多个会话并行时会优先提示错误、中断及待输入。未写入事件的等待可能暂时无法识别。", "Detection uses local Codex processes and chat events. Errors, interruptions and input requests take priority across concurrent chats. Waits without recorded events may be unavailable."), 11, true, 0));
        SelectStatus(statusIndex);
    }

    private void SelectStatus(int index)
    {
        statusIndex = index;
        if (statusDetail is null) return;
        var scenario = Scenario(index);
        previewStatus.Update(new(scenario.Kind, Running: scenario.Kind == CodexActivityKind.Working ? 1 : 0, Waiting: scenario.Kind == CodexActivityKind.NeedsInput ? 1 : 0), scenario.Exhausted, scenario.Error, scenario.Low);
        statusDetail.Children.Clear();
        var halo = new Border { Width = Narrow ? 52 : 90, Height = Narrow ? 52 : 90, CornerRadius = new CornerRadius(45), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, Narrow ? 0 : 22) };
        halo.SetResourceReference(Border.BackgroundProperty, "ButtonFill"); halo.Child = new StatusLamp { Status = previewStatus, Width = Narrow ? 18 : 25, Height = Narrow ? 18 : 25 };
        var statusName = Text(scenario.Name, Narrow ? 20 : 24);
        var pattern = Text(scenario.Pattern, 12, false, Narrow ? 0 : 16);
        var color = (Color)ColorConverter.ConvertFromString(previewStatus.Color);
        var surface = ((SolidColorBrush)FindResource("Surface")).Color;
        var readable = ContrastColors.AccentOn(new(color.R, color.G, color.B), new(surface.R, surface.G, surface.B), 4.5);
        pattern.Foreground = new SolidColorBrush(Color.FromRgb(readable.Red, readable.Green, readable.Blue));
        if (Narrow)
        {
            var hero = new Grid(); hero.ColumnDefinitions.Add(new() { Width = new GridLength(70) }); hero.ColumnDefinitions.Add(new()); hero.Children.Add(halo);
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; label.Children.Add(statusName); label.Children.Add(pattern);
            Grid.SetColumn(label, 1); hero.Children.Add(label); statusDetail.Children.Add(hero);
        }
        else { statusDetail.Children.Add(halo); statusDetail.Children.Add(statusName); statusDetail.Children.Add(pattern); }
        var explanation = Text(scenario.Detail, Narrow ? 12 : 13, true, 0); explanation.Margin = new Thickness(0, Narrow ? 16 : 0, 0, 0); statusDetail.Children.Add(explanation);
        if (index == 2)
        {
            var prompt = Text(L.F("演示提示  ·  请返回 Codex 完成确认", "Sample prompt  ·  Return to Codex to confirm"), 11, false, 0);
            var bubble = Card(prompt, 12); bubble.Margin = new Thickness(0, 20, 0, 0); statusDetail.Children.Add(bubble);
            if (SystemParameters.ClientAreaAnimation) bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
        }
        // Update selection without firing the user-selection handler.
        if (statusChoices.Length == 8 && statusChoices[index].IsChecked != true)
        {
            selectingStatus = true;
            statusChoices[index].IsChecked = true;
            selectingStatus = false;
        }
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        if (IsLoaded && IsVisible && Window.GetWindow(this)?.WindowState != WindowState.Minimized && automatic && SystemParameters.ClientAreaAnimation) playback.Start();
        else playback.Stop();
    }

    private void PausePlayback()
    {
        automatic = false; playback.Stop();
        if (autoplay is not null) autoplay.IsChecked = false;
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
