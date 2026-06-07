using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSAtomParser</c> — Atom 1.0 (RFC 4287) via <see cref="XmlReader"/>.</summary>
public static class AtomParser
{
    private const string DaringFireballPrefix = "https://daringfireball.net/";

    public static ParsedFeed? Parse(ParserData parserData)
    {
        using var stream = new MemoryStream(parserData.Data);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
        };
        using var reader = XmlReader.Create(stream, settings);

        string feedUrl = parserData.Url;
        Uri? feedBase = Uri.TryCreate(feedUrl, UriKind.Absolute, out var fb) ? fb : null;
        string? title = null;
        string? homepageUrl = null;
        string? nextUrl = null;
        string? hubUrl = null;
        string? feedLanguage = null;
        string? iconUrl = null;
        ParsedAuthor? rootAuthor = null;
        var feedAuthors = new HashSet<ParsedAuthor>();
        var items = new List<ParsedItem>();
        bool isDaringFireball = false;

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var local = reader.LocalName;
                if (local == "feed")
                {
                    if (!string.IsNullOrEmpty(reader.XmlLang)) feedLanguage = reader.XmlLang;
                    continue;
                }

                if (local == "title" && title is null) { title = ReadInnerText(reader); continue; }
                if (local == "subtitle") { Skip(reader); continue; }
                if (local == "icon") { iconUrl = Resolve(ReadInnerText(reader), feedBase); continue; }
                if (local == "logo") { iconUrl ??= Resolve(ReadInnerText(reader), feedBase); continue; }
                if (local == "link")
                {
                    var rel = reader.GetAttribute("rel");
                    var href = Resolve(reader.GetAttribute("href"), feedBase);
                    if (!string.IsNullOrEmpty(href))
                    {
                        if (rel is null or "alternate")
                        {
                            homepageUrl ??= href;
                            if (href.StartsWith(DaringFireballPrefix, StringComparison.OrdinalIgnoreCase))
                                isDaringFireball = true;
                        }
                        else if (rel == "next") nextUrl ??= href;
                        else if (rel == "hub") hubUrl ??= href;
                    }
                    Skip(reader);
                    continue;
                }
                if (local == "author")
                {
                    var a = ReadAuthor(reader, feedBase);
                    if (a is not null)
                    {
                        feedAuthors.Add(a);
                        rootAuthor ??= a;
                    }
                    continue;
                }
                if (local == "entry")
                {
                    var p = ReadEntry(reader, feedUrl, feedBase, feedLanguage, rootAuthor, isDaringFireball);
                    if (p is not null) items.Add(p);
                    continue;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FeedParserException(FeedParserErrorKind.InvalidXml, ex.Message, ex);
        }

        HashSet<ParsedHub>? hubs = null;
        if (hubUrl is not null) hubs = new HashSet<ParsedHub> { new ParsedHub("WebSub", hubUrl) };

