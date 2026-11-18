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
        var choices = new UniformGrid { Columns = narrowSettings ? 3 : ThemeService.Palettes.Length, Margin = new Thickness(0, 0, -6, 0) };
        foreach (var preset in ThemeService.Palettes)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var preview = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 6) };
            foreach (var color in new[] { preset.LightBackground, preset.LightAccent, preset.DarkSurface })
            {
                var chip = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    Margin = new Thickness(1, 0, 1, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
                };
                chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                preview.Children.Add(chip);
            }
            content.Children.Add(preview);
            var caption = new TextBlock { Text = L.T(preset.Name), FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            caption.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            content.Children.Add(caption);
            var choice = new RadioButton { Content = content, Tag = preset.Key, IsChecked = settings.Palette == preset.Key, GroupName = "palette-presets", Style = (Style)FindResource("IntervalChoice"), Margin = new Thickness(0, 0, 6, 4) };
            AutomationProperties.SetName(choice, L.T(preset.Name));
            choice.Checked += (_, _) =>
            {
                settings.Palette = preset.Key;
                if (preset.Key == "monochrome") settings.Theme = "system";
                CommitSettings();
                if (preset.Key == "monochrome") QueueSettingsRender();
            };
            choices.Children.Add(choice);
        }
        section.Children.Add(choices);
        SettingChoices(parent, "面板材质", "layers", ["纯色面板", "液态玻璃"], ["solid", "glass"], ["layout-grid", "layers"], settings.Material,
            value => settings.Material = value,
            L.F("液态玻璃以原生模糊和中性雾面柔化背景。", "Liquid glass softens the background with native blur and a neutral frost."));

    }
}
