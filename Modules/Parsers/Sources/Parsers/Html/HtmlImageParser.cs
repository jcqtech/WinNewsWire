using System.Globalization;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public sealed record HtmlImage(string Url, int? Width, int? Height);

public static class HtmlImageParser
{
    public static HtmlImage? FirstSignificantImage(ParserData parserData)
    {
        var receiver = new ImageParserDelegate(Uri.TryCreate(parserData.Url, UriKind.Absolute, out var baseUrl) ? baseUrl : null);
        new HtmlScanner(receiver).Parse(parserData.Data);
        return receiver.Image;
    }

    private sealed class ImageParserDelegate(Uri? baseUrl) : IHtmlScannerDelegate
    {
        public HtmlImage? Image { get; private set; }
        private string? _enclosingLinkUrl;

        public void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing)
        {
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "a"u8))
            {
                _enclosingLinkUrl = ResolveUrl(attributes["href"]);
                return;
            }
            if (!XmlAscii.EqualsCaseInsensitive(name.Span, "img"u8)) return;
            var width = PixelDimension(attributes["width"]);
            var height = PixelDimension(attributes["height"]);
            foreach (var source in ImageSources(attributes))
            {
                var url = ResolveUrl(source);
                if (url is null || !SignificantImageFilter.IsSignificant(url, width, height, attributes["class"], _enclosingLinkUrl)) continue;
                Image = new(url, width, height);
                scanner.StopParsing();
                return;
            }
        }

        public void DidEndTag(HtmlScanner scanner, ReadOnlyMemory<byte> name)
        {
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "a"u8)) _enclosingLinkUrl = null;
        }

        private string? ResolveUrl(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            if (baseUrl is not null && Uri.TryCreate(baseUrl, value, out var resolved)) return resolved.AbsoluteUri;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute.AbsoluteUri;
            return null;
        }

        private static IEnumerable<string?> ImageSources(HtmlAttributes attributes)
        {
            yield return attributes["data-lazy-src"];
            yield return attributes["data-src"];
            yield return attributes["src"];
            yield return LargestSrcsetCandidate(attributes["srcset"]);
        }
    }

    private static string? LargestSrcsetCandidate(string? value)
    {
        if (value is null) return null;
        var remaining = value.AsSpan();
        string? largestUrl = null;
        var largestSize = double.NegativeInfinity;
        while (!remaining.IsEmpty)
        {
            var skip = 0;
            while (skip < remaining.Length && (char.IsWhiteSpace(remaining[skip]) || remaining[skip] == ',')) skip++;
            remaining = remaining[skip..];
            if (remaining.IsEmpty) break;
            var length = 0;
            while (length < remaining.Length && !char.IsWhiteSpace(remaining[length])) length++;
            var url = remaining[..length];
            remaining = remaining[length..];
            ReadOnlySpan<char> descriptor = [];
            if (url.EndsWith(",")) url = url.TrimEnd(',');
            else
            {
                var separator = remaining.IndexOf(',');
                var descriptorLength = separator < 0 ? remaining.Length : separator;
                descriptor = remaining[..descriptorLength].Trim();
                remaining = remaining[descriptorLength..];
            }
            var size = descriptor.Length > 0 && double.TryParse(descriptor[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
            if (url.IsEmpty || size <= largestSize) continue;
            largestUrl = url.ToString();
            largestSize = size;
        }
        return largestUrl;
    }

    private static int? PixelDimension(string? value)
    {
        var dimension = value?.Trim();
        if (dimension is null) return null;
        if (dimension.EndsWith("px", StringComparison.OrdinalIgnoreCase)) dimension = dimension[..^2];
        return int.TryParse(dimension, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
