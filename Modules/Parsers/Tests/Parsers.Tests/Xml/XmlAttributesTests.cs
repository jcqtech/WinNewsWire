using System.Text;
using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlAttributesTests
{
    private static XmlAttributes.Attribute Attribute(string local, string value, string? prefix = null, string? uri = null)
        => new(new(prefix, uri), Encoding.UTF8.GetBytes(local), Encoding.UTF8.GetBytes(value));
    private static XmlAttributes Make(params XmlAttributes.Attribute[] attributes) => new(attributes);

    [Fact] public void EmptySingletonIsEmpty()
    {
        Assert.Equal(0, XmlAttributes.Empty.Count);
        Assert.True(XmlAttributes.Empty.IsEmpty);
    }
    [Fact] public void CountAndIsEmpty()
    {
        var attributes = Make(Attribute("href", "/"));
        Assert.Equal(1, attributes.Count);
        Assert.False(attributes.IsEmpty);
    }
    [Fact] public void AttributeLocalName() => Assert.Equal("href", Attribute("href", "/").LocalName);
    [Fact] public void AttributeValue() => Assert.Equal("https://example.com", Attribute("href", "https://example.com").Value);
    [Fact] public void AttributeQualifiedNameUnprefixed() => Assert.Equal("href", Attribute("href", "/").QualifiedName);
    [Fact] public void AttributeQualifiedNamePrefixed()
        => Assert.Equal("xml:lang", Attribute("lang", "en", "xml", "http://www.w3.org/XML/1998/namespace").QualifiedName);
    [Fact] public void SubscriptFindsUnprefixedAttribute() => Assert.Equal("/foo", Make(Attribute("href", "/foo"))["href"]);
    [Fact] public void SubscriptIsCaseSensitive()
    {
        var attributes = Make(Attribute("href", "/foo"));
        Assert.Null(attributes["HREF"]);
        Assert.Null(attributes["Href"]);
    }
    [Fact] public void SubscriptReturnsNilForMissing() => Assert.Null(Make(Attribute("href", "/foo"))["type"]);
    [Fact] public void SubscriptFindsPrefixedAttributeWithQualifiedName()
        => Assert.Equal("en", Make(Attribute("lang", "en", "xml", "http://www.w3.org/XML/1998/namespace"))["xml:lang"]);
    [Fact] public void SubscriptPrefixedAttributeDoesNotMatchBareLocalName()
        => Assert.Null(Make(Attribute("lang", "en", "xml", "http://www.w3.org/XML/1998/namespace"))["lang"]);
    [Fact] public void SubscriptUnprefixedAttributeDoesNotMatchQualifiedQuery()
        => Assert.Null(Make(Attribute("lang", "en"))["xml:lang"]);
    [Fact] public void SubscriptWrongPrefixFails()
        => Assert.Null(Make(Attribute("lang", "en", "xml", "http://www.w3.org/XML/1998/namespace"))["foo:lang"]);
    [Fact] public void CaseInsensitiveMatchesMixedCaseLocalName()
    {
        var attributes = Make(Attribute("isPermaLink", "false"));
        foreach (var query in new[] { "ispermalink", "ISPERMALINK", "IsPermaLink" })
            Assert.Equal("false", attributes.ValueForCaseInsensitiveName(query));
    }
    [Fact] public void CaseInsensitiveMatchesPrefixAndLocal()
        => Assert.Equal("en", Make(Attribute("Lang", "en", "XML", "http://www.w3.org/XML/1998/namespace")).ValueForCaseInsensitiveName("xml:lang"));
    [Fact] public void CaseInsensitiveDoesNotAffectNonLetters()
    {
        var attributes = Make(Attribute("data-id", "42"));
        Assert.Equal("42", attributes.ValueForCaseInsensitiveName("data-id"));
        Assert.Null(attributes.ValueForCaseInsensitiveName("data_id"));
    }
    [Fact] public void DictionaryBuildsQualifiedKeys()
    {
        var dictionary = Make(Attribute("href", "/foo"), Attribute("lang", "en", "xml", "http://www.w3.org/XML/1998/namespace")).Dictionary();
        Assert.Equal(2, dictionary.Count);
        Assert.Equal("/foo", dictionary["href"]);
        Assert.Equal("en", dictionary["xml:lang"]);
    }
    [Fact] public void DictionaryFromEmptyIsEmpty() => Assert.Empty(XmlAttributes.Empty.Dictionary());
    [Fact] public void DictionaryLastWinsOnDuplicateKeys()
        => Assert.Equal("second", Make(Attribute("x", "first"), Attribute("x", "second")).Dictionary()["x"]);
    [Fact] public void ForEachVisitsAllAttributesInOrder()
    {
        var attributes = Make(Attribute("a", "1"), Attribute("b", "2", "ns", "urn:ns"), Attribute("c", "3"));
        var collected = new List<(string? Prefix, string Local, string Value)>();
        attributes.ForEach((ns, local, value) => collected.Add((ns.Prefix, local, value)));
        Assert.Equal(new (string?, string, string)[] { (null, "a", "1"), ("ns", "b", "2"), (null, "c", "3") }, collected);
    }
    [Fact] public void ForEachOnEmptyIsNoop()
    {
        var count = 0;
        XmlAttributes.Empty.ForEach((_, _, _) => count++);
        Assert.Equal(0, count);
    }
    [Fact] public void ValuesRoundTripUnicode()
    {
        const string value = "Caf\u00E9 \u2014 r\u00E9sum\u00E9 \u65E5\u672C\u8A9E";
        Assert.Equal(value, Make(Attribute("title", value))["title"]);
    }
}
