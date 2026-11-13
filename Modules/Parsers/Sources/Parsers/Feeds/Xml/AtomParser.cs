using System.Globalization;
using System.Text;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

public static class AtomParser
{
    public static ParsedFeed Parse(ParserData parserData)
    {
        var receiver = new AtomDelegate(parserData.Url);
        new XmlSaxParser(receiver).Parse(parserData.Data);
        return receiver.BuildParsedFeed();
    }

    private sealed class AtomDelegate(string feedUrl) : IXmlSaxParserDelegate
    {
        private readonly bool _isDaringFireball = feedUrl.Contains("daringfireball.net/", StringComparison.Ordinal);
        private readonly Uri? _documentUrl = Uri.TryCreate(feedUrl, UriKind.Absolute, out var url) ? url : null;
        private string? _feedTitle, _homepageUrl, _language, _iconUrl, _logoUrl;
        private readonly List<RssItem> _items = [];
        private readonly Dictionary<RssItem, LegacyIdentity> _legacyIdentities = [];
        private bool _legacyHomepageFound, _legacyDaringFireball;
        private ParsedAuthor? _rootAuthor;
        private AtomAuthor? _currentAuthor;
        private bool _parsingArticle, _parsingAuthor, _parsingSource, _endFeedFound;
        private int _namespaceElementDepth;
        private readonly MediaElementTracker _media = new();
        private readonly List<XmlAttributes> _attributesStack = [];
        private readonly List<Uri?> _baseUrlStack = [];
        private RssItem? CurrentArticle => _items.LastOrDefault();
        private XmlAttributes CurrentAttributes => _attributesStack.LastOrDefault() ?? XmlAttributes.Empty;
        private Uri? EffectiveBaseUrl => _baseUrlStack.LastOrDefault();

        public ParsedFeed BuildParsedFeed()
        {
            if (_rootAuthor is { } author)
                foreach (var item in _items.Where(item => item.Authors.Count == 0)) item.Authors.Add(author);
            return new ParsedFeed(FeedType.Atom, _feedTitle, _homepageUrl, feedUrl, _language,
                FeedDescription: null, NextUrl: null, IconUrl: _logoUrl ?? _iconUrl, FaviconUrl: _iconUrl,
                Authors: null, Expired: false, Hubs: null, Items: _items.Select(item =>
                    item.ToParsedItem(feedUrl, LegacyArticleIdentity.Atom(item,
                        _legacyIdentities[item].Permalink, _legacyIdentities[item].Published))).ToHashSet());
        }

        public void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns, XmlAttributes attributes)
        {
            if (_endFeedFound) return;
            _attributesStack.Add(attributes);
            PushBaseUrl(attributes);
            if (_namespaceElementDepth > 0) { _namespaceElementDepth++; return; }
            if (_parsingArticle && ns.IsMediaRss && CurrentArticle is { } mediaItem)
            {
                if (_media.Start(localName.Span, attributes, mediaItem, parser, ResolvedUrlString)) _namespaceElementDepth = 1;
                return;
            }
            if (_media.ContainerDepth > 0) { _namespaceElementDepth = 1; return; }
            if (_parsingArticle && ns.Prefix is not null) { _namespaceElementDepth = 1; return; }

            var name = Encoding.UTF8.GetString(localName.Span);
            if (name == "entry")
            {
                _parsingArticle = true;
                _media.Reset();
                var item = new RssItem();
                _items.Add(item);
                _legacyIdentities.Add(item, new());
                return;
            }
            if (name == "author") { _parsingAuthor = true; _currentAuthor = new(); return; }
            if (name == "source") { _parsingSource = true; return; }
            if (_parsingArticle && name is "content" or "summary")
            {
                if (name == "content" && CurrentArticle is { } article) article.Language = CurrentAttributes["xml:lang"];
                if (CurrentAttributes["type"] == "xhtml") { parser.CaptureRawInnerContent(); return; }
            }
            if (!_parsingArticle && name == "link") { AddHomePageLink(); return; }
            if (name == "feed") _language = CurrentAttributes["xml:lang"];
            parser.BeginStoringCharacters();
        }

