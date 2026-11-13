using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class HtmlImageAttributeInserter
{
    /// <summary>Prepends already HTML-escaped attributes, including their leading space, without rewriting the existing HTML.</summary>
    public static string InsertAttributes(string attributes, string sourceUrl, string html, Uri? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        ArgumentNullException.ThrowIfNull(html);
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        var receiver = new InserterDelegate(sourceUrl, baseUrl);
        new HtmlScanner(receiver).Parse(bytes);
        if (receiver.InsertionOffsets.Count == 0) return html;
        byte[] inserted = Encoding.UTF8.GetBytes(attributes);
        var result = new ArrayBufferWriter<byte>(checked(bytes.Length + inserted.Length * receiver.InsertionOffsets.Count));
        int copied = 0;
        foreach (int offset in receiver.InsertionOffsets)
        {
            result.Write(bytes.AsSpan(copied, offset - copied));
            result.Write(inserted);
            copied = offset;
        }
        result.Write(bytes.AsSpan(copied));
        return Encoding.UTF8.GetString(result.WrittenSpan);
    }

    private sealed class InserterDelegate(string sourceUrl, Uri? baseUrl) : IHtmlScannerDelegate
    {
        public List<int> InsertionOffsets { get; } = [];
        private int _pictureDepth;

        public void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing)
        {
            if (XmlAscii.EqualsCaseInsensitive(name.Span, "picture"u8))
            {
                if (!selfClosing) _pictureDepth++;
                return;
            }
            if (_pictureDepth != 0 || !XmlAscii.EqualsCaseInsensitive(name.Span, "img"u8)
                || attributes["srcset"] is not null || attributes["data-canonical-src"] is not null
                || HtmlImageParser.ResolveUrl(attributes["src"], baseUrl) != sourceUrl) return;
            if (!MemoryMarshal.TryGetArray(name, out var slice))
                throw new InvalidOperationException("The HTML scanner must return tag names from its input array.");
            InsertionOffsets.Add(slice.Offset + slice.Count);
        }

        public void DidEndTag(HtmlScanner scanner, ReadOnlyMemory<byte> name)
        {
            if (_pictureDepth > 0 && XmlAscii.EqualsCaseInsensitive(name.Span, "picture"u8)) _pictureDepth--;
        }
    }
}
