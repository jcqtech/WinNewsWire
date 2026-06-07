using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinNewsWire.Converters;

/// <summary>
/// Converts a local file path (as produced by <c>FaviconDownloader.FaviconPathAsync</c>) to a
/// <see cref="BitmapImage"/> suitable for binding to <c>Image.Source</c>. Returns null for
/// empty strings so the bound <c>Image</c> stays hidden via <c>TargetNullValue</c>.
/// </summary>
public class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var uri = Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : new Uri(s, UriKind.RelativeOrAbsolute);
            return new BitmapImage(uri);
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
