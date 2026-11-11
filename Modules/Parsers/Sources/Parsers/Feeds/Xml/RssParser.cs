using System.Globalization;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class RssParser
{
    public static ParsedFeed Parse(ParserData parserData)
    {
        var receiver = new RssDelegate(parserData.Url);
        new XmlSaxParser(receiver).Parse(parserData.Data);
        return receiver.BuildParsedFeed();
    }

    private sealed class RssDelegate(string feedUrl) : IXmlSaxParserDelegate
    {
        private string? _title, _homepageUrl, _language, _channelImageUrl;
        private bool _isRdf, _endRssFound, _parsingArticle, _parsingAuthor, _parsingChannelImage, _parsingTextInput;
        private int _namespaceElementDepth;
        private readonly List<RssItem> _items = [];
        private readonly HashSet<RssItem> _rdfAboutItems = [];
        private Dictionary<string, string>? _currentAttributes;
        private RssItem? CurrentItem => _items.LastOrDefault();

        public ParsedFeed BuildParsedFeed() => new(
            FeedType.Rss, _title, _homepageUrl, feedUrl, _language,
            FeedDescription: null, NextUrl: null, IconUrl: _channelImageUrl, FaviconUrl: null,
            Authors: null, Expired: false, Hubs: null,
            Items: _items.Select(item => item.ToParsedItem(feedUrl, LegacyArticleIdentity.Rss(item, _rdfAboutItems.Contains(item)))).ToHashSet());

        public void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns, XmlAttributes attributes)
        {
            if (_endRssFound) return;
            var name = localName.Span;
            var unprefixed = ns.Prefix is null;
            if (_namespaceElementDepth > 0) { _namespaceElementDepth++; return; }
            if (_parsingArticle && !unprefixed && !ns.IsDublinCore && !ns.IsContent && !ns.IsSource)
            {
                _namespaceElementDepth = 1;
                return;
            }
            if (name.SequenceEqual("RDF"u8)) { _isRdf = true; return; }
            if (unprefixed && XmlAscii.EqualsCaseInsensitive(name, "textinput"u8)) { _parsingTextInput = true; return; }
            if (_parsingTextInput) return;

            _currentAttributes = (_isRdf && name.SequenceEqual("item"u8))
                || name.SequenceEqual("guid"u8) || name.SequenceEqual("enclosure"u8)
                ? attributes.Dictionary() : null;
            if (unprefixed && name.SequenceEqual("item"u8))
            {
                var item = new RssItem();
                _items.Add(item);
                _parsingArticle = true;
                if (_isRdf && _currentAttributes is { } itemAttributes
                    && itemAttributes.TryGetValue("rdf:about", out var about) && about.Length > 0)
                {
                    item.Guid = item.Permalink = about;
                    _rdfAboutItems.Add(item);
                }
            }
            else if (unprefixed && name.SequenceEqual("image"u8)) _parsingChannelImage = true;
            else if (unprefixed && name.SequenceEqual("author"u8) && _parsingArticle) _parsingAuthor = true;

            if (!_parsingChannelImage || unprefixed && name.SequenceEqual("url"u8)) parser.BeginStoringCharacters();
        }

        public void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns)
        {
            if (_endRssFound) return;
            var name = localName.Span;
            var unprefixed = ns.Prefix is null;
            if (_namespaceElementDepth > 0) { _namespaceElementDepth--; return; }
            if ((_isRdf && name.SequenceEqual("RDF"u8)) || unprefixed && name.SequenceEqual("rss"u8)) { _endRssFound = true; return; }
            if (unprefixed && XmlAscii.EqualsCaseInsensitive(name, "textinput"u8)) { _parsingTextInput = false; return; }
            if (_parsingTextInput) return;
            if (_parsingChannelImage && !_parsingArticle && unprefixed && name.SequenceEqual("url"u8))
            {
                _channelImageUrl = parser.CurrentStringWithTrimmedWhitespace;
                return;
            }
            if (unprefixed && name.SequenceEqual("image"u8)) { _parsingChannelImage = false; return; }
            if (unprefixed && name.SequenceEqual("item"u8)) { _parsingArticle = false; return; }
            if (_parsingArticle)
            {
                HandleArticleElementEnd(parser, name, ns);
                if (unprefixed && name.SequenceEqual("author"u8)) _parsingAuthor = false;
            }
            else if (!_parsingChannelImage && unprefixed)
            {
                if (name.SequenceEqual("link"u8) && string.IsNullOrEmpty(_homepageUrl))
                    _homepageUrl = parser.CurrentStringWithTrimmedWhitespace;
                else if (name.SequenceEqual("title"u8)) _title = parser.CurrentStringWithTrimmedWhitespace;
                else if (name.SequenceEqual("language"u8)) _language = parser.CurrentStringWithTrimmedWhitespace;
            }
        }

        private void HandleArticleElementEnd(XmlSaxParser parser, ReadOnlySpan<byte> name, XmlNamespace ns)
        {
            if (CurrentItem is not { } item) return;
            if (ns.Prefix is not null)
            {
                if (ns.IsDublinCore)
                {
                    if (name.SequenceEqual("creator"u8)) AddAuthor(item, parser.CurrentStringWithTrimmedWhitespace);
                    else if (name.SequenceEqual("date"u8) && parser.CurrentCharacters is { } bytes)
                        item.DatePublished = DateParser.Parse(bytes.Span);
                }
                else if (ns.IsContent && name.SequenceEqual("encoded"u8)
                    && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } content) item.Body = content;
                else if (ns.IsSource && name.SequenceEqual("markdown"u8)) item.Markdown = parser.CurrentStringWithTrimmedWhitespace;
                return;
            }
            if (name.SequenceEqual("guid"u8))
            {
                if (parser.CurrentStringWithTrimmedWhitespace is not { } guid) return;
                item.Guid = guid;
                _rdfAboutItems.Remove(item);
                var isPermalink = _currentAttributes?.FirstOrDefault(pair => pair.Key.Equals("ispermalink", StringComparison.OrdinalIgnoreCase)).Value;
                if (!string.Equals(isPermalink, "false", StringComparison.OrdinalIgnoreCase) && IsProbablyUrlOrRelativePath(guid))
                    item.Permalink = ResolveUrl(guid);
            }
            else if (name.SequenceEqual("pubDate"u8))
            {
                if (parser.CurrentCharacters is { } bytes) item.DatePublished = DateParser.Parse(bytes.Span);
            }
            else if (name.SequenceEqual("author"u8)) AddAuthor(item, parser.CurrentStringWithTrimmedWhitespace);
            else if (name.SequenceEqual("link"u8))
            {
                if (item.Link is null && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } link)
                    item.Link = ResolveUrl(link);
            }
            else if (name.SequenceEqual("description"u8)) item.Body ??= parser.CurrentStringWithTrimmedWhitespace;
            else if (name.SequenceEqual("title"u8))
            {
                if (!_parsingAuthor && parser.CurrentStringWithTrimmedWhitespace is { } title) item.Title = title;
            }
            else if (name.SequenceEqual("enclosure"u8)) HandleEnclosure(item);
        }

        private void HandleEnclosure(RssItem item)
        {
            if (_currentAttributes is null || !_currentAttributes.TryGetValue("url", out var url) || url.Length == 0) return;
            long.TryParse(_currentAttributes.GetValueOrDefault("length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);
            var attachment = ParsedAttachment.Create(url, _currentAttributes.GetValueOrDefault("type"), null, length > 0 ? length : null, null);
            if (attachment is not null) item.Attachments.Add(attachment);
        }

        private static void AddAuthor(RssItem item, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var author = text.Contains('@') ? new ParsedAuthor(null, null, null, text)
                : text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new ParsedAuthor(null, text, null, null)
                : new ParsedAuthor(text, null, null, null);
            item.Authors.Add(author);
        }

        private static bool IsProbablyUrlOrRelativePath(string text)
            => !text.Contains(' ') && text.Contains('/') && !text.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);

        private string ResolveUrl(string text)
        {
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return text;
            return Uri.TryCreate(_homepageUrl, UriKind.Absolute, out var baseUrl) && Uri.TryCreate(baseUrl, text, out var resolved)
                ? resolved.AbsoluteUri : text;
        }
    }
}
