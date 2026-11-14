using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinNewsWire.Helpers;

public sealed partial class ThumbnailView : UserControl
{
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(ThumbnailView),
            new PropertyMetadata(null));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    internal static Rect AspectFitRect(Rect container, Size imageSize)
    {
        if (!(imageSize.Width > 0 && imageSize.Height > 0)) return container;
        double scale = Math.Min(container.Width / imageSize.Width, container.Height / imageSize.Height);
        double width = Math.Round(imageSize.Width * scale, MidpointRounding.AwayFromZero);
        double height = Math.Round(imageSize.Height * scale, MidpointRounding.AwayFromZero);
        return new Rect(container.X + Math.Floor((container.Width - width) / 2),
            container.Y + Math.Floor((container.Height - height) / 2), width, height);
    }

    public ThumbnailView()
    {
        InitializeComponent();
    }
}
