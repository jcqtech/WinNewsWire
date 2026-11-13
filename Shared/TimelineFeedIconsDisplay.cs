using WinNewsWire.Articles;
using WinNewsWire.AppShared.Extensions;

namespace WinNewsWire.AppShared;

public enum TimelineFeedIconsDisplay
{
    Automatic = 0,
    Always = 1,
    Never = 2
}

public static class TimelineFeedIconsDisplayExtensions
{
    public static string DisplayString(this TimelineFeedIconsDisplay display)
        => AppStrings.Get(display switch
        {
            TimelineFeedIconsDisplay.Automatic => "Automatic",
            TimelineFeedIconsDisplay.Always => "Always",
            TimelineFeedIconsDisplay.Never => "Never",
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        });

    public static bool ShowsIcons(this TimelineFeedIconsDisplay display, bool singleFeed, IEnumerable<Article> articles)
        => display switch
        {
            TimelineFeedIconsDisplay.Always => true,
            TimelineFeedIconsDisplay.Never => false,
            TimelineFeedIconsDisplay.Automatic => !singleFeed
                || articles.Any(article => article.Authors?.Any(author => author.AvatarUrl is not null) == true)
                    && articles.Any(article => !string.IsNullOrEmpty(article.Byline())),
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        };
}
