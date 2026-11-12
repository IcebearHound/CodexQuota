using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Quota.Desktop;

public partial class MainWindow
{
    private void BuildPaletteSettings(Panel parent)
    {
        var section = SettingsControls.Section(parent, "调色板", "palette");
        var choices = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -6, 0) };
        foreach (var preset in ThemeService.Palettes)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.DarkAccent)), Margin = new Thickness(0, 0, 0, 7) });
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
            L.F("液态玻璃使用 Windows 11 系统模糊、半透明层次与柔光描边。系统背景不可用时使用纯色配色。", "Liquid glass uses Windows 11 backdrop blur, translucent layers and soft highlights. Solid colors are used when the system backdrop is unavailable."));
    }
}
