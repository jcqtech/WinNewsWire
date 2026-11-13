using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinNewsWire.Helpers;

public sealed partial class ThumbnailView : UserControl
{
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(ThumbnailView),
            new PropertyMetadata(null, ImageSourceChanged));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public ThumbnailView() => InitializeComponent();

    private static void ImageSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ThumbnailView)sender).Placeholder.Visibility = args.NewValue is null ? Visibility.Visible : Visibility.Collapsed;
}
