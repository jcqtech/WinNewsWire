using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinNewsWire.Converters;

/// <summary>
/// Converts a bool (IsRead) to an unread dot visibility.
/// Shows the dot when the article is NOT read.
/// </summary>
public class IsReadToUnreadDotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isRead && !isRead ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
