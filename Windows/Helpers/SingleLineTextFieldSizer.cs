using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinNewsWire.Helpers;

public readonly record struct TextFont(double Size, ushort Weight = 400, string Family = "Segoe UI");

public sealed class SingleLineTextFieldSizer
{
    private static readonly Dictionary<TextFont, SingleLineTextFieldSizer> Sizers = new();
    private readonly Dictionary<string, Size> _cache = new(StringComparer.Ordinal);
    private readonly TextBlock _text;

    private SingleLineTextFieldSizer(TextFont font) => _text = CreateTextBlock(font);

    public static Size SizeFor(string text, TextFont font)
    {
        VerifyAccess();
        if (!Sizers.TryGetValue(font, out var sizer)) Sizers[font] = sizer = new(font);
        if (sizer._cache.TryGetValue(text, out var cached)) return cached;
        sizer._text.Text = text;
        sizer._text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = sizer._text.DesiredSize;
        return sizer._cache[text] = new Size(Math.Ceiling(measured.Width), Math.Ceiling(measured.Height));
    }

    public static void EmptyCache() { VerifyAccess(); Sizers.Clear(); }

    internal static TextBlock CreateTextBlock(TextFont font, int maximumLines = 1, bool wrap = false)
        => new()
        {
            FontSize = font.Size,
            FontWeight = new global::Windows.UI.Text.FontWeight { Weight = font.Weight },
            FontFamily = new FontFamily(font.Family),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxLines = maximumLines,
            TextTrimming = TextTrimming.None
        };

    internal static void VerifyAccess()
    {
        if (DispatcherQueue.GetForCurrentThread()?.HasThreadAccess != true)
            throw new InvalidOperationException("Text measurements require the WinUI thread.");
    }
}
