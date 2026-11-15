using System.IO.Compression;
using System.Diagnostics;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire.AppShared.ArticleThemes;

public static class ArticleThemeDownloader
{
    public const long MaximumThemeSize = 10_000_000;

    public static async Task<ArticleTheme> InstallFromUrlAsync(Uri url, ArticleThemesManager manager,
        CancellationToken cancellationToken = default, Downloader? downloader = null)
    {
        using var prepared = await PrepareFromUrlAsync(url, manager, cancellationToken, downloader).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return prepared.Install(overwriteExisting: true);
    }

    public static async Task<ArticleThemeImport> PrepareFromUrlAsync(Uri url, ArticleThemesManager manager,
        CancellationToken cancellationToken = default, Downloader? downloader = null)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("Article themes can be downloaded only from an HTTP or HTTPS URL.", nameof(url));
        cancellationToken.ThrowIfCancellationRequested();
        var result = await (downloader ?? Downloader.Shared).DownloadAsync(
            url.AbsoluteUri, ct: cancellationToken, userAgentStyle: UserAgentStyle.Browser).ConfigureAwait(false);
        if (!result.Success || result.Data is not { Length: > 0 })
            throw new InvalidDataException("The article theme could not be downloaded.");
        if (result.Data.Length > MaximumThemeSize) throw new InvalidDataException("The article theme exceeds the 10 MB size limit.");

        var temporaryPath = Path.Combine(manager.FolderPath, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, result.Data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return manager.PrepareImport(temporaryPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"Could not remove downloaded theme archive {temporaryPath}: {error.Message}");
            }
        }
    }

    public static string UnzipTheme(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root) && ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(root).Any()))
            throw new IOException("Extract themes into a new, empty directory.");
        using var archive = ZipFile.OpenRead(archivePath);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!FileUtilities.IsValidWindowsFileName(segment))
                    throw new InvalidDataException("The theme archive contains a filename that is unsafe on Windows.");
            }
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || relative.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The theme archive contains an unsafe path or link.");
            if (!destinations.Add(path) && entry.Name.Length > 0)
                throw new InvalidDataException("The theme archive contains duplicate Windows filenames.");
            if (entry.Length > MaximumThemeSize - expanded) throw new InvalidDataException("The theme exceeds the 10 MB size limit.");
            expanded += entry.Length;
        }
        archive.ExtractToDirectory(root);
        return Directory.EnumerateDirectories(root, "*" + ArticleTheme.ThemeExtension, SearchOption.AllDirectories)
            .FirstOrDefault(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Contains("__MACOSX", StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("There is no NetNewsWire theme in this archive.");
    }
}
