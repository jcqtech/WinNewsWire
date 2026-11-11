using System.Globalization;
using WinNewsWire.AppShared.Extensions;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class SimpleHtmlTests
{
    [Fact] public void PlainText() => Assert.Equal("Hello World", SimpleHtml.Parse("Hello World").Text);
    [Fact] public void BoldTag() => Assert.Equal("Use bold here", SimpleHtml.Parse("Use <b>bold</b> here").Text);
    [Fact] public void ItalicTag() => Assert.Equal("Use italic here", SimpleHtml.Parse("Use <i>italic</i> here").Text);
    [Fact] public void MultipleTags() => Assert.Equal("Bold and italic together", SimpleHtml.Parse("<b>Bold</b> and <i>italic</i> together").Text);
    [Fact] public void AmpersandEntity() => Assert.Equal("Tom & Jerry", SimpleHtml.Parse("Tom &amp; Jerry").Text);
    [Fact] public void MixedEntitiesAndTags() => Assert.Equal("Tom & Jerry", SimpleHtml.Parse("<b>Tom</b> &amp; <i>Jerry</i>").Text);
    [Fact] public void QTagProducesQuotes()
    {
        var text = SimpleHtml.Parse("<q>hi</q>", CultureInfo.GetCultureInfo("en-US")).Text;
        Assert.Contains("hi", text);
        Assert.True(new StringInfo(text).LengthInTextElements > 2);
    }
    [Fact] public void BoldFontIsApplied()
        => Assert.True(Assert.Single(SimpleHtml.Parse("<b>Bold</b>").Runs).Style.HasFlag(InlineTextStyle.Bold));
    [Fact] public void UnderlineIsApplied()
        => Assert.True(Assert.Single(SimpleHtml.Parse("<u>Underlined</u>").Runs).Style.HasFlag(InlineTextStyle.Underline));
    [Fact] public void UnicodeText() => Assert.Equal("Caf\u00E9 r\u00E9sum\u00E9 \U0001F389", SimpleHtml.Parse("Caf\u00E9 r\u00E9sum\u00E9 \U0001F389").Text);
    [Fact] public void UnicodeInsideTag() => Assert.Equal("Caf\u00E9 r\u00E9sum\u00E9", SimpleHtml.Parse("<b>Caf\u00E9 r\u00E9sum\u00E9</b>").Text);
    [Fact] public void AbbrTitleRendersAsPlainText()
    {
        var sanitized = ArticleStringFormatter.SanitizedTitle("[<abbr title=\"Not Safe For Work\">NSFW</abbr>]", forHtml: true)!;
        Assert.Equal("[NSFW]", SimpleHtml.Parse(sanitized).Text);
    }
    [Fact] public void LiteralLessThanTitleIsNotTruncated()
    {
        const string input = "No place in children's hands: <16s in UK to be banned";
        Assert.Equal(input, SimpleHtml.Parse(ArticleStringFormatter.SanitizedTitle(input, forHtml: true)!).Text);
    }
    [Fact] public void NestedStylesRetainTheirOuterCounts()
    {
        var text = SimpleHtml.Parse("<b>one <strong>two</strong> three</b> plain");
        Assert.Equal(new AttributedTextRun("one two three", InlineTextStyle.Bold), text.Runs[0]);
        Assert.Equal(new AttributedTextRun(" plain", InlineTextStyle.None), text.Runs[1]);
    }
    [Fact] public void CombinedTraitsAndDecorationsArePreserved()
    {
        var style = Assert.Single(SimpleHtml.Parse("<b><i><u><del><code><sup>x</sup></code></del></u></i></b>").Runs).Style;
        Assert.Equal(InlineTextStyle.Bold | InlineTextStyle.Italic | InlineTextStyle.Underline
            | InlineTextStyle.Strikethrough | InlineTextStyle.Monospace | InlineTextStyle.Superscript, style);
    }
    [Fact] public void QuotesAlternateWhenNested()
        => Assert.Equal("\u201Couter \u2018inner\u2019\u201D", SimpleHtml.Parse("<q>outer <q>inner</q></q>", CultureInfo.GetCultureInfo("en-US")).Text);
}
