namespace WinNewsWire.AppShared;

public enum TimelineThumbnailsDisplay
{
    Small = 0,
    Large = 1,
    None = 2
}

public static class TimelineThumbnailsDisplayExtensions
{
    public static string DisplayString(this TimelineThumbnailsDisplay display)
        => AppStrings.Get(display switch
        {
            TimelineThumbnailsDisplay.Small => "Small",
            TimelineThumbnailsDisplay.Large => "Large",
            TimelineThumbnailsDisplay.None => "None",
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        });
}
