using System;
using Microsoft.UI.Xaml.Data;

namespace WinNewsWire.Converters;

public class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not DateTimeOffset date || date == DateTimeOffset.MinValue)
            return string.Empty;

        var now = DateTimeOffset.Now;
        var diff = now - date;

        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";

        return date.LocalDateTime.ToString("MMM d, yyyy");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
