using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Quota.Desktop;

public sealed class BrandIcon : Image
{
    private static readonly BitmapSource Mark = LoadMark();

    public BrandIcon()
    {
        Width = Height = 24;
        UseLayoutRounding = true;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Stretch = Stretch.Uniform;
        Source = Mark;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Fant);
    }

    private static BitmapSource LoadMark()
    {
        var mark = new BitmapImage(new Uri("pack://application:,,,/CodexQuota;component/Assets/brand.png"));
        mark.Freeze();
        return mark;
    }
}
