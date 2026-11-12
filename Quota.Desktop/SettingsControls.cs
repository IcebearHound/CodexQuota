using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Quota.Desktop;

internal static class SettingsControls
{
    internal static Grid Label(string text, string icon, int size = 12)
    {
        var label = new Grid();
        label.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        label.ColumnDefinitions.Add(new());
        label.Children.Add(new AppIcon { Kind = icon, HorizontalAlignment = HorizontalAlignment.Left });
        var title = new TextBlock { Text = L.T(text), FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        Grid.SetColumn(title, 1); label.Children.Add(title);
        return label;
    }

    internal static Border Card(UIElement content, int padding = 18)
    {
        var card = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(WindowSurface.CornerRadius), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    internal static StackPanel Section(Panel parent, string title, string icon, string description = "")
    {
        var content = new StackPanel();
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(30) });
        heading.ColumnDefinitions.Add(new());
        var glyph = new AppIcon { Kind = icon, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        glyph.SetResourceReference(AppIcon.ForegroundProperty, "Accent"); heading.Children.Add(glyph);
        var details = new StackPanel();
        var name = DialogLayout.Text(title, 14); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0); details.Children.Add(name);
        if (description.Length > 0)
        {
            var text = DialogLayout.Text(description, 11, true); text.Margin = new Thickness(0, 5, 0, 0); details.Children.Add(text);
        }
        Grid.SetColumn(details, 1); heading.Children.Add(details); content.Children.Add(heading);
        parent.Children.Add(Card(content));
        return content;
    }

    internal static Button Action(string label, string icon, Action action)
    {
        var button = new Button { Content = Label(label, icon), Margin = new Thickness(0, 0, 8, 8), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, L.T(label));
        button.Click += (_, _) => action();
        return button;
    }

    internal static void Choices(Panel parent, string label, string[] labels, string[] values, string[] icons, string selected, Action<string> change)
    {
        var grid = new UniformGrid { Columns = labels.Length > 3 ? 2 : labels.Length, Margin = new Thickness(0, 0, -6, 0) };
        for (var index = 0; index < labels.Length; index++)
        {
            var value = values[index];
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new AppIcon { Kind = icons[index], Margin = new Thickness(0, 0, 0, 7) });
            var text = new TextBlock { Text = L.T(labels[index]), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            text.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RadioButton), 1) });
            content.Children.Add(text);
            var radio = new RadioButton { Content = content, IsChecked = selected == value, GroupName = label, Style = (Style)Application.Current.FindResource("IntervalChoice"), Margin = new Thickness(0, 0, 6, 6), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(radio, L.T(labels[index]));
            radio.Checked += (_, _) => change(value); grid.Children.Add(radio);
        }
        parent.Children.Add(grid);
    }

    internal static void Toggle(Panel parent, string label, string icon, bool value, Action<bool> change, string description = "")
    {
        var toggle = new CheckBox { Content = Label(label, icon), IsChecked = value, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetName(toggle, L.T(label));
        toggle.Checked += (_, _) => change(true);
        toggle.Unchecked += (_, _) => change(false);
        parent.Children.Add(toggle);
        if (description.Length > 0)
        {
            var hint = DialogLayout.Text(description, 11, true); hint.Margin = new Thickness(44, 0, 0, 10); parent.Children.Add(hint);
        }
    }

    internal static void Input(Panel parent, string label, string value, Func<string, bool> update, string hint, string invalidHint)
    {
        var title = DialogLayout.Text(label, 12); title.Margin = new Thickness(0, 10, 0, 7); parent.Children.Add(title);
        var input = new TextBox { Text = value, FontSize = 12, Padding = new Thickness(10, 8, 10, 8) };
        AutomationProperties.SetName(input, L.T(label));
        var feedback = DialogLayout.Text(hint, 11, true); feedback.Margin = new Thickness(0, 6, 0, 10);
        input.TextChanged += (_, _) =>
        {
            var valid = update(input.Text.Trim());
            feedback.Text = valid ? hint : invalidHint;
            input.SetResourceReference(Control.BorderBrushProperty, valid ? "BorderBrush" : "Error");
            feedback.SetResourceReference(TextBlock.ForegroundProperty, valid ? "Muted" : "Error");
            AutomationProperties.SetHelpText(input, feedback.Text);
        };
        AutomationProperties.SetHelpText(input, hint);
        parent.Children.Add(input); parent.Children.Add(feedback);
    }

    internal static Border ConnectionCard(string icon, string title, string description, Action connect, bool enabled = true)
    {
        var content = new StackPanel();
        var heading = DialogLayout.Text(title, 14); heading.FontWeight = FontWeights.SemiBold; content.Children.Add(heading);
        content.Children.Add(DialogLayout.Text(description, 12, true));
        var button = Action(title, icon, connect); button.IsEnabled = enabled; content.Children.Add(button);
        return Card(content);
    }
}
