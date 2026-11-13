using System.Globalization;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class HtmlImageParser
{
    public static IReadOnlyList<string> SignificantImageUrlStrings(string html, Uri? baseUrl, int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        if (maximumCount == 0) return [];
        var receiver = new ImageParserDelegate(baseUrl, maximumCount);
        new HtmlScanner(receiver).Parse(Encoding.UTF8.GetBytes(html));
        return receiver.UrlStrings;
    }

    private sealed class ImageParserDelegate(Uri? baseUrl, int maximumCount) : IHtmlScannerDelegate
    {
        public List<string> UrlStrings { get; } = [];
        private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
        private string? _enclosingLinkUrl;

        public void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing)
        {
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "a"u8))
            {
                _enclosingLinkUrl = ResolveUrl(attributes["href"]);
                return;
            }
            if (!XmlAscii.EqualsCaseInsensitive(name.Span, "img"u8)) return;
            var style = StyleProperties(attributes["style"]);
            if (attributes["hidden"] is not null
                || style.GetValueOrDefault("display")?.StartsWith("none", StringComparison.OrdinalIgnoreCase) == true) return;
            var width = PixelDimension(style.GetValueOrDefault("width")) ?? PixelDimension(attributes["width"]);
            var height = PixelDimension(style.GetValueOrDefault("height")) ?? PixelDimension(attributes["height"]);
            if (!SignificantImageFilter.HasSignificantDimensions(width, height)) return;
            foreach (var source in ImageSources(attributes))
            {
                var url = ResolveUrl(source);
                if (url is null || !SignificantImageFilter.IsSignificant(url, width, height, attributes["class"], _enclosingLinkUrl)) continue;
                if (_seenUrls.Add(url)) UrlStrings.Add(url);
                if (UrlStrings.Count >= maximumCount) scanner.StopParsing();
                return;
            }
        }

        public void DidEndTag(HtmlScanner scanner, ReadOnlyMemory<byte> name)
        {
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "a"u8)) _enclosingLinkUrl = null;
        }

        private string? ResolveUrl(string? value)
        {
            if (value is null || value.AsSpan().TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            value = value.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (baseUrl is not null && Uri.TryCreate(baseUrl, value, out var resolved)) return resolved.AbsoluteUri;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute.AbsoluteUri;
            return null;
        }

        private static IEnumerable<string?> ImageSources(HtmlAttributes attributes)
        {
            yield return PreferredSrcsetCandidate(attributes["data-lazy-srcset"]);
            yield return PreferredSrcsetCandidate(attributes["data-srcset"]);
            yield return attributes["data-lazy-src"];
            yield return attributes["data-src"];
            yield return attributes["data-original"];
            yield return PreferredSrcsetCandidate(attributes["srcset"]);
            yield return attributes["src"];
        }
    }

    private static string? PreferredSrcsetCandidate(string? value)
    {
        if (value is null) return null;
        var remaining = value.AsSpan();
        string? largestUrl = null;
        var largestSize = double.NegativeInfinity;
        string? preferredUrl = null;
        var preferredSize = double.PositiveInfinity;
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
            var sizeDescriptorLength = 0;
            while (sizeDescriptorLength < descriptor.Length && !char.IsWhiteSpace(descriptor[sizeDescriptorLength])) sizeDescriptorLength++;
            descriptor = descriptor[..sizeDescriptorLength];
            var size = descriptor.Length > 0 && double.TryParse(descriptor[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
            if (url.IsEmpty) continue;
            if (descriptor.EndsWith("w") && size >= 1200 && size < preferredSize)
            {
                preferredUrl = url.ToString();
                preferredSize = size;
            }
            if (size > largestSize)
            {
                largestUrl = url.ToString();
                largestSize = size;
            }
        }
        return preferredUrl ?? largestUrl;
    }

    private static int? PixelDimension(string? value)
    {
        var dimension = value?.Trim();
        if (dimension is null) return null;
        if (dimension.EndsWith("px", StringComparison.OrdinalIgnoreCase)) dimension = dimension[..^2];
        return int.TryParse(dimension, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static Dictionary<string, string> StyleProperties(string? style)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in style?.Split(';') ?? [])
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0) continue;
            properties[declaration[..separator].Trim()] = declaration[(separator + 1)..].Trim();
        }
        return properties;
    }
}
