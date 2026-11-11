using WinNewsWire.AppShared.Sharing;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class SharingTests
{
    [Fact]
    public void SharingSubject() => Assert.Equal("Immunization", ArticleSharing.Subject([Article("Immunization")]));

    [Fact]
    public void SharingSubjectMultipleArticles()
        => Assert.Equal("NetNewsWire Status: Almost Beta, No Algorithms Follow-Up",
            ArticleSharing.Subject([Article("NetNewsWire Status: Almost Beta"), Article("No Algorithms Follow-Up")]));

    private static Article Article(string title)
    {
        var id = Guid.NewGuid().ToString();
        return new(Guid.NewGuid().ToString(), id, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            title, null, null, null, null, null, null, null, null, null, null, null,
            new ArticleStatus(id, true, DateTime.UtcNow));
    }
}
