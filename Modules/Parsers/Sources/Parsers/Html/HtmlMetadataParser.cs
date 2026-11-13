using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class HtmlMetadataParser
{
    public static HtmlMetadata Parse(ParserData parserData)
    {
        var receiver = new MetadataParserDelegate(parserData.Url.Contains("youtube", StringComparison.OrdinalIgnoreCase));
        new HtmlScanner(receiver).Parse(parserData.Data);
        return new HtmlMetadata(parserData.Url, receiver.Tags);
    }

    private sealed class MetadataParserDelegate(bool scanPastHead) : IHtmlScannerDelegate
    {
        public List<HtmlTag> Tags { get; } = [];
        public void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing)
        {
            if (!scanPastHead && XmlAscii.EqualsCaseInsensitive(name.Span, "body"u8)) { scanner.StopParsing(); return; }
            if (attributes.IsEmpty) return;
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "link"u8))
            {
                if (string.IsNullOrEmpty(attributes["rel"]) || string.IsNullOrEmpty(attributes["href"] ?? attributes["src"])) return;
                Tags.Add(new(HtmlTagType.Link, attributes.Dictionary()));
            }
            else if (XmlAscii.EqualsCaseInsensitive(name.Span, "meta"u8))
                Tags.Add(new(HtmlTagType.Meta, attributes.Dictionary()));
        }
    }
}
