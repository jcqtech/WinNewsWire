using WinNewsWire.Images;

namespace WinNewsWire.AppShared;

public enum TimelineThumbnailsDisplay
{
    Small = 0,
    Large = 1,
    None = 2
}

public static class TimelineThumbnailsDisplayExtensions
{
    public static ThumbnailMode? ToThumbnailMode(this TimelineThumbnailsDisplay display) => display switch
    {
        TimelineThumbnailsDisplay.Small => ThumbnailMode.Small,
        TimelineThumbnailsDisplay.Large => ThumbnailMode.Large,
        TimelineThumbnailsDisplay.None => null,
        _ => throw new ArgumentOutOfRangeException(nameof(display))
    };

    public static string DisplayString(this TimelineThumbnailsDisplay display)
        => AppStrings.Get(display switch
        {
            TimelineThumbnailsDisplay.Small => "Small",
            TimelineThumbnailsDisplay.Large => "Large",
            TimelineThumbnailsDisplay.None => "None",
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        });
}
