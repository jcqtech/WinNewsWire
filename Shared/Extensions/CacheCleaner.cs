using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Extensions;

public static class CacheCleaner
{
    internal const string DownloadedImagesPurgeKey = "didRemoveFoldersReplacedByDownloadedImageCache-2026-10-27";

    public static void PurgeIfNecessary(AppDefaults defaults)
    {
        if (defaults.GetValue(DownloadedImagesPurgeKey, false)) return;
        var caches = AppConfig.CachesDirectory;
        var targets = new[]
        {
            Path.Combine(AppConfig.DataDirectory, "Favicons"),
            Path.Combine(caches, "Favicons"),
            Path.Combine(caches, "Images"),
            Path.Combine(caches, "FeedIcons"),
            Path.Combine(caches, "Thumbnails"),
            Path.Combine(caches, "LargeThumbnails")
        };
        foreach (var path in targets)
        {
            try { DeleteCachePath(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not purge image cache {path}: {error}");
                throw;
            }
        }
        defaults.SetValue(DownloadedImagesPurgeKey, true);
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
