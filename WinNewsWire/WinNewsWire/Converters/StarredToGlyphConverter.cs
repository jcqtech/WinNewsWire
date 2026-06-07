using System;
using Microsoft.UI.Xaml.Data;

namespace WinNewsWire.Converters;

/// <summary>
/// Converts a string to a star glyph. Filled star when starred, outline when not.
/// </summary>
public class StarredToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isStarred && isStarred ? "\uE735" : "\uE734"; // FavoriteStar filled / outline
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
