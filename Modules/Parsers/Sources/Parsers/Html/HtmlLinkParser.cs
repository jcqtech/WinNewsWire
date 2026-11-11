using System.Buffers;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class HtmlLinkParser
{
    public static IReadOnlyList<HtmlLink> ParseLinks(ParserData parserData)
    {
        var receiver = new LinkParserDelegate(Uri.TryCreate(parserData.Url, UriKind.Absolute, out var baseUrl) ? baseUrl : null);
        new HtmlScanner(receiver).Parse(parserData.Data);
        receiver.FlushPending();
        return receiver.Links;
    }

    private sealed class LinkParserDelegate(Uri? baseUrl) : IHtmlScannerDelegate
    {
        public List<HtmlLink> Links { get; } = [];
        private bool _collectingText;
        private string? _pendingUrl, _pendingTitle;
        private readonly ArrayBufferWriter<byte> _pendingText = new();

        public void DidStartTag(HtmlScanner scanner, ReadOnlyMemory<byte> name, HtmlAttributes attributes, bool selfClosing)
        {
            if (IsAnchor(name.Span))
            {
                FlushPending();
                var href = attributes["href"];
                _pendingUrl = href is not null && Uri.TryCreate(baseUrl, href, out var resolved) ? resolved.AbsoluteUri : null;
                _pendingTitle = attributes["title"];
                _pendingText.Clear();
                _collectingText = true;
            }
            else if (_collectingText) _pendingText.Clear();
        }
        public void DidEndTag(HtmlScanner scanner, ReadOnlyMemory<byte> name)
        {
            if (_collectingText && IsAnchor(name.Span)) FlushPending();
        }
        public void DidFindCharacters(HtmlScanner scanner, ReadOnlyMemory<byte> bytes)
        {
            if (_collectingText) _pendingText.Write(bytes.Span);
        }
        public void FlushPending()
        {
            if (!_collectingText) return;
            var text = Encoding.UTF8.GetString(_pendingText.WrittenSpan).Trim();
            Links.Add(new(_pendingUrl, text.Length == 0 ? null : text, _pendingTitle));
            _pendingUrl = _pendingTitle = null;
            _pendingText.Clear();
            _collectingText = false;
        }
        private static bool IsAnchor(ReadOnlySpan<byte> name) => name.Length == 1 && name[0] is XmlAscii.LowerA or XmlAscii.UpperA;
    }
}
