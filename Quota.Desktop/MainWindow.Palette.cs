using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Quota.Desktop;

public partial class MainWindow
{
    private void BuildPaletteSettings(Panel parent)
    {
        var section = SettingsControls.Section(parent, "调色板", "palette");
        var choices = new UniformGrid { Columns = narrowSettings ? 2 : 4, Margin = new Thickness(0, 0, -6, 0) };
        foreach (var preset in ThemeService.Palettes)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeService.IsLight ? preset.LightAccent : preset.DarkAccent));
            var background = (Color)ColorConverter.ConvertFromString(ThemeService.IsLight ? preset.LightBackground : preset.DarkBackground);
            var preview = new Grid { Width = 58, Height = 35, Margin = new Thickness(0, 0, 0, 8) };
            preview.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(background, accent.Color, 35), Opacity = 0.85
            });
            var card = new Grid { Margin = new Thickness(5) };
            card.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = ThemeService.IsLight ? Brushes.White : new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.DarkSurface))
            });
            card.Children.Add(new QuotaRing
            {
                Value = 72, Width = 20, Height = 20, StrokeThickness = 2.2, Foreground = accent,
                Track = new SolidColorBrush(Color.FromArgb(30, accent.Color.R, accent.Color.G, accent.Color.B)),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0)
            });
            card.Children.Add(new Border
            {
                Background = accent, Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 5, 0)
            });
            preview.Children.Add(card); content.Children.Add(preview);
            var caption = new TextBlock { Text = L.T(preset.Name), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            caption.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            content.Children.Add(caption);
            var choice = new RadioButton { Content = content, Tag = preset.Key, IsChecked = settings.Palette == preset.Key, GroupName = "palette-presets", Style = (Style)FindResource("IntervalChoice") };
            AutomationProperties.SetName(choice, L.T(preset.Name));
            choice.Checked += (_, _) => { settings.Palette = preset.Key; CommitSettings(); };
            choices.Children.Add(choice);
        }
        section.Children.Add(choices);
        SettingChoices(parent, "面板材质", "layers", ["纯色面板", "液态玻璃"], ["solid", "glass"], ["layout-grid", "layers"], settings.Material,
            value => settings.Material = value,
            L.F("液态玻璃透出背景色彩，叠加原生模糊、半透明卡片与双层高光边缘。", "Liquid glass reveals background colors through native blur, translucent cards and double-highlighted edges."));
    }
}
