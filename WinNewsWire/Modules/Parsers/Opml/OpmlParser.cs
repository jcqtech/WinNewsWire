using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSOPMLParser</c>.</summary>
public static class OpmlParser
{
    public static OpmlDocument Parse(ParserData parserData)
    {
        using var stream = new MemoryStream(parserData.Data);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(stream, settings);

        string? docTitle = null;
        var rootChildren = new List<OpmlItem>();
        var rootAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool foundOpmlRoot = false;

        try
        {
            // Find the first element — must be <opml>. Otherwise this is not
            // an OPML document and we throw to match NetNewsWire's
            // `testNotOPML` behavior (an RSS/Atom/JSON file fed to the OPML
            // parser surfaces as an error instead of an empty document).
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (string.Equals(reader.LocalName, "opml", StringComparison.OrdinalIgnoreCase))
                {
                    foundOpmlRoot = true;
                    break;
                }
                throw new FeedParserException(FeedParserErrorKind.InvalidXml,
                    $"Root element is <{reader.LocalName}>, not <opml>.");
            }
            if (!foundOpmlRoot)
                throw new FeedParserException(FeedParserErrorKind.InvalidXml,
                    "OPML root element not found.");

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "title") docTitle = reader.ReadElementContentAsString()?.Trim();
                else if (reader.LocalName == "body") { ReadOutlines(reader, rootChildren); }
            }
        }
        catch (XmlException ex)
        {
            throw new FeedParserException(FeedParserErrorKind.InvalidXml, ex.Message, ex);
        }

        return new OpmlDocument(docTitle, parserData.Url, rootAttrs, rootChildren);
    }

    private static void ReadOutlines(XmlReader reader, List<OpmlItem> into)
    {
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement) return;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "outline") into.Add(ReadOutline(reader));
            else if (!reader.IsEmptyElement) reader.Skip();
        }
    }

    private static OpmlItem ReadOutline(XmlReader reader)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute()) attrs[reader.LocalName] = reader.Value;
            reader.MoveToElement();
        }
        var children = new List<OpmlItem>();
        if (!reader.IsEmptyElement) ReadOutlines(reader, children);
        return new OpmlItem(attrs, children);
    }
}
