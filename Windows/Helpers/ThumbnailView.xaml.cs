using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinNewsWire.Helpers;

public sealed partial class ThumbnailView : UserControl
{
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(ThumbnailView),
            new PropertyMetadata(null, PresentationChanged));

    public static readonly DependencyProperty FitsImageProperty =
        DependencyProperty.Register(nameof(FitsImage), typeof(bool), typeof(ThumbnailView),
            new PropertyMetadata(false, PresentationChanged));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(ThumbnailView),
            new PropertyMetadata(null));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public bool FitsImage
    {
        get => (bool)GetValue(FitsImageProperty);
        set => SetValue(FitsImageProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public ThumbnailView()
    {
        InitializeComponent();
        UpdatePresentation();
    }

    private static void PresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((ThumbnailView)sender).UpdatePresentation();

    private void UpdatePresentation()
    {
        ThumbnailBrush.Stretch = FitsImage ? Stretch.Uniform : Stretch.UniformToFill;
        ThumbnailBrush.AlignmentY = FitsImage ? AlignmentY.Center : AlignmentY.Top;
        Placeholder.Visibility = ImageSource is null || FitsImage ? Visibility.Visible : Visibility.Collapsed;
    }
}
