using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinNewsWire.Converters;

/// <summary>
/// Converts a local path or revisioned feed-image reference to a
/// <see cref="BitmapImage"/> suitable for binding to <c>Image.Source</c>. Returns null for
/// empty strings so the bound <c>Image</c> stays hidden via <c>TargetNullValue</c>.
/// </summary>
public class PathToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var reference = value is Models.FeedImageReference image ? image : new(value as string, 0);
        if (reference.Path is not { Length: > 0 } s || string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var uri = Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : new Uri(s, UriKind.RelativeOrAbsolute);
            if (s.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return new SvgImageSource(uri);
            return new BitmapImage
            {
                CreateOptions = reference.Revision > 0 ? BitmapCreateOptions.IgnoreImageCache : BitmapCreateOptions.None,
                UriSource = uri
            };
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not display favicon {s}: {error.Message}");
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
