using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinNewsWire.Helpers;

public sealed class TimelineTextPanel : Panel
{
    public static readonly DependencyProperty NumberOfLinesProperty = DependencyProperty.Register(
        nameof(NumberOfLines), typeof(int), typeof(TimelineTextPanel), new PropertyMetadata(3, AppearanceChanged));
    public static readonly DependencyProperty ShowFullTitlesProperty = DependencyProperty.Register(
        nameof(ShowFullTitles), typeof(bool), typeof(TimelineTextPanel), new PropertyMetadata(false, AppearanceChanged));

    public bool ShowFullTitles
    {
        get => (bool)GetValue(ShowFullTitlesProperty);
        set => SetValue(ShowFullTitlesProperty, value);
    }

    public int NumberOfLines
    {
        get => (int)GetValue(NumberOfLinesProperty);
        set => SetValue(NumberOfLinesProperty, value);
    }

    private static void AppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((TimelineTextPanel)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2 || Children[0] is not TextBlock title || Children[1] is not TextBlock summary)
            throw new InvalidOperationException("A timeline text panel requires title and summary text blocks.");
        int limit = ShowFullTitles ? 12 : NumberOfLines;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        bool hasTitle = !string.IsNullOrWhiteSpace(HtmlInlines.GetAttributedText(title)?.Text ?? title.Text);
        title.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        title.MaxLines = limit;
        var available = new Size(availableSize.Width, double.PositiveInfinity);
        title.Measure(available);
        int titleLines = hasTitle ? limit : 0;
        if (hasTitle && double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            var font = new TextFont(title.FontSize, title.FontWeight.Weight, title.FontFamily.Source);
            titleLines = Math.Clamp(MultilineTextFieldSizer.NumberOfLinesForMeasuredHeight(
                title.DesiredSize.Height, font, limit), 1, limit);
        }
        int remaining = ShowFullTitles ? (hasTitle ? 0 : 4) : limit - titleLines;
        summary.Visibility = remaining > 0 && !string.IsNullOrWhiteSpace(summary.Text) ? Visibility.Visible : Visibility.Collapsed;
        summary.MaxLines = Math.Max(1, remaining);
        summary.Measure(available);
        double gap = title.Visibility == Visibility.Visible && summary.Visibility == Visibility.Visible ? 3 : 0;
        return new Size(Math.Max(title.DesiredSize.Width, summary.DesiredSize.Width),
            title.DesiredSize.Height + gap + summary.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        foreach (var child in Children)
        {
            if (top > 0 && child.Visibility == Visibility.Visible) top += 3;
            child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
            top += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