        return new ParsedFeed(FeedType.Atom, title, homepageUrl, feedUrl, feedLanguage,
            FeedDescription: null, NextUrl: nextUrl, IconUrl: iconUrl, FaviconUrl: null,
            Authors: feedAuthors.Count == 0 ? null : feedAuthors, Expired: false,
            Hubs: hubs, Items: new HashSet<ParsedItem>(items));
    }

    private static ParsedItem? ReadEntry(XmlReader reader, string feedUrl, Uri? feedBase,
        string? feedLanguage, ParsedAuthor? rootAuthor, bool isDaringFireball)
    {
        if (reader.IsEmptyElement) return null;

        string? id = null, title = null, summary = null, contentHtml = null, contentText = null;
        string? url = null, externalUrl = null;
        string? imageUrl = null;
        string? language = reader.XmlLang.NilIfEmptyOrWhitespace();
        DateTime? datePublished = null, dateModified = null;
        var authors = new HashSet<ParsedAuthor>();
        var tags = new HashSet<string>();
        var atts = new HashSet<ParsedAttachment>();
        var attUrls = new HashSet<string>();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "entry") break;
            if (reader.NodeType != XmlNodeType.Element) continue;
            // Inherit xml:lang from any descendant scope (DF sets it on <content> only).
            language ??= reader.XmlLang.NilIfEmptyOrWhitespace();
            var local = reader.LocalName;
            var ns = reader.NamespaceURI;
            var isAtomNs = string.IsNullOrEmpty(ns) || ns == "http://www.w3.org/2005/Atom";

            // Yahoo Media RSS (mrss) — used by theatlantic.com and many others — lives
            // alongside Atom and supplies enclosures + lead images. We have to handle it
            // BEFORE the switch below, because media:content / media:thumbnail / media:title
            // would otherwise collide with Atom's own <content>/<title> cases.
            if (ns == "http://search.yahoo.com/mrss/")
            {
                if (local == "thumbnail" || local == "content")
                {
                    var mUrl = Resolve(reader.GetAttribute("url"), feedBase);
                    var mType = reader.GetAttribute("type");
                    var medium = reader.GetAttribute("medium");
                    if (!string.IsNullOrEmpty(mUrl))
                    {
                        bool looksLikeImage =
                            medium == "image" ||
                            local == "thumbnail" ||
                            (mType is not null && mType.StartsWith("image", StringComparison.OrdinalIgnoreCase)) ||
                            LooksLikeImageUrl(mUrl);
                        if (looksLikeImage)
                        {
                            imageUrl ??= mUrl;
                        }
                        else if (attUrls.Add(mUrl))
                        {
                            var att = ParsedAttachment.Create(mUrl, mType, null, null, null);
                            if (att is not null) atts.Add(att);
                        }
                    }
                }
                Skip(reader);
                continue;
            }

            if (!isAtomNs)
            {
                // Any other foreign-namespace element (itunes:*, dc:*, etc.) — ignore.
                // Preserves the old behavior for namespaces we don't special-case.
                Skip(reader);
                continue;
            }

            switch (local)
            {
                case "id": id = ReadInnerText(reader); break;
                case "title": title = ReadInnerText(reader); break;
                case "summary": summary = ReadInnerText(reader); break;
                case "content":
                    {
                        var type = reader.GetAttribute("type");
                        var inner = ReadContentBody(reader, type);
                        if (type == "text") contentText = inner;
                        else contentHtml = inner;
                    }
                    break;
                case "published": datePublished = DateParser.Parse(ReadInnerText(reader) ?? ""); break;
                case "updated": dateModified = DateParser.Parse(ReadInnerText(reader) ?? ""); break;
                case "author":
                    {
                        var a = ReadAuthor(reader, feedBase);
                        if (a is not null) authors.Add(a);
                    }
                    break;
                case "category":
                    {
                        var term = reader.GetAttribute("term");
                        if (!string.IsNullOrEmpty(term)) tags.Add(term);
                        Skip(reader);
                    }
                    break;
                case "link":
                    {
                        var rel = reader.GetAttribute("rel");
                        var href = Resolve(reader.GetAttribute("href"), feedBase);
                        var type = reader.GetAttribute("type");
                        var lenStr = reader.GetAttribute("length");
                        long? len = null;
                        if (long.TryParse(lenStr, out var l) && l > 0) len = l;

                        if (!string.IsNullOrEmpty(href))
                        {
                            // Default rel is "alternate" per spec.
                            var effectiveRel = string.IsNullOrEmpty(rel) ? "alternate" : rel;
                            if (effectiveRel == "enclosure")
                            {
                                if (attUrls.Add(href))
                                {
                                    var att = ParsedAttachment.Create(href, type, null, len, null);
                                    if (att is not null) atts.Add(att);
                                }
                            }
                            else if (isDaringFireball && (effectiveRel == "alternate" || effectiveRel == "related"))
                            {
                                // DaringFireball swaps permalink/external: decide by URL prefix.
                                if (href.StartsWith(DaringFireballPrefix, StringComparison.OrdinalIgnoreCase))
                                    url ??= href;
                                else
                                    externalUrl ??= href;
                            }
                            else if (effectiveRel == "alternate")
                            {
                                url ??= href;
                            }
                            else if (effectiveRel == "related")
                            {
                                externalUrl ??= href;
                            }
                        }
                        Skip(reader);
                    }
                    break;
                default:
                    Skip(reader);
                    break;
            }
        }

        if (id is null && url is null && title is null) return null;
        var uniqueId = id ?? url ?? Md5Hash.Hex((title ?? "") + (datePublished?.Ticks.ToString() ?? ""));

        // Inherit root author if the entry has no authors of its own.
        if (authors.Count == 0 && rootAuthor is not null) authors.Add(rootAuthor);

        // Inherit feed-level xml:lang if the entry doesn't declare one.
        language ??= feedLanguage;

        return new ParsedItem(
            syncServiceId: null,
            uniqueId: uniqueId,
            feedUrl: feedUrl,
            url: url,
            externalUrl: externalUrl,
            title: title,
            language: language,
            contentHtml: contentHtml,
            contentText: contentText,
            markdown: null,
            summary: summary,
            imageUrl: imageUrl,
            bannerImageUrl: null,
            datePublished: datePublished,
            dateModified: dateModified,
            authors: authors.Count == 0 ? null : authors,
            tags: tags.Count == 0 ? null : tags,
            attachments: atts.Count == 0 ? null : atts);
    }

    private static ParsedAuthor? ReadAuthor(XmlReader reader, Uri? feedBase)
    {
        if (reader.IsEmptyElement) return null;
        string? name = null, uri = null, email = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "author") break;
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "name": name = ReadInnerText(reader); break;
                case "uri": uri = Resolve(ReadInnerText(reader), feedBase); break;
                case "email": email = ReadInnerText(reader); break;
                default: Skip(reader); break;
            }
        }
        if (name is null && uri is null && email is null) return null;
        return new ParsedAuthor(name, uri, null, email);
    }

    private static string? Resolve(string? href, Uri? baseUri)
    {
        if (string.IsNullOrEmpty(href)) return href;
        if (baseUri is null) return href;
        if (Uri.TryCreate(href, UriKind.Absolute, out _)) return href;
        if (Uri.TryCreate(baseUri, href, out var abs)) return abs.AbsoluteUri;
        return href;
    }

    private static string? ReadContentBody(XmlReader reader, string? type)
    {
        if (reader.IsEmptyElement) return null;
        if (type == "xhtml")
        {
            var sb = new StringBuilder();
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    sb.Append(reader.ReadOuterXml());
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) sb.Append(reader.Value);
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) sb.Append(reader.Value);
            }
            return sb.ToString().Trim().NilIfEmptyOrWhitespace();
        }
        return ReadInnerText(reader);
    }

    private static string? ReadInnerText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        var sb = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                sb.Append(reader.Value);
            else if (reader.NodeType == XmlNodeType.Element)
            {
                // ReadOuterXml() leaves the reader positioned on the node AFTER the inner
                // element, so we have to re-check our terminators before letting the outer
                // loop call Read() again (otherwise we skip past the closing tag and corrupt
                // the enclosing parse).
                sb.Append(reader.ReadOuterXml());
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    sb.Append(reader.Value);
            }
        }
        return sb.ToString().Trim().NilIfEmptyOrWhitespace();
    }

    private static void Skip(XmlReader reader)
    {
        if (reader.IsEmptyElement) return;
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
        }
    }

    private static bool LooksLikeImageUrl(string url)
    {
        int q = url.IndexOf('?');
        var path = q < 0 ? url : url.Substring(0, q);
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);
    }
}
