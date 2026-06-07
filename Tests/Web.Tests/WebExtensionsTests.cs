using System.Collections.Generic;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.Web.Tests;

/// <summary>
/// Port of NetNewsWire RSWeb's <c>DictionaryTests.swift</c> and
/// <c>StringTests.swift</c>. These cover the query-string and HTML escape
/// helpers used by every remote-account API caller.
/// </summary>
public class WebExtensionsTests
{
    [Fact]
    public void SimpleQueryString()
    {
        var d = new Dictionary<string, string>
        {
            ["foo"] = "bar",
            ["param1"] = "This is a value.",
        };
        var s = d.UrlQueryString();
        Assert.True(s == "foo=bar&param1=This%20is%20a%20value."
                 || s == "param1=This%20is%20a%20value.&foo=bar",
                 $"Unexpected query string: '{s}'");
    }

    [Fact]
    public void QueryStringWithAmpersand()
    {
        var d = new Dictionary<string, string>
        {
            ["fo&o"] = "bar",
            ["param1"] = "This is a&value.",
        };
        var s = d.UrlQueryString();
        Assert.True(s == "fo%26o=bar&param1=This%20is%20a%26value."
                 || s == "param1=This%20is%20a%26value.&fo%26o=bar",
                 $"Unexpected query string: '{s}'");
    }

    [Fact]
    public void QueryStringWithAccentedCharacters()
    {
        var d = new Dictionary<string, string> { ["fée"] = "bør" };
        Assert.Equal("f%C3%A9e=b%C3%B8r", d.UrlQueryString());
    }

    [Fact]
    public void QueryStringWithEmoji()
    {
        var d = new Dictionary<string, string> { ["🌴e"] = "bar🎩🌴" };
        Assert.Equal("%F0%9F%8C%B4e=bar%F0%9F%8E%A9%F0%9F%8C%B4", d.UrlQueryString());
    }

    [Fact]
    public void HtmlEscaping()
    {
        var s = "<foo>\"bar\"&'baz'".EscapedHtml();
        Assert.Equal("&lt;foo&gt;&quot;bar&quot;&amp;&apos;baz&apos;", s);
    }
}
