using WinNewsWire.Core;

namespace WinNewsWire.AppShared.ArticleThemes;

public static class ArticleThemeResource
{
    public static Task<(byte[] Data, string ContentType)> ReadAsync(
        string folder, string escapedPath, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            var path = Path.GetFullPath(folder);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked theme folders cannot serve article resources.");
            var segments = escapedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) throw new FileNotFoundException("The theme resource is not a file.");
            foreach (var encoded in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = Uri.UnescapeDataString(encoded);
                if (!FileUtilities.IsValidWindowsFileName(segment))
                    throw new InvalidDataException("The theme resource path is invalid.");
                path = Path.Combine(path, segment);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked files cannot serve article resources.");
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > ArticleThemeDownloader.MaximumThemeSize)
                throw new InvalidDataException("The theme resource exceeds the theme size limit.");
            var data = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".css" => "text/css; charset=utf-8",
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".json" => "application/json",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".avif" => "image/avif",
                ".ico" => "image/x-icon",
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".otf" => "font/otf",
                _ => "application/octet-stream"
            };
            return (data, contentType);
        }, cancellationToken);
}
