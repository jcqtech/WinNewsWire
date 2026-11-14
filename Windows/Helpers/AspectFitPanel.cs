using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinNewsWire.Helpers;

public sealed class AspectFitPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(Math.Min(availableSize.Width, width), Math.Min(availableSize.Height, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var container = new Rect(0, 0, finalSize.Width, finalSize.Height);
        foreach (var child in Children) child.Arrange(ThumbnailView.AspectFitRect(container, child.DesiredSize));
        return finalSize;
    }
}
