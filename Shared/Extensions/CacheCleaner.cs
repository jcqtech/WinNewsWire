using System.Diagnostics;
using System.Text.Json;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Extensions;

public static class CacheCleaner
{
    internal const string ResizingPurgeKey = "didPurgeImageCachesForResizing-2026-03-30";
    internal const string LastFlushKey = "lastImageCacheFlushDate";

    public static void PurgeIfNecessary(AppDefaults defaults, DateTime? now = null)
    {
        var date = now ?? DateTime.UtcNow;
        var resized = defaults.GetValue(ResizingPurgeKey, false);
        var last = defaults.GetValue<DateTime?>(LastFlushKey);
        bool periodicFlush = last is { } previous && date - previous > TimeSpan.FromDays(3);
        var caches = AppConfig.CachesDirectory;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!resized || periodicFlush)
        {
            targets.Add(Path.Combine(AppConfig.DataDirectory, "Favicons"));
            targets.Add(Path.Combine(caches, "Favicons"));
            targets.Add(Path.Combine(caches, "Images"));
        }
        if (!resized) targets.Add(Path.Combine(caches, "FeedIcons"));
        if (periodicFlush)
        {
            targets.Add(Path.Combine(caches, "FeedURLToIconURLCache.plist"));
            targets.Add(Path.Combine(caches, "HomePageToIconURLCache.plist"));
            targets.Add(Path.Combine(caches, "HomePagesWithNoIconURLCache.plist"));
        }
        foreach (var path in targets)
        {
            try { DeleteCachePath(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not purge image cache {path}: {error}");
                throw;
            }
        }
        var changes = new Dictionary<string, JsonElement>();
        if (!resized) changes[ResizingPurgeKey] = JsonSerializer.SerializeToElement(true);
        if (last is null || periodicFlush) changes[LastFlushKey] = JsonSerializer.SerializeToElement(date);
        defaults.SetValues(changes);
    }

    private static void DeleteCachePath(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Image cache cleanup will not traverse a filesystem link: " + path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteCachePath(child);
        Directory.Delete(path);
    }
}
