using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Automation.Provider;
using Quota.Core;

namespace Quota.Desktop;

public sealed class StatusLamp : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(nameof(Status), typeof(CodexStatusViewModel), typeof(StatusLamp), new PropertyMetadata(null, StatusChanged));
    public CodexStatusViewModel? Status { get => (CodexStatusViewModel?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public static readonly DependencyProperty ContrastBackgroundProperty = DependencyProperty.Register(nameof(ContrastBackground), typeof(Color?), typeof(StatusLamp), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Color? ContrastBackground { get => (Color?)GetValue(ContrastBackgroundProperty); set => SetValue(ContrastBackgroundProperty, value); }
    private CodexLampAnimation animationMode;
    private readonly TextBlock tooltipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
    public StatusLamp()
    {
        Width = 8; Height = 8;
        ToolTip = new ToolTip { Content = tooltipText };
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 15000);
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemSettingsChanged; Update(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemSettingsChanged; Update(); };
        IsVisibleChanged += (_, _) => Update();
        MouseLeftButtonDown += (_, args) => { if (Status?.CanOpenInterruptedTask == true) args.Handled = true; };
        MouseLeftButtonUp += (_, args) => { if (Status?.CanOpenInterruptedTask == true) { Activate(); args.Handled = true; } };
        KeyDown += (_, args) => { if (Status?.CanOpenInterruptedTask == true && args.Key is Key.Enter or Key.Space) { Activate(); args.Handled = true; } };
    }
    private static void StatusChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var lamp = (StatusLamp)target;
        if (args.OldValue is CodexStatusViewModel previous) PropertyChangedEventManager.RemoveHandler(previous, lamp.Changed, "");
        if (args.NewValue is CodexStatusViewModel current) PropertyChangedEventManager.AddHandler(current, lamp.Changed, "");
        lamp.Update();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName is nameof(CodexStatusViewModel.Signal) or nameof(CodexStatusViewModel.Message)) Update(); }
    private void SystemSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Update();
    }
    private void Update()
    {
        tooltipText.Text = Status?.Tooltip ?? "Codex";
        Focusable = Status?.CanOpenInterruptedTask == true;
        if (Focusable) Cursor = Cursors.Hand; else ClearValue(CursorProperty);
        if ((!IsLoaded || !IsVisible) && ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        AutomationProperties.SetName(this, Status?.Message ?? "Codex");
        AutomationProperties.SetHelpText(this, Status?.Tooltip ?? "Codex");
        InvalidateVisual();
        var mode = IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation
            ? Status?.Animation ?? CodexLampAnimation.Steady : CodexLampAnimation.Steady;
        if (mode == animationMode) return;
        animationMode = mode;
        BeginAnimation(OpacityProperty, CreateAnimation(mode));
    }
    internal static DoubleAnimationBase? CreateAnimation(CodexLampAnimation mode)
    {
        DoubleAnimationBase? animation = mode switch
        {
            CodexLampAnimation.Breathing => new DoubleAnimation(0.28, 1, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            },
            CodexLampAnimation.Flashing => new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(400), RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
                }
            },
            _ => null
        };
        if (animation is not null) Timeline.SetDesiredFrameRate(animation, 30);
        return animation;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var color = (Color)ColorConverter.ConvertFromString(Status?.Color ?? "#82938D");
        if (ContrastBackground is { } background)
        {
            var adjusted = ContrastColors.AccentOn(new(color.R, color.G, color.B), new(background.R, background.G, background.B), 4.5);
            color = Color.FromRgb(adjusted.Red, adjusted.Green, adjusted.Blue);
        }
        drawing.DrawEllipse(new SolidColorBrush(color), null, new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2, ActualHeight / 2);
    }
    private void Activate()
    {
        if (ToolTip is ToolTip tooltip) tooltip.IsOpen = false;
        Status?.OpenInterruptedTask();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new StatusLampPeer(this);
    private sealed class StatusLampPeer(StatusLamp owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => owner.Status?.CanOpenInterruptedTask == true ? AutomationControlType.Button : AutomationControlType.Custom;
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke && owner.Status?.CanOpenInterruptedTask == true ? this : base.GetPattern(patternInterface);
        public void Invoke()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(owner.Activate);
        }
    }
}
