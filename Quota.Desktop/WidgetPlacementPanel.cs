using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Quota.Desktop;

public sealed class WidgetPlacementPanel : StackPanel
{
    private readonly MainWindow main;
    public WidgetPlacementPanel(MainWindow main) { this.main = main; Build(); }

    private void Build()
    {
        Children.Clear();
        var settings = main.GuideSettings;
        var taskbar = new CheckBox { Content = SettingsControls.Label(L.F("放置任务栏小组件", "Show taskbar widget"), "layout-grid"), IsChecked = settings.TaskbarWidgetEnabled, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(taskbar, L.F("放置任务栏小组件", "Show taskbar widget"));
        taskbar.Checked += (_, _) => Change(true, settings.TaskbarPlacement);
        taskbar.Unchecked += (_, _) => Change(false, settings.TaskbarPlacement);
        Children.Add(taskbar);
        var choices = new UniformGrid { Columns = 3, IsEnabled = settings.TaskbarWidgetEnabled };
        var names = new[] { L.F("自动", "Auto"), L.F("靠左", "Left"), L.F("靠右", "Right") };
        var values = new[] { "auto", "left", "right" };
        for (var index = 0; index < values.Length; index++)
        {
            var side = values[index];
            var radio = new RadioButton { Content = SettingsControls.Label(names[index], index == 0 ? "monitor" : "chevron-right", 11), GroupName = "widget-taskbar-side", IsChecked = settings.TaskbarPlacement == side, Style = (Style)FindResource("IntervalChoice") };
            AutomationProperties.SetName(radio, names[index]);
            radio.Checked += (_, _) => Change(settings.TaskbarWidgetEnabled, side);
            choices.Children.Add(radio);
        }
        Children.Add(choices);
        var layout = SettingsControls.Section(this, L.T("任务栏布局"), "layout-grid");
        SettingsControls.Choices(layout, "任务栏布局", ["圆环模式", "状态条模式"], ["ring", "bar"], ["rotate-cw", "activity"], settings.DetailedTaskbar ? "bar" : "ring", value => main.SetTaskbarLayout(value == "bar"));
    }
    private void Change(bool enabled, string position) { main.SetGuideTaskbar(enabled, position); Build(); }
}
