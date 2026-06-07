using WinNewsWire.Parsers;

namespace WinNewsWire.Web;

/// <summary>Port of <c>HTMLMetadataDownloader</c>.</summary>
public static class HtmlMetadataDownloader
{
    public static async Task<HtmlMetadata?> DownloadAsync(string url, CancellationToken ct = default)
    {
        var result = await Downloader.Shared.DownloadAsync(url, null, ct).ConfigureAwait(false);
        if (!result.Success || result.Data is null) return null;
        var parserData = new ParserData(url, result.Data);
        return HtmlMetadataParser.Parse(parserData);
    }
}
