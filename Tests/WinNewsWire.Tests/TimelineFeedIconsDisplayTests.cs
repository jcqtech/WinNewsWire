using WinNewsWire.Articles;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class TimelineFeedIconsDisplayTests
{
    [Theory]
    [InlineData(TimelineFeedIconsDisplay.Automatic, false, false, false, true)]
    [InlineData(TimelineFeedIconsDisplay.Automatic, true, false, false, false)]
    [InlineData(TimelineFeedIconsDisplay.Automatic, true, true, false, false)]
    [InlineData(TimelineFeedIconsDisplay.Automatic, true, false, true, false)]
    [InlineData(TimelineFeedIconsDisplay.Automatic, true, true, true, true)]
    [InlineData(TimelineFeedIconsDisplay.Always, true, false, false, true)]
    [InlineData(TimelineFeedIconsDisplay.Never, false, true, true, false)]
    public void AutomaticUsesContainerBylineAndAvatarWhileExplicitChoicesOverrideThem(
        TimelineFeedIconsDisplay mode, bool singleFeed, bool byline, bool avatar, bool expected)
        => Assert.Equal(expected, mode.ShowsIcons(singleFeed, [ArticleFor(byline, avatar)]));

    [Fact]
    public void BylineAndAvatarCanBelongToDifferentArticlesInTheSameFeed()
        => Assert.True(TimelineFeedIconsDisplay.Automatic.ShowsIcons(true,
            [ArticleFor(byline: true, avatar: false), ArticleFor(byline: false, avatar: true)]));

    [Fact]
    public void InvalidStoredAndNewValuesDoNotReplaceTheNativeFallbackOrLastValidSetting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "feed-icon-display-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "defaults.json");
        try
        {
            var defaults = new AppDefaults(path);
            defaults.SetValue(AppDefaultsSharedExtensions.TimelineFeedIconsDisplayKey, 99);
            Assert.Equal(TimelineFeedIconsDisplay.Automatic, defaults.GetTimelineFeedIconsDisplay());
            Assert.Equal(99, defaults.GetValue<int>(AppDefaultsSharedExtensions.TimelineFeedIconsDisplayKey));
            defaults.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Never);
            int notifications = 0;
            defaults.Changed += (_, _) => notifications++;
            Assert.Throws<ArgumentOutOfRangeException>(() => defaults.SetTimelineFeedIconsDisplay((TimelineFeedIconsDisplay)(-1)));
            Assert.Equal(TimelineFeedIconsDisplay.Never, new AppDefaults(path).GetTimelineFeedIconsDisplay());
            Assert.Equal(0, notifications);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static Article ArticleFor(bool byline, bool avatar)
    {
        var author = Author.Create(null, byline ? "Writer" : "", null,
            avatar ? "https://author.test/avatar.png" : null, null)!;
        var id = Guid.NewGuid().ToString("N");
        return new Article("account", id, "feed", id, "Title", null, null, null, null, null, null, null,
            null, null, new HashSet<Author> { author }, null, new ArticleStatus(id, true, false, DateTime.UnixEpoch));
    }
}
