using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinNewsWire.AppShared.Extensions;

namespace WinNewsWire.Helpers;

public readonly record struct TextFieldSizeInfo(Size Size, int NumberOfLinesUsed);

public sealed class MultilineTextFieldSizer
{
    private static readonly Dictionary<(TextFont Font, int Lines), MultilineTextFieldSizer> Sizers = new();
    private readonly int _maximumLines;
    private readonly TextBlock _text;
    private readonly int _singleLineHeight;
    private readonly int _doubleLineHeight;
    private readonly Dictionary<string, Dictionary<int, int>> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<AttributedText, Dictionary<int, int>> _attributedCache = new();

    private MultilineTextFieldSizer(TextFont font, int maximumLines)
    {
        _maximumLines = maximumLines;
        _text = SingleLineTextFieldSizer.CreateTextBlock(font, maximumLines, wrap: true);
        _text.Text = "AqLjJ0/y";
        _singleLineHeight = MeasureHeight(200);
        _text.Text = "AqLjJ0/y\nAqLjJ0/y";
        _doubleLineHeight = MeasureHeight(200);
    }

    public static TextFieldSizeInfo SizeFor(string text, TextFont font, int numberOfLines, int width)
    {
        var sizer = For(font, numberOfLines);
        if (!sizer._cache.TryGetValue(text, out var widths)) sizer._cache[text] = widths = new();
        return sizer.SizeFor(width, widths, () => sizer._text.Text = text);
    }

    public static TextFieldSizeInfo SizeFor(AttributedText text, TextFont font, int numberOfLines, int width)
    {
        if (text.Text.Length == 0) return new(new Size(0, 0), 0);
        var sizer = For(font, numberOfLines);
        if (!sizer._attributedCache.TryGetValue(text, out var widths)) sizer._attributedCache[text] = widths = new();
        return sizer.SizeFor(width, widths, () => HtmlInlines.Render(sizer._text, text));
    }

    public static void EmptyCache() { SingleLineTextFieldSizer.VerifyAccess(); Sizers.Clear(); }

    private static MultilineTextFieldSizer For(TextFont font, int lines)
    {
        SingleLineTextFieldSizer.VerifyAccess();
        ArgumentOutOfRangeException.ThrowIfNegative(lines);
        if (!Sizers.TryGetValue((font, lines), out var sizer)) Sizers[(font, lines)] = sizer = new(font, lines);
        return sizer;
    }

    private TextFieldSizeInfo SizeFor(int width, Dictionary<int, int> heights, Action setText)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        if (!heights.TryGetValue(width, out var height))
        {
            var neighbor = HeightConsideringNeighbors(heights, width);
            if (neighbor is { } known) height = known;
            else { setText(); height = MeasureHeight(width); heights[width] = height; }
        }
        var lineHeight = _doubleLineHeight / 2.0;
        return new(new Size(width, height), lineHeight > 0 ? (int)Math.Round(height / lineHeight) : 0);
    }

    private int MeasureHeight(int width)
    {
        _text.InvalidateMeasure();
        _text.Measure(new Size(width, double.PositiveInfinity));
        return (int)Math.Ceiling(_text.DesiredSize.Height);
    }

    private int? HeightConsideringNeighbors(Dictionary<int, int> heights, int width)
    {
        (int Width, int Height) smaller = (0, 0), larger = (0, 0);
        foreach (var (candidateWidth, candidateHeight) in heights)
        {
            if (candidateWidth < width && Math.Abs(candidateHeight - _singleLineHeight) <= 4) return candidateHeight;
            if (_maximumLines == 2 && candidateWidth > width && Math.Abs(candidateHeight - _doubleLineHeight) <= 4) return candidateHeight;
            if (candidateWidth < width && (smaller.Width == 0 || candidateWidth > smaller.Width)) smaller = (candidateWidth, candidateHeight);
            else if (candidateWidth > width && (larger.Width == 0 || candidateWidth < larger.Width)) larger = (candidateWidth, candidateHeight);
            if (smaller.Width != 0 && larger.Width != 0 && smaller.Height == larger.Height) return smaller.Height;
        }
        return null;
    }
}
