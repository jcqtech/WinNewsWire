namespace WinNewsWire.Parsers.Xml;

public readonly record struct XmlNamespace(string? Prefix, string? Uri)
{
    public bool IsAtom => Uri == Uris.Atom;
    public bool IsDublinCore => Uri is Uris.DublinCore or Uris.DublinCoreTerms;
    public bool IsContent => Uri == Uris.Content;
    public bool IsXhtml => Uri == Uris.Xhtml;
    public bool IsMediaRss => Uri is Uris.MediaRss or Uris.MediaRssBoard;
    public bool IsITunes => Uri == Uris.ITunes;
    public bool IsSource => Uri == Uris.Source;

    public static class Uris
    {
        public const string Atom = "http://www.w3.org/2005/Atom";
        public const string DublinCore = "http://purl.org/dc/elements/1.1/";
        public const string DublinCoreTerms = "http://purl.org/dc/terms/";
        public const string Content = "http://purl.org/rss/1.0/modules/content/";
        public const string Xhtml = "http://www.w3.org/1999/xhtml";
        public const string MediaRss = "http://search.yahoo.com/mrss/";
        public const string MediaRssBoard = "http://www.rssboard.org/media-rss";
        public const string ITunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        public const string Source = "https://source.scripting.com/";
    }
}
