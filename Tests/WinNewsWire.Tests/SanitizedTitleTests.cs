using WinNewsWire.AppShared.Extensions;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>Every input/assertion from Tests/NetNewsWireTests/SanitizedTitleTests.swift.</summary>
public class SanitizedTitleTests
{
    [Theory]
    [InlineData("plainTextPreserved", true, "Hello World", "Hello World")]
    [InlineData("plainTextPreserved", false, "Hello World", "Hello World")]
    [InlineData("emptyString", true, "", "")]
    [InlineData("emptyString", false, "", "")]
    [InlineData("nilTitleReturnsNil", true, null, null)]
    [InlineData("nilTitleReturnsNil", false, null, null)]
    [InlineData("angleBracketedTextForHTMLIsEscaped", true, "<hello-world>", "&lt;hello-world&gt;")]
    [InlineData("angleBracketedTextNotForHTMLIsPreserved", false, "<hello-world>", "<hello-world>")]
    [InlineData("htmlLookingTitleWordForHTMLIsEscaped", true, "It All Starts with a Humble <textarea>", "It All Starts with a Humble &lt;textarea&gt;")]
    [InlineData("allowedTagForHTMLIsPreserved", true, "Use <b>bold</b>", "Use <b>bold</b>")]
    [InlineData("allowedTagNotForHTMLIsStripped", false, "Use <b>bold</b>", "Use bold")]
    [InlineData("multipleAllowedTagsForHTML", true, "<b>Bold</b> and <i>italic</i>", "<b>Bold</b> and <i>italic</i>")]
    [InlineData("multipleAllowedTagsNotForHTML", false, "<b>Bold</b> and <i>italic</i>", "Bold and italic")]
    [InlineData("disallowedTagForHTMLIsEscaped", true, "Use <script>evil</script>", "Use &lt;script&gt;evil&lt;/script&gt;")]
    [InlineData("disallowedTagNotForHTMLIsPreservedLiterally", false, "Use <script>evil</script>", "Use <script>evil</script>")]
    [InlineData("abbrWithAttributeForHTML", true, "[<abbr title=\"Not Safe For Work\">NSFW</abbr>]", "[<abbr title=\"Not Safe For Work\">NSFW</abbr>]")]
    [InlineData("abbrWithAttributeNotForHTML", false, "[<abbr title=\"Not Safe For Work\">NSFW</abbr>]", "[NSFW]")]
    [InlineData("allowedTagWithAttributeForHTML", true, "<cite id=\"x\">Book</cite>", "<cite id=\"x\">Book</cite>")]
    [InlineData("allowedTagWithAttributeNotForHTML", false, "<cite id=\"x\">Book</cite>", "Book")]
    [InlineData("selfClosingAllowedTag", true, "X<b/>Y", "X<b/>Y")]
    [InlineData("selfClosingAllowedTag", false, "X<b/>Y", "XY")]
    [InlineData("selfClosingDisallowedTag", true, "X<br/>Y", "X&lt;br/&gt;Y")]
    [InlineData("selfClosingDisallowedTag", false, "X<br/>Y", "X<br/>Y")]
    [InlineData("unclosedTagNotForHTML", false, "foo <bar", "foo <bar")]
    [InlineData("unclosedTagForHTML", true, "foo <bar", "foo &lt;bar")]
    [InlineData("literalLessThanInTitleIsNotTruncated", true, "No place in children's hands: <16s in UK to be banned", "No place in children's hands: &lt;16s in UK to be banned")]
    [InlineData("literalLessThanInTitleIsNotTruncated", false, "No place in children's hands: <16s in UK to be banned", "No place in children's hands: <16s in UK to be banned")]
    [InlineData("bareLessThanAtEnd", true, "foo <", "foo ")]
    [InlineData("bareLessThanAtEnd", false, "foo <", "foo ")]
    [InlineData("unicodeContentPreserved", true, "Caf\u00e9 r\u00e9sum\u00e9 with emoji \U0001f389", "Caf\u00e9 r\u00e9sum\u00e9 with emoji \U0001f389")]
    [InlineData("unicodeContentPreserved", false, "Caf\u00e9 r\u00e9sum\u00e9 with emoji \U0001f389", "Caf\u00e9 r\u00e9sum\u00e9 with emoji \U0001f389")]
    [InlineData("unicodeInsideAllowedTagForHTML", true, "<b>Caf\u00e9</b>", "<b>Caf\u00e9</b>")]
    [InlineData("unicodeInsideAllowedTagNotForHTML", false, "<b>Caf\u00e9</b>", "Caf\u00e9")]
    public void MatchesUpstream(string sourceCase, bool forHtml, string? title, string? expected)
    {
        Assert.NotEmpty(sourceCase);
        Assert.Equal(expected, ArticleStringFormatter.SanitizedTitle(title, forHtml));
    }
}
