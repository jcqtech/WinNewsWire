namespace WinNewsWire.Parsers.Xml;

public interface IXmlSaxParserDelegate
{
    void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns, XmlAttributes attributes) { }
    void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns) { }
    void DidFindCharacters(XmlSaxParser parser, ReadOnlyMemory<byte> bytes) { }
    void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlyMemory<byte> bytes, ReadOnlyMemory<byte> localName, XmlNamespace ns) { }
    void DidEnd(XmlSaxParser parser) { }
}
