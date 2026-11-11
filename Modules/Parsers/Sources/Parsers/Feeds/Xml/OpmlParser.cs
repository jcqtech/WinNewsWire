using System.Xml;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class OpmlParser
{
    public static OpmlDocument Parse(ParserData parserData)
    {
        var rootAttributes = ValidateDocument(parserData.Data);
        var receiver = new OpmlParserDelegate();
        new XmlSaxParser(receiver).Parse(parserData.Data);
        return new OpmlDocument(receiver.Title, parserData.Url, rootAttributes, receiver.Children);
    }

    private static Dictionary<string, string> ValidateDocument(byte[] bytes)
    {
        // Keep Windows' all-or-nothing import validation before the liberal SAX pass.
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null
        });
        Dictionary<string, string>? rootAttributes = null;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || rootAttributes is not null) continue;
                if (!reader.LocalName.Equals("opml", StringComparison.OrdinalIgnoreCase))
                    throw new FeedParserException(FeedParserErrorKind.InvalidXml, $"Root element is <{reader.LocalName}>, not <opml>.");
                rootAttributes = new(StringComparer.OrdinalIgnoreCase);
                while (reader.MoveToNextAttribute()) rootAttributes[reader.Name] = reader.Value;
                reader.MoveToElement();
            }
        }
        catch (XmlException error)
        {
            throw new FeedParserException(FeedParserErrorKind.InvalidXml, error.Message, error);
        }
        return rootAttributes ?? throw new FeedParserException(FeedParserErrorKind.InvalidXml, "OPML root element not found.");
    }

    private sealed class OpmlParserDelegate : IXmlSaxParserDelegate
    {
        public List<OpmlItem> Children { get; } = [];
        public string? Title { get; private set; }
        private readonly Stack<List<OpmlItem>> _itemChildren = [];
        private bool _collectingDocumentTitle;

        public void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns, XmlAttributes attributes)
        {
            if (localName.Span.SequenceEqual("title"u8))
            {
                parser.BeginStoringCharacters();
                _collectingDocumentTitle = _itemChildren.Count == 0;
                return;
            }
            if (!localName.Span.SequenceEqual("outline"u8)) return;
            var children = new List<OpmlItem>();
            var item = new OpmlItem(attributes.Dictionary(), children);
            (_itemChildren.TryPeek(out var parent) ? parent : Children).Add(item);
            _itemChildren.Push(children);
        }

        public void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns)
        {
            if (localName.Span.SequenceEqual("title"u8))
            {
                if (_collectingDocumentTitle) Title = parser.CurrentStringWithTrimmedWhitespace;
                _collectingDocumentTitle = false;
            }
            else if (localName.Span.SequenceEqual("outline"u8)) _itemChildren.TryPop(out _);
        }
    }
}
