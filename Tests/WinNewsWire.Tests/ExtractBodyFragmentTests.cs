using WinNewsWire.AppShared.ArticleRendering;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class ExtractBodyFragmentTests
{
    [Fact]
    public void FullDocumentYieldsBodyFragmentOnly()
    {
        const string html = """
            <!doctype html>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head><style>body { background: #FAFAFA; }</style></head>
            <body style="background-color: #FAFAFA;margin: 0;"><p>Newsletter content</p></body>
            </html>
            """;
        Assert.Equal("<p>Newsletter content</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(html));
    }

    [Fact]
    public void UppercaseBodyTag()
        => Assert.Equal("<p>x</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<HTML><BODY BGCOLOR=\"white\"><p>x</p></BODY></HTML>"));

    [Fact]
    public void UnclosedBodyRunsToEnd()
        => Assert.Equal("<p>one</p><p>two</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<html><body class=\"c\"><p>one</p><p>two</p>"));

    [Fact]
    public void DocumentWithoutBodyTagLosesWrapper()
        => Assert.Equal("<p>content</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<!DOCTYPE html><html lang=\"en\"><head><title>t</title></head><p>content</p></html>"));

    [Fact]
    public void DoctypeOnlyPrefixIsRemoved()
        => Assert.Equal("<p>content</p>", ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<!doctype html><p>content</p>"));

    [Fact]
    public void PlainFragmentIsUnchanged()
    {
        const string html = "<p>Just a normal article <b>fragment</b>.</p>";
        Assert.Equal(html, ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(html));
    }

    [Fact]
    public void BodyPrefixedTagIsNotMistakenForBodyTag()
    {
        const string html = "<p>The <bodyguard>stood</bodyguard> watch.</p>";
        Assert.Equal(html, ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(html));
    }

    [Fact]
    public void HeaderElementSurvivesDocumentUnwrapping()
        => Assert.Equal("<header><h1>Title</h1></header><p>content</p>",
            ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded("<html><header><h1>Title</h1></header><p>content</p></html>"));
}