        public void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlyMemory<byte> bytes, ReadOnlyMemory<byte> localName, XmlNamespace ns)
        {
            if (CurrentArticle is not { } article) return;
            var html = EffectiveBaseUrl is { } baseUrl ? HtmlRelativeUrlResolver.Resolve(bytes.Span, baseUrl) : Encoding.UTF8.GetString(bytes.Span);
            if (localName.Span.SequenceEqual("content"u8)) article.Body = html;
            else if (localName.Span.SequenceEqual("summary"u8)) article.Summary = html;
        }

        public void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns)
        {
            try
            {
                var name = Encoding.UTF8.GetString(localName.Span);
                if (name == "feed") { _endFeedFound = true; return; }
                if (_endFeedFound) return;
                if (_namespaceElementDepth > 0) { _namespaceElementDepth--; return; }
                if (_parsingArticle && ns.IsMediaRss && CurrentArticle is { } mediaItem)
                {
                    _media.End(localName.Span, mediaItem, parser);
                    return;
                }
                if (_parsingAuthor) { HandleAuthorElementEnd(parser, name); return; }
                if (name == "entry") { _parsingArticle = false; return; }
                if (_parsingArticle && !_parsingSource) { HandleArticleElementEnd(parser, name, ns); return; }
                if (name == "source") { _parsingSource = false; return; }
                if (!_parsingArticle && !_parsingSource) HandleFeedElementEnd(parser, name, ns);
            }
            finally
            {
                if (_attributesStack.Count > 0) _attributesStack.RemoveAt(_attributesStack.Count - 1);
                if (_baseUrlStack.Count > 0) _baseUrlStack.RemoveAt(_baseUrlStack.Count - 1);
            }
        }

        private void HandleArticleElementEnd(XmlSaxParser parser, string name, XmlNamespace ns)
        {
            if (CurrentArticle is not { } article || ns.Prefix is not null) return;
            var text = parser.CurrentStringWithTrimmedWhitespace;
            switch (name)
            {
                case "id": article.Guid = text; break;
                case "title": article.Title = text; break;
                case "content":
                    if (text is not null) article.Body = ResolvedHtmlBody(text);
                    break;
                case "summary":
                    if (text is not null) article.Summary = ResolvedHtmlBody(text);
                    break;
                case "link": HandleArticleLink(article); break;
                case "published":
                    article.DatePublished = DateFromParser(parser);
                    _legacyIdentities[article].Published = article.DatePublished;
                    break;
                case "updated": article.DateModified = DateFromParser(parser); break;
                case "issued": article.DatePublished ??= DateFromParser(parser); break;
                case "modified": article.DateModified ??= DateFromParser(parser); break;
            }
        }

        private void HandleArticleLink(RssItem article)
        {
            var attributes = CurrentAttributes;
            RecordLegacyLink(article, attributes);
            if (attributes["href"] is not { Length: > 0 } raw || ResolvedUrlString(raw) is not { } resolved) return;
            var rel = attributes["rel"] ?? "alternate";
            if (rel == "enclosure")
            {
                long.TryParse(attributes["length"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);
                var attachment = ParsedAttachment.Create(resolved, attributes["type"], attributes["title"], length > 0 ? length : null, null);
                if (attachment is not null) article.Attachments.Add(attachment);
                return;
            }
            if (_isDaringFireball)
            {
                if (resolved.StartsWith("https://daringfireball.net/", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(article.Permalink)) article.Permalink = resolved;
                }
                else if (string.IsNullOrEmpty(article.Link)) article.Link = resolved;
                return;
            }
            if (rel == "related" && string.IsNullOrEmpty(article.Link)) article.Link = resolved;
            if (rel == "alternate" && string.IsNullOrEmpty(article.Permalink)) article.Permalink = resolved;
        }

        private void HandleAuthorElementEnd(XmlSaxParser parser, string name)
        {
            if (name == "author")
            {
                _parsingAuthor = false;
                if (_currentAuthor?.ToParsedAuthor() is { } author)
                {
                    if (_parsingArticle) CurrentArticle?.Authors.Add(author);
                    else _rootAuthor ??= author;
                }
                _currentAuthor = null;
                return;
            }
            if (_currentAuthor is not { } current) return;
            var text = parser.CurrentStringWithTrimmedWhitespace;
            if (name == "name") current.Name = text;
            else if (name == "email") current.EmailAddress = text;
            else if (name == "uri" && text is not null) current.Url = ResolvedUrlString(text) ?? text;
        }

        private void HandleFeedElementEnd(XmlSaxParser parser, string name, XmlNamespace ns)
        {
            if (ns.Prefix is not null) return;
            var text = parser.CurrentStringWithTrimmedWhitespace;
            if (name == "title" && string.IsNullOrEmpty(_feedTitle)) _feedTitle = text;
            else if (name == "icon" && !string.IsNullOrEmpty(text)) _iconUrl = ResolvedUrlString(text) ?? text;
            else if (name == "logo" && _logoUrl is null && !string.IsNullOrEmpty(text)) _logoUrl = ResolvedUrlString(text) ?? text;
        }

        private void AddHomePageLink()
        {
            if (!_legacyHomepageFound && CurrentAttributes["rel"] is null or "alternate" &&
                CurrentAttributes["href"] is { Length: > 0 } legacyHref)
            {
                _legacyHomepageFound = true;
                _legacyDaringFireball = ResolveLegacyUrl(legacyHref).StartsWith("https://daringfireball.net/", StringComparison.OrdinalIgnoreCase);
            }
            if (!string.IsNullOrEmpty(_homepageUrl)) return;
            var attributes = CurrentAttributes;
            if (attributes["href"] is not { Length: > 0 } raw) return;
            if (attributes["rel"] is null or "alternate") _homepageUrl = ResolvedUrlString(raw);
        }

        private static DateTime? DateFromParser(XmlSaxParser parser)
            => parser.CurrentCharacters is { } bytes ? DateParser.Parse(bytes.Span) : null;

        private void PushBaseUrl(XmlAttributes attributes)
        {
            var inherited = EffectiveBaseUrl;
            var xmlBase = attributes["xml:base"];
            if (!string.IsNullOrEmpty(xmlBase) &&
                Uri.TryCreate(inherited ?? _documentUrl, xmlBase, out var resolved) &&
                resolved.Scheme is "http" or "https")
                _baseUrlStack.Add(resolved);
            else _baseUrlStack.Add(inherited);
        }

        private string ResolvedHtmlBody(string html)
            => CurrentAttributes["type"] is "html" or "text/html" && EffectiveBaseUrl is { } baseUrl
                ? HtmlRelativeUrlResolver.Resolve(html, baseUrl) : html;

        private string? ResolvedUrlString(string value)
        {
            if (IsValidUrlString(value)) return value;
            if (EffectiveBaseUrl is { } effective && Uri.TryCreate(effective, value, out var resolved) && IsValidUrlString(resolved.AbsoluteUri))
                return resolved.AbsoluteUri;
            var fallback = string.IsNullOrEmpty(_homepageUrl) ? feedUrl : _homepageUrl;
            return Uri.TryCreate(fallback, UriKind.Absolute, out var baseUrl) &&
                   Uri.TryCreate(baseUrl, value, out resolved) && IsValidUrlString(resolved.AbsoluteUri)
                ? resolved.AbsoluteUri : null;
        }

        private static bool IsValidUrlString(string value)
            => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private void RecordLegacyLink(RssItem article, XmlAttributes attributes)
        {
            if (attributes["href"] is not { Length: > 0 } raw) return;
            var href = ResolveLegacyUrl(raw);
            var rel = string.IsNullOrEmpty(attributes["rel"]) ? "alternate" : attributes["rel"];
            var identity = _legacyIdentities[article];
            if (_legacyDaringFireball && rel is "alternate" or "related")
            {
                if (href.StartsWith("https://daringfireball.net/", StringComparison.OrdinalIgnoreCase)) identity.Permalink ??= href;
            }
            else if (rel == "alternate") identity.Permalink ??= href;
        }

        private string ResolveLegacyUrl(string value)
        {
            if (_documentUrl is null || Uri.TryCreate(value, UriKind.Absolute, out _)) return value;
            return Uri.TryCreate(_documentUrl, value, out var absolute) ? absolute.AbsoluteUri : value;
        }

        private sealed class LegacyIdentity
        {
            public string? Permalink { get; set; }
            public DateTime? Published { get; set; }
        }
    }

    private sealed class AtomAuthor
    {
        public string? Name { get; set; }
        public string? EmailAddress { get; set; }
        public string? Url { get; set; }
        public ParsedAuthor? ToParsedAuthor() => Name is null && EmailAddress is null && Url is null ? null : new(Name, Url, null, EmailAddress);
    }
}
