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
        var downloaded = AddFile("Caches\\DownloadedImages", "source");
        var image = AddFile("Caches\\Images", "image.png");
        var thumbnail = AddFile("Caches\\Thumbnails", "small.jpg");
        var largeThumbnail = AddFile("Caches\\LargeThumbnails", "large.jpg");
        var feedIcon = AddFile("Caches\\FeedIcons", "feed.png");
        var browser = AddFile("Caches\\WebView2", "browser-data");
        var article = AddFile("Accounts\\Test", "DB.sqlite3");
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.False(File.Exists(favicon));
        Assert.False(File.Exists(nativeFavicon));
        Assert.True(File.Exists(downloaded));
        Assert.False(File.Exists(image));
        Assert.False(File.Exists(thumbnail));
        Assert.False(File.Exists(largeThumbnail));
        Assert.False(File.Exists(feedIcon));
        Assert.True(File.Exists(browser));
        Assert.True(File.Exists(article));
        var reopened = new AppDefaults(Path.Combine(_directory, "defaults.json"));
        Assert.True(reopened.GetValue(CacheCleaner.DownloadedImagesPurgeKey, false));
    }

    [Fact]
    public void CompletedCleanupIgnoresAnExpiredLegacyFlushDateAndPreservesCaches()
    {
        CacheCleaner.PurgeIfNecessary(_defaults);
        var favicon = AddFile("Favicons", "icon.png");
        var nativeFavicon = AddFile("Caches\\Favicons", "native-icon");
        var downloaded = AddFile("Caches\\DownloadedImages", "source");
        var feedIcon = AddFile("Caches\\FeedIcons", "feed.png");
        var mapping = AddFile("Caches", "FeedURLToIconURLCache.plist");
        _defaults.SetValue("lastImageCacheFlushDate", Now);
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.True(File.Exists(favicon));
        Assert.True(File.Exists(nativeFavicon));
        Assert.True(File.Exists(downloaded));
        Assert.True(File.Exists(mapping));
        Assert.True(File.Exists(feedIcon));
        Assert.Equal(Now, _defaults.GetValue<DateTime?>("lastImageCacheFlushDate"));
    }

    [Fact]
    public void UpgradingRemovesAllRetiredImageFoldersOnceWithoutDeletingSharedSources()
    {
        _defaults.SetValue("didPurgeImageCachesForResizing-2026-03-30", true);
        _defaults.SetValue("didRemoveFoldersReplacedByDownloadedImageCache-2026-10-26", true);
        var old = new[] { "Favicons", "Images", "FeedIcons", "Thumbnails", "LargeThumbnails" }
            .Select(folder => AddFile("Caches\\" + folder, "retired")).ToArray();
        var source = AddFile("Caches\\DownloadedImages", "source");
        var favicon = AddFile("Caches\\Favicons", "icon");
        var legacyFavicon = AddFile("Favicons", "legacy");
        var browser = AddFile("Caches\\WebView2", "data");
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.All(old, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(favicon));
        Assert.False(File.Exists(legacyFavicon));
        Assert.True(File.Exists(browser));
        var laterFile = AddFile("Caches\\Images", "not-from-the-upgrade");
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.True(File.Exists(laterFile));
    }

    [Fact]
    public void CompletedRetiredFolderCleanupDoesNotRunAnObsoleteResizePurge()
    {
        _defaults.SetValue(CacheCleaner.DownloadedImagesPurgeKey, true);
        var favicon = AddFile("Caches\\Favicons", "not-from-the-upgrade");
        var feedIcon = AddFile("Caches\\FeedIcons", "not-from-the-upgrade");
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.True(File.Exists(favicon));
        Assert.True(File.Exists(feedIcon));
    }

    [Fact]
    public void AReadOnlyRetiredFileDoesNotAdvanceTheCompletionMarker()
    {
        var path = AddFile("Caches\\FeedIcons", "retired");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var error = Record.Exception(() => CacheCleaner.PurgeIfNecessary(_defaults));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(_defaults.GetValue(CacheCleaner.DownloadedImagesPurgeKey, false));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.False(File.Exists(path));
        Assert.True(_defaults.GetValue(CacheCleaner.DownloadedImagesPurgeKey, false));
    }

    [Fact]
    public void MissingCachePathsAreAnIdempotentSuccess()
    {
        CacheCleaner.PurgeIfNecessary(_defaults);
        CacheCleaner.PurgeIfNecessary(_defaults);
        CacheCleaner.PurgeIfNecessary(_defaults);
        Assert.True(_defaults.GetValue(CacheCleaner.DownloadedImagesPurgeKey, false));
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
