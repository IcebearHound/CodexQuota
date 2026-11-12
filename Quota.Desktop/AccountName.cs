using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Quota.Desktop;

public sealed class AccountName : Grid
{
    private readonly TextBlock label = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox editor = new() { FontSize = 11, Padding = new Thickness(3, 1, 3, 1), MaxLength = 40, Visibility = Visibility.Collapsed };
    private readonly Button edit;
    private AccountGroupViewModel? editingGroup;
    public AccountName()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = new GridLength(22) });
        edit = new Button { Content = new AppIcon { Kind = "pencil", Width = 13, Height = 13 }, Padding = new Thickness(0), Width = 21, Height = 22, Opacity = 0, Style = (Style)FindResource("IconButton") };
        edit.SetResourceReference(ToolTipProperty, "Loc.编辑名称");
        editor.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Loc.账号名称");
        SetColumn(edit, 1); Children.Add(label); Children.Add(editor); Children.Add(edit);
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        DataContextChanged += (_, _) => EndEdit();
        Unloaded += (_, _) => { ((ToolTip)label.ToolTip).IsOpen = false; EndEdit(); };
        MouseEnter += (_, _) => UpdateEditVisibility();
        MouseLeave += (_, _) => UpdateEditVisibility();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateEditVisibility();
        edit.Click += (_, args) => { BeginEdit(); args.Handled = true; };
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) { Commit(); args.Handled = true; }
            if (args.Key == Key.Escape) { EndEdit(); args.Handled = true; }
        };
        editor.LostKeyboardFocus += (_, _) => { if (editingGroup is not null) Commit(); };
        label.ToolTip = new ToolTip { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxWidth = 340 };
        ToolTipService.SetInitialShowDelay(label, 350);
        label.ToolTipOpening += (_, args) =>
        {
            if (DataContext is not AccountGroupViewModel group || editingGroup is not null) { args.Handled = true; return; }
            var body = new StackPanel { Width = 280 };
            body.Children.Add(DialogLayout.Text(group.Name, 14));
            body.Children.Add(DialogLayout.Text(group.Plan + " · " + group.Status, 11, true));
            body.Children.Add(DialogLayout.Text(group.Identity, 11, true));
            body.Children.Add(DialogLayout.Text(L.T("认证来源"), 10, true));
            body.Children.Add(DialogLayout.Text(group.SourceSummary, 11));
            body.Children.Add(DialogLayout.Text(group.ResetCardSummary, 11));
            foreach (var bucket in group.Buckets)
                foreach (var window in bucket.Windows)
                {
                    body.Children.Add(DialogLayout.Text(bucket.Name + " · " + window.Label + " · " + window.Caption + " " + window.Percent, 11));
                    body.Children.Add(DialogLayout.Text(window.ResetText, 10, true));
                }
            if (group.HasError) { body.Children.Add(DialogLayout.Text(group.Error ?? "", 11)); body.Children.Add(DialogLayout.Text(group.ActionHint, 11, true)); }
            body.Children.Add(DialogLayout.Text(group.Updated, 10, true));
            ((ToolTip)label.ToolTip).Content = new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        };
    }
    private void BeginEdit()
    {
        if (DataContext is not AccountGroupViewModel group) return;
        ((ToolTip)label.ToolTip).IsOpen = false;
        editingGroup = group; editor.Text = group.EditableName; label.Visibility = Visibility.Collapsed; editor.Visibility = Visibility.Visible;
        editor.Focus(); editor.SelectAll();
    }
    private void Commit()
    {
        if (editingGroup is not { } group) return;
        var name = editor.Text.Trim();
        var current = ReferenceEquals(DataContext, group) && IsLoaded && IsVisible;
        EndEdit();
        if (current && name.Length > 0 && Window.GetWindow(this) is MainWindow owner) owner.RenameAccountGroup(group, name);
    }
    private void EndEdit() { editingGroup = null; editor.Visibility = Visibility.Collapsed; label.Visibility = Visibility.Visible; UpdateEditVisibility(); }
    private void UpdateEditVisibility() => edit.Opacity = IsMouseOver || IsKeyboardFocusWithin ? 1 : 0;
}
