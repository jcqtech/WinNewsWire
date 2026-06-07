using System;
using System.Collections.Generic;
using WinNewsWire.Articles;
using WinNewsWire.AppShared.Extensions;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>
/// Tests for the article helper extensions ported from
/// <c>Shared/Extensions/ArticleUtilities.swift</c>.
/// </summary>
public class ArticleUtilitiesTests
{
    private static Article Make(
        string? url = null, string? externalUrl = null,
        string? contentHtml = null, string? contentText = null, string? summary = null,
        DateTime? published = null, DateTime? modified = null,
        IReadOnlySet<Author>? authors = null)
    {
        var status = new ArticleStatus("a", false, false, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return new Article(
            accountID: "acct", articleID: "a", feedID: "f", uniqueID: "u",
            title: "title", contentHtml: contentHtml, contentText: contentText, markdown: null,
            url: url, externalURL: externalUrl, summary: summary, imageURL: null,
            datePublished: published, dateModified: modified,
            authors: authors, attachments: null, status: status);
    }

    [Fact]
    public void PreferredLinkPrefersUrl()
    {
        var article = Make(url: "https://example.com/a", externalUrl: "https://other.example/x");
        Assert.Equal("https://example.com/a", article.PreferredLink());
    }

    [Fact]
    public void PreferredLinkFallsBackToExternalUrl()
    {
        var article = Make(externalUrl: "https://other.example/x");
        Assert.Equal("https://other.example/x", article.PreferredLink());
    }

    [Fact]
    public void PreferredLinkNullWhenNoLinks()
    {
        Assert.Null(Make().PreferredLink());
    }

    [Fact]
    public void BodyPrefersHtmlThenTextThenSummary()
    {
        Assert.Equal("<p>HTML</p>", Make(contentHtml: "<p>HTML</p>", contentText: "txt", summary: "s").Body());
        Assert.Equal("txt", Make(contentText: "txt", summary: "s").Body());
        Assert.Equal("s", Make(summary: "s").Body());
        Assert.Null(Make().Body());
    }

    [Fact]
    public void LogicalDatePublishedFallsBackThroughDateChain()
    {
        var arrived = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var modified = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var published = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(published, Make(published: published, modified: modified).LogicalDatePublished());
        Assert.Equal(modified, Make(modified: modified).LogicalDatePublished());
        Assert.Equal(arrived, Make().LogicalDatePublished());
    }

    [Fact]
    public void BylineWithNoAuthorsIsEmpty()
    {
        Assert.Equal(string.Empty, Make().Byline());
    }

    [Fact]
    public void BylineWithSingleAuthorMatchingFeedNameIsEmpty()
    {
        var authors = new HashSet<Author> { Author.Create(null, "John Gruber", null, null, null)! };
        var article = Make(authors: authors);
        Assert.Equal(string.Empty, article.Byline(feedName: "John Gruber"));
    }

    [Fact]
    public void BylineWithSingleAuthor()
    {
        var authors = new HashSet<Author> { Author.Create(null, "Jane", null, null, null)! };
        Assert.Equal("Jane", Make(authors: authors).Byline());
    }

    [Fact]
    public void BylineWithEmail()
    {
        var authors = new HashSet<Author> { Author.Create(null, "Jane", null, null, "jane@example.com")! };
        Assert.Equal("Jane <jane@example.com>", Make(authors: authors).Byline());
    }

    [Fact]
    public void BylineStripsNoReplyAddresses()
    {
        var authors = new HashSet<Author> { Author.Create(null, "Jane", null, null, "noreply@example.com")! };
        Assert.Equal("Jane", Make(authors: authors).Byline());
    }

    [Fact]
    public void BylineMultipleAuthorsAreCommaSeparated()
    {
        var authors = new HashSet<Author>
        {
            Author.Create("a1", "Jane", null, null, null)!,
            Author.Create("a2", "Bob",  null, null, null)!,
        };
        var byline = Make(authors: authors).Byline();
        Assert.Contains("Jane", byline);
        Assert.Contains("Bob", byline);
        Assert.Contains(", ", byline);
    }
}
