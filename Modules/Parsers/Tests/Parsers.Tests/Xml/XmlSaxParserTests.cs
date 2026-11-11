using System.Text;
using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlSaxParserTests
{
    [Fact] public void SingleElement()
        => AssertEvents(Parse("<root/>"), Start("root"), End("root"), Done);
    [Fact] public void ElementWithContent()
        => AssertEvents(Parse("<root>hello</root>"), Start("root"), Characters("hello"), End("root"), Done);
    [Fact] public void NestedElements()
        => AssertEvents(Parse("<a><b>x</b><c/></a>"), Start("a"), Start("b"), Characters("x"), End("b"), Start("c"), End("c"), End("a"), Done);
    [Fact] public void Attributes()
        => AssertEvents(Parse("<a href=\"http://example.com\" title='X'/>"),
            Start("a", attributes: new() { ["href"] = "http://example.com", ["title"] = "X" }), End("a"), Done);
    [Fact] public void NamespacesResolveUri()
    {
        var events = Parse("<a xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>foo</dc:title></a>");
        Assert.Equal(6, events.Count);
        AssertStart(events[1], "title", "dc", XmlNamespace.Uris.DublinCore);
    }
    [Fact] public void DefaultNamespace()
    {
        var events = Parse("<root xmlns=\"http://example.com/ns\"><child/></root>");
        AssertStart(events[1], "child", null, "http://example.com/ns");
    }
    [Fact] public void BuiltInXmlPrefix()
    {
        var events = Parse("<a xml:lang=\"en\"><b xml:base=\"http://x.com\"/></a>");
        Assert.Equal("start", events[0].Kind);
        Assert.Equal("en", events[0].Attributes!["xml:lang"]);
    }
    [Fact] public void NamespacePrefixInheritedFromOuterScope()
    {
        var events = Parse("<a xmlns:x=\"http://x.com\"><b><x:tag>ok</x:tag></b></a>");
        var tag = Assert.Single(events, e => e.Kind == "start" && e.Name == "tag");
        Assert.Equal("x", tag.Prefix);
        Assert.Equal("http://x.com", tag.Uri);
    }
    [Fact] public void XmlnsAttributeNotExposed()
    {
        var attributes = Parse("<a xmlns=\"foo\" href=\"http://x\"/>")[0].Attributes!;
        Assert.Equal(new Dictionary<string, string> { ["href"] = "http://x" }, attributes);
    }
    [Fact] public void XmlFiveEntitiesInContent() => Assert.Equal(Characters("&<>\"'"), Parse("<a>&amp;&lt;&gt;&quot;&apos;</a>")[1]);
    [Fact] public void NumericDecimalEntity() => Assert.Equal(Characters("\u2014"), Parse("<a>&#8212;</a>")[1]);
    [Fact] public void NumericHexEntity() => Assert.Equal(Characters("\u2014"), Parse("<a>&#x2014;</a>")[1]);
    [Fact] public void NumericHexEntityUppercaseX() => Assert.Equal(Characters("\u2014"), Parse("<a>&#X2014;</a>")[1]);
    [Fact] public void HtmlNamedEntitiesPassThroughLiterally() => Assert.Equal(Characters("&nbsp;&copy;&mdash;"), Parse("<a>&nbsp;&copy;&mdash;</a>")[1]);
    [Fact] public void UnknownEntityPassesThroughLiterally() => Assert.Equal(Characters("&foo;&bar"), Parse("<a>&foo;&bar</a>")[1]);
    [Fact] public void EntitiesInAttributes() => Assert.Equal("x&y", Parse("<a href=\"x&amp;y\"/>")[0].Attributes!["href"]);
    [Fact] public void CDataPassesThroughVerbatim() => Assert.Equal(Characters("&amp;<b>&nbsp;</b>"), Parse("<a><![CDATA[&amp;<b>&nbsp;</b>]]></a>")[1]);
    [Fact] public void CDataWithClosingBrackets() => Assert.Equal(Characters("array[0] and [1]]=="), Parse("<a><![CDATA[array[0] and [1]]==]]></a>")[1]);
    [Fact] public void CDataWithNumericEntityLeavesLiteral() => Assert.Equal(Characters("&#8212;"), Parse("<a><![CDATA[&#8212;]]></a>")[1]);
    [Fact] public void UnescapedAmpersand() => Assert.Equal(Characters("Fish & Chips"), Parse("<a>Fish & Chips</a>")[1]);
    [Fact] public void StrayLessThan()
    {
        var content = string.Concat(Parse("<a>a < b</a>").Where(e => e.Kind == "characters").Select(e => e.Name));
        Assert.Contains("<", content);
        Assert.Contains("a ", content);
        Assert.Contains("b", content);
    }
    [Fact] public void UnclosedTagImpliesEndAtEof()
    {
        var events = Parse("<a><b>hello");
        Assert.Contains(events, e => e.Kind == "end" && e.Name == "a");
        Assert.Contains(events, e => e.Kind == "end" && e.Name == "b");
    }
    [Fact] public void MismatchedEndTag() => Assert.True(Parse("<a><b></a></b>").Count(e => e.Kind == "end") >= 2);
    [Fact] public void CommentIsIgnored()
        => Assert.Equal(new[] { "a", "b" }, Parse("<a><!-- ignore me --><b/></a>").Where(e => e.Kind == "start").Select(e => e.Name));
    [Fact] public void ProcessingInstructionIsIgnored()
        => Assert.Equal(new[] { "a", "b" }, Parse("<a><?target data?><b/></a>").Where(e => e.Kind == "start").Select(e => e.Name));
    [Fact] public void DocTypeIsIgnored()
        => Assert.Single(Parse("<!DOCTYPE html [\n<!ENTITY foo \"bar\">\n]>\n<a/>"), e => e.Kind == "start");
    [Fact] public void XmlDeclarationIsIgnored()
        => Assert.Single(Parse("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a/>"), e => e.Kind == "start");
    [Fact] public void Utf8Bom()
        => Assert.Equal(Characters("hello"), ParseBytes([0xEF, 0xBB, 0xBF, .. "<a>hello</a>"u8])[1]);
    [Fact] public void Utf16LeBom()
        => Assert.Equal(Characters("hello"), ParseBytes([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<a>hello</a>")])[1]);
    [Fact] public void Utf16BeBom()
        => Assert.Equal(Characters("hello"), ParseBytes([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("<a>hello</a>")])[1]);
    [Fact] public void Latin1Declaration()
        => Assert.Equal(Characters("caf\u00E9"), ParseBytes([.. "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><a>caf"u8, 0xE9, .. "</a>"u8])[1]);
    [Fact] public void Windows1252Declaration()
        => Assert.Equal(Characters("\u201CHi\u201D"), ParseBytes([.. "<?xml version=\"1.0\" encoding=\"windows-1252\"?><a>"u8, 0x93, .. "Hi"u8, 0x94, .. "</a>"u8])[1]);
    [Fact] public void BeginStoringCharacters()
    {
        var receiver = new TestDelegate();
        receiver.OnStart = (parser, name, _, _) => { if (name == "title") parser.BeginStoringCharacters(); };
        receiver.OnEnd = (parser, name, _) => { if (name == "title") receiver.Accumulated = parser.CurrentStringWithTrimmedWhitespace; };
        new XmlSaxParser(receiver).Parse("<root><title>  Hello  </title></root>"u8.ToArray());
        Assert.Equal("Hello", receiver.Accumulated);
    }
    [Fact] public void CaptureRawInnerContent()
    {
        var receiver = new TestDelegate
        {
            OnStart = (parser, name, _, attributes) =>
            {
                if (name == "content" && attributes["type"] == "xhtml") parser.CaptureRawInnerContent();
            }
        };
        new XmlSaxParser(receiver).Parse("<root><content type=\"xhtml\"><div>hello <em>world</em>!</div></content></root>"u8.ToArray());
        Assert.Equal(new[] { "root", "content" }, receiver.Events.Where(e => e.Kind == "start").Select(e => e.Name));
        Assert.Equal("<div>hello <em>world</em>!</div>", receiver.Captured);
    }

    [Theory]
    [InlineData(" \t\r\nvalue \r\n", "value")]
    [InlineData("\u00a0 value \u2003", "value")]
    [InlineData(" \u2003\t\r\n", null)]
    [InlineData("\ufeffvalue", "\ufeffvalue")]
    public void StoredStringsPreserveUnicodeWhitespaceSemantics(string text, string? expected)
    {
        var receiver = new TestDelegate
        {
            OnStart = (parser, _, _, _) => parser.BeginStoringCharacters()
        };
        receiver.OnEnd = (parser, _, _) => receiver.Accumulated = parser.CurrentStringWithTrimmedWhitespace;
        new XmlSaxParser(receiver).Parse(Encoding.UTF8.GetBytes("<a><![CDATA[" + text + "]]></a>"));
        Assert.Equal(expected, receiver.Accumulated);
    }

    [Fact]
    public void ScanningAttributeFreeElementsDoesNotAllocatePerToken()
    {
        const int count = 10_000;
        var bytes = Encoding.UTF8.GetBytes("<root>" + string.Concat(Enumerable.Repeat("<item>text</item>", count)) + "</root>");
        var receiver = new CountingDelegate();
        var parser = new XmlSaxParser(receiver);
        parser.Parse(bytes);
        receiver.Starts = 0;
        receiver.Ends = 0;
        receiver.Characters = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        parser.Parse(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count + 1, receiver.Starts);
        Assert.Equal(count + 1, receiver.Ends);
        Assert.Equal(count, receiver.Characters);
        Assert.True(allocated < 64 * 1024, $"Token scanning allocated {allocated} bytes.");
    }

    private sealed class CountingDelegate : IXmlSaxParserDelegate
    {
        internal int Starts, Ends, Characters;
        public void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> name, XmlNamespace ns, XmlAttributes attributes) => Starts++;
        public void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> name, XmlNamespace ns) => Ends++;
        public void DidFindCharacters(XmlSaxParser parser, ReadOnlyMemory<byte> bytes) => Characters++;
    }

    [Fact]
    public void LargeStoredBodyDoesNotAllocateAnotherStringToTrimAsciiWhitespace()
    {
        const int length = 1024 * 1024;
        var text = new string('x', length);
        long allocated = 0;
        var receiver = new TestDelegate
        {
            OnStart = (parser, _, _, _) => parser.BeginStoringCharacters()
        };
        receiver.OnEnd = (parser, _, _) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            receiver.Accumulated = parser.CurrentStringWithTrimmedWhitespace;
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        };
        new XmlSaxParser(receiver).Parse(Encoding.UTF8.GetBytes("<a><![CDATA[ \r\n" + text + "\r\n ]]></a>"));
        Assert.Equal(text, receiver.Accumulated);
        Assert.InRange(allocated, length * 2, length * 2 + 4096);
    }

    [Fact] public void CaptureRawInnerContentWithNestedSameName()
    {
        var receiver = new TestDelegate();
        receiver.OnStart = (parser, name, _, _) => { if (name == "a" && receiver.Captured is null) parser.CaptureRawInnerContent(); };
        new XmlSaxParser(receiver).Parse("<root><a><a>x</a>y<a>z</a></a></root>"u8.ToArray());
        Assert.Equal("<a>x</a>y<a>z</a>", receiver.Captured);
    }
    [Fact] public void CaptureRawInnerContentPreservesRawEntities()
        => Assert.Equal("a &amp; b &lt; c", Capture("<root><body>a &amp; b &lt; c</body></root>"));
    [Fact] public void CaptureRawInnerContentEmpty()
        => Assert.Equal("", Capture("<root><body></body></root>"));
    [Fact] public void NamespaceConvenienceIsDublinCore()
    {
        var sawDublinCore = false;
        var receiver = new TestDelegate
        {
            OnStart = (_, name, ns, _) => { if (name == "creator") sawDublinCore = ns.IsDublinCore; }
        };
        new XmlSaxParser(receiver).Parse("<rss xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator>Ana</dc:creator></rss>"u8.ToArray());
        Assert.True(sawDublinCore);
    }
    [Fact] public void NamespaceConvenienceIsAtom()
    {
        var atomCount = 0;
        var receiver = new TestDelegate { OnStart = (_, _, ns, _) => { if (ns.IsAtom) atomCount++; } };
        new XmlSaxParser(receiver).Parse("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry/></feed>"u8.ToArray());
        Assert.Equal(2, atomCount);
    }
    [Fact] public void SelfClosingFiresEndEvent() => Assert.Single(Parse("<a/>"), e => e.Kind == "end");

    [Fact] public void RawCaptureRetainsTrailingNonEmittingMarkup()
        => Assert.Equal("text<!-- comment --><?target data?>", Capture("<root><body>text<!-- comment --><?target data?></body></root>"));
    [Fact] public void UnprefixedAttributesDoNotInheritDefaultNamespace()
    {
        var receiver = new TestDelegate
        {
            OnStart = (_, _, _, attributes) => attributes.ForEach((ns, _, _) => Assert.Equal(new XmlNamespace(null, null), ns))
        };
        new XmlSaxParser(receiver).Parse("<root xmlns='urn:default' href='/'/>"u8.ToArray());
    }
    [Fact] public void RawCaptureAtEofSynthesizesOuterEnd()
        => Assert.Equal("<p>unfinished", Capture("<root><body><p>unfinished"));
    [Fact] public void DelegateFailurePropagatesAndParserCanBeReused()
    {
        var receiver = new TestDelegate { OnStart = (_, _, _, _) => throw new InvalidOperationException("callback failed") };
        var parser = new XmlSaxParser(receiver);
        Assert.Throws<InvalidOperationException>(() => parser.Parse("<a xmlns='urn:old'/>"u8.ToArray()));
        receiver.OnStart = null;
        receiver.Events.Clear();
        parser.Parse("<b/>"u8.ToArray());
        AssertEvents(receiver.Events, Start("b"), End("b"), Done);
    }

    [Fact] public void StoredCharacterSnapshotSurvivesSubsequentElements()
    {
        ReadOnlyMemory<byte>? snapshot = null;
        var receiver = new TestDelegate
        {
            OnStart = (parser, _, _, _) => parser.BeginStoringCharacters(),
            OnEnd = (parser, name, _) => { if (name == "a") snapshot = parser.CurrentCharacters; }
        };
        new XmlSaxParser(receiver).Parse("<root><a>first</a><b>second</b></root>"u8.ToArray());
        Assert.True(snapshot.HasValue);
        Assert.Equal("first", Encoding.UTF8.GetString(snapshot.Value.Span));
    }

    private static string? Capture(string input)
    {
        var receiver = new TestDelegate
        {
            OnStart = (parser, name, _, _) => { if (name == "body") parser.CaptureRawInnerContent(); }
        };
        new XmlSaxParser(receiver).Parse(Encoding.UTF8.GetBytes(input));
        return receiver.Captured;
    }
    private static List<Event> Parse(string xml) => ParseBytes(Encoding.UTF8.GetBytes(xml));
    private static List<Event> ParseBytes(byte[] bytes)
    {
        var receiver = new TestDelegate();
        new XmlSaxParser(receiver).Parse(bytes);
        return receiver.Events;
    }
    private sealed record Event(string Kind, string? Name = null, string? Prefix = null, string? Uri = null, Dictionary<string, string>? Attributes = null);
    private static Event Start(string name, string? prefix = null, string? uri = null, Dictionary<string, string>? attributes = null)
        => new("start", name, prefix, uri, attributes ?? []);
    private static Event End(string name, string? prefix = null, string? uri = null) => new("end", name, prefix, uri);
    private static Event Characters(string value) => new("characters", value);
    private static readonly Event Done = new("done");
    private static void AssertStart(Event actual, string name, string? prefix, string? uri)
    {
        Assert.Equal("start", actual.Kind);
        Assert.Equal(name, actual.Name);
        Assert.Equal(prefix, actual.Prefix);
        Assert.Equal(uri, actual.Uri);
    }
    private static void AssertEvents(List<Event> actual, params Event[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].Prefix, actual[i].Prefix);
            Assert.Equal(expected[i].Uri, actual[i].Uri);
            Assert.Equal(expected[i].Attributes, actual[i].Attributes);
        }
    }
    private sealed class TestDelegate : IXmlSaxParserDelegate
    {
        public List<Event> Events { get; } = [];
        public Action<XmlSaxParser, string, XmlNamespace, XmlAttributes>? OnStart { get; set; }
        public Action<XmlSaxParser, string, XmlNamespace>? OnEnd { get; set; }
        public string? Accumulated { get; set; }
        public string? Captured { get; private set; }
        public void DidStartElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns, XmlAttributes attributes)
        {
            var name = Encoding.UTF8.GetString(localName.Span);
            Events.Add(Start(name, ns.Prefix, ns.Uri, attributes.Dictionary()));
            OnStart?.Invoke(parser, name, ns, attributes);
        }
        public void DidEndElement(XmlSaxParser parser, ReadOnlyMemory<byte> localName, XmlNamespace ns)
        {
            var name = Encoding.UTF8.GetString(localName.Span);
            Events.Add(End(name, ns.Prefix, ns.Uri));
            OnEnd?.Invoke(parser, name, ns);
        }
        public void DidFindCharacters(XmlSaxParser parser, ReadOnlyMemory<byte> bytes)
        {
            var text = Encoding.UTF8.GetString(bytes.Span);
            if (Events.Count > 0 && Events[^1].Kind == "characters")
                Events[^1] = Characters(Events[^1].Name + text);
            else Events.Add(Characters(text));
        }
        public void DidCaptureRawInnerContent(XmlSaxParser parser, ReadOnlyMemory<byte> bytes, ReadOnlyMemory<byte> localName, XmlNamespace ns)
            => Captured = Encoding.UTF8.GetString(bytes.Span);
        public void DidEnd(XmlSaxParser parser) => Events.Add(Done);
    }
}
