using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class CacheCleanerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.CacheTests." + Guid.NewGuid().ToString("N"));
    private readonly AppDefaults _defaults;
    private static readonly DateTime Now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public CacheCleanerTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        _defaults = new(Path.Combine(AppConfig.DataDirectory, "defaults.json"));
    }

    [Fact]
    public void FirstRunPurgesOnlyTheNamedImageCachesAndPersistsItsMarkers()
    {
        var favicon = AddFile("Favicons", "icon.png");
        var nativeFavicon = AddFile("Caches\\Favicons", "native-icon");
        var image = AddFile("Caches\\Images", "image.png");
        var thumbnail = AddFile("Caches\\Thumbnails", "small.jpg");
        var largeThumbnail = AddFile("Caches\\LargeThumbnails", "large.jpg");
        var feedIcon = AddFile("Caches\\FeedIcons", "feed.png");
        var browser = AddFile("Caches\\WebView2", "browser-data");
        var article = AddFile("Accounts\\Test", "DB.sqlite3");
        CacheCleaner.PurgeIfNecessary(_defaults, Now);
        Assert.False(File.Exists(favicon));
        Assert.False(File.Exists(nativeFavicon));
        Assert.False(File.Exists(image));
        Assert.False(File.Exists(thumbnail));
        Assert.False(File.Exists(largeThumbnail));
        Assert.False(File.Exists(feedIcon));
        Assert.True(File.Exists(browser));
        Assert.True(File.Exists(article));
        var reopened = new AppDefaults(Path.Combine(_directory, "defaults.json"));
        Assert.True(reopened.GetValue(CacheCleaner.ResizingPurgeKey, false));
        Assert.Equal(Now, reopened.GetValue<DateTime?>(CacheCleaner.LastFlushKey));
    }

    [Fact]
    public void PeriodicFlushIsStrictlyAfterThreeDaysAndKeepsNewFeedIconCaches()
    {
        CacheCleaner.PurgeIfNecessary(_defaults, Now);
        var favicon = AddFile("Favicons", "icon.png");
        var nativeFavicon = AddFile("Caches\\Favicons", "native-icon");
        var thumbnail = AddFile("Caches\\Thumbnails", "small.jpg");
        var largeThumbnail = AddFile("Caches\\LargeThumbnails", "large.jpg");
        var feedIcon = AddFile("Caches\\FeedIcons", "feed.png");
        var mapping = AddFile("Caches", "FeedURLToIconURLCache.plist");
        CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(3));
        Assert.True(File.Exists(favicon));
        Assert.True(File.Exists(nativeFavicon));
        Assert.True(File.Exists(thumbnail));
        Assert.True(File.Exists(largeThumbnail));
        Assert.True(File.Exists(mapping));
        CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(3).AddTicks(1));
        Assert.False(File.Exists(favicon));
        Assert.False(File.Exists(nativeFavicon));
        Assert.False(File.Exists(thumbnail));
        Assert.False(File.Exists(largeThumbnail));
        Assert.False(File.Exists(mapping));
        Assert.True(File.Exists(feedIcon));
        Assert.Equal(Now.AddDays(3).AddTicks(1), _defaults.GetValue<DateTime?>(CacheCleaner.LastFlushKey));
    }

    [Fact]
    public void AReadOnlyCacheFailureDoesNotAdvanceTheFlushDate()
    {
        CacheCleaner.PurgeIfNecessary(_defaults, Now);
        var path = AddFile("Favicons", "locked.png");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = Record.Exception(() => CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(4)));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(Now, _defaults.GetValue<DateTime?>(CacheCleaner.LastFlushKey));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(4));
        Assert.False(File.Exists(path));
        Assert.Equal(Now.AddDays(4), _defaults.GetValue<DateTime?>(CacheCleaner.LastFlushKey));
    }

    [Fact]
    public void MissingCachePathsAreAnIdempotentSuccess()
    {
        CacheCleaner.PurgeIfNecessary(_defaults, Now);
        CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(4));
        CacheCleaner.PurgeIfNecessary(_defaults, Now.AddDays(4));
        Assert.Equal(Now.AddDays(4), _defaults.GetValue<DateTime?>(CacheCleaner.LastFlushKey));
    }

    private string AddFile(string folder, string name)
    {
        var path = Path.Combine(_directory, folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "cached");
        return path;
    }

    public void Dispose()
    {
        AppConfig.SetDataDirectoryOverride(null);
        Directory.Delete(_directory, recursive: true);
    }
}
