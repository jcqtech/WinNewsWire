using System.Text;
using System.Xml.Linq;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.Account.Tests;

public class OpmlRoundTripTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void NestedFoldersAreFlattenedIntoTopmostNamedFolder()
    {
        var account = _manager.CreateAccount();
        var items = Parse("""
            <outline text="Outer">
              <outline text="One" xmlUrl="https://example.com/one"/>
              <outline text="Inner">
                <outline text="Two" xmlUrl="https://example.com/two"/>
                <outline text="Deeper"><outline text="Three" xmlUrl="https://example.com/three"/></outline>
              </outline>
            </outline>
            """);
        account.LoadOpmlItems(items);
        var folder = Assert.Single(account.Folders);
        Assert.Equal("Outer", folder.Name);
        Assert.Equal(3, folder.Feeds.Count);
        Assert.Equal("Inner", items[0].Children[1].Title);
        Assert.Equal(2, items[0].Children[1].Children.Count);
    }

    [Fact]
    public void UnnamedFoldersArePromoted()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Parse("""
            <outline>
              <outline text="Root" xmlUrl="https://example.com/root"/>
              <outline text="Named"><outline><outline text="Child" xmlUrl="https://example.com/child"/></outline></outline>
            </outline>
            """));
        Assert.Equal("Root", Assert.Single(account.TopLevelFeeds).Name);
        var folder = Assert.Single(account.Folders);
        Assert.Equal("Named", folder.Name);
        Assert.Equal("Child", Assert.Single(folder.Feeds).Name);
    }

    [Fact]
    public void EmptyNamedFoldersSurvive()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Parse("""<outline text="Empty"/><outline text="News" type="folder"/>"""));
        Assert.Equal(new[] { "Empty", "News" }, account.Folders.Select(f => f.Name));
        Assert.All(account.Folders, f => Assert.Empty(f.Feeds));
    }

    [Fact]
    public void OneFeedCanBelongToRootAndMultipleFolders()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Parse("""
            <outline text="Feed" xmlUrl="https://example.com/feed"/>
            <outline text="One"><outline text="Feed" xmlUrl="https://example.com/feed"/></outline>
            <outline text="Two"><outline text="Feed" xmlUrl="https://example.com/feed"/></outline>
            """));
        var feed = Assert.Single(account.TopLevelFeeds);
        Assert.Equal(2, account.Folders.Count);
        Assert.All(account.Folders, f => Assert.Same(feed, Assert.Single(f.Feeds)));
        Assert.Same(feed, Assert.Single(account.FlattenedFeeds()));

        var restored = _manager.Reopen(account);
        var restoredFeed = Assert.Single(restored.TopLevelFeeds);
        Assert.All(restored.Folders, f => Assert.Same(restoredFeed, Assert.Single(f.Feeds)));
        Assert.Single(restored.FlattenedFeeds());
    }

    [Fact]
    public void ImportDoesNotMoveExistingSubscriptions()
    {
        var account = _manager.CreateAccount();
        var oldFolder = account.AddFolder("Original");
        var feed = account.AddFeed("https://example.com/feed", "Feed", oldFolder);
        account.LoadOpmlItems(Parse("""
            <outline text="Imported"><outline text="Feed" xmlUrl="https://example.com/feed"/></outline>
            """));
        Assert.Same(feed, Assert.Single(oldFolder.Feeds));
        Assert.Same(feed, Assert.Single(account.Folders.Single(f => f.Name == "Imported").Feeds));
    }

    [Fact]
    public void ImportedNameSurvivesMetadataRefresh()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Parse("""<outline text="My Name" xmlUrl="https://example.com/feed"/>"""));
        var feed = Assert.Single(account.TopLevelFeeds);
        feed.Name = "Publisher Name";
        Assert.Equal("My Name", feed.NameForDisplay);
    }

    [Fact]
    public void ImportLeavesAnExistingEditedNameAlone()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed", "Publisher Name");
        feed.EditedName = "My Choice";
        account.LoadOpmlItems(Parse("""<outline text="Imported Name" xmlUrl="https://example.com/feed"/>"""));
        Assert.Equal("My Choice", Assert.Single(account.TopLevelFeeds).NameForDisplay);
    }

    [Fact]
    public void UrlsWithDifferentPathCasingRemainDistinct()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Parse("""
            <outline text="Upper" xmlUrl="https://example.com/Feed"/>
            <outline text="Lower" xmlUrl="https://example.com/feed"/>
            """));
        Assert.Equal(2, account.TopLevelFeeds.Count);
    }

    [Fact]
    public void DuplicateUrlsInContainerAreDeduplicated()
    {
        var account = _manager.CreateAccount();
        var items = Parse("""
            <outline text="First" xmlUrl="https://example.com/feed"/>
            <outline text="Second" xmlUrl="https://example.com/feed"/>
            """);
        account.LoadOpmlItems(items);
        account.LoadOpmlItems(items);
        Assert.Equal("First", Assert.Single(account.TopLevelFeeds).Name);
    }

    [Fact]
    public void ExportDoesNotInventUntitledName()
    {
        var account = _manager.CreateAccount();
        account.AddFeed("https://example.com/feed");
        var outline = Assert.Single(XDocument.Parse(account.ExportOpml()).Descendants("outline"));
        Assert.Equal("", (string?)outline.Attribute("title"));
        Assert.Equal("", (string?)outline.Attribute("text"));
    }

    [Fact]
    public void ExportIsSortedAndIncludesStandardFeedAttributes()
    {
        var account = _manager.CreateAccount();
        account.AddFeed("https://example.com/b", "B");
        account.AddFeed("https://example.com/a", "A");
        account.AddFolder("Z folder");
        account.AddFolder("A folder");
        var document = XDocument.Parse(account.ExportOpml());
        Assert.Equal("1.1", (string?)document.Root?.Attribute("version"));
        var outlines = document.Root!.Element("body")!.Elements("outline").ToList();
        Assert.Equal(new[] { "A", "B", "A folder", "Z folder" }, outlines.Select(e => (string?)e.Attribute("text")));
        Assert.Equal("", (string?)outlines[0].Attribute("description"));
        Assert.Equal("RSS", (string?)outlines[0].Attribute("version"));
    }

    [Fact]
    public void ExportProducesLegalXmlAndPreservesUnicode()
    {
        var account = _manager.CreateAccount();
        account.Name = "A\0&B <C>";
        account.AddFeed("https://example.com/feed?a=1&b=2", "Caf\u00e9 \U0001f389 \u0001<&\"'>");
        var document = XDocument.Parse(account.ExportOpml());
        Assert.Equal("A&B <C>", document.Root!.Element("head")!.Element("title")!.Value);
        var outline = Assert.Single(document.Descendants("outline"));
        Assert.Equal("Caf\u00e9 \U0001f389 <&\"'>", (string?)outline.Attribute("title"));
        Assert.Equal("https://example.com/feed?a=1&b=2", (string?)outline.Attribute("xmlUrl"));
    }

    [Fact]
    public void OwnFileIsLoadedOnLaunch()
    {
        var account = _manager.CreateAccount();
        account.AddFeed("https://example.com/removed", "Removed");
        File.WriteAllText(account.OpmlFile!.FilePath, Document("""
            <outline text="New"><outline text="Added" xmlUrl="https://example.com/added"/></outline>
            """));
        var reopened = _manager.Reopen(account);
        Assert.Empty(reopened.TopLevelFeeds);
        Assert.Equal("New", Assert.Single(reopened.Folders).Name);
        Assert.Equal("https://example.com/added", Assert.Single(reopened.FlattenedFeeds()).Url);
    }

    [Fact]
    public void FailedRestoreKeepsExistingSubscriptions()
    {
        var account = _manager.CreateAccount();
        account.AddFeed("https://example.com/feed", "Feed");
        File.WriteAllText(account.OpmlFile!.FilePath, "<opml><broken>");
        var reopened = _manager.Reopen(account);
        Assert.Equal("https://example.com/feed", Assert.Single(reopened.TopLevelFeeds).Url);
        Assert.Equal("<opml><broken>", File.ReadAllText(reopened.OpmlFile!.FilePath));
    }

    [Fact]
    public void RenameUpdatesOpmlMirror()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed", "Feed");
        feed.EditedName = "Renamed";
        feed.NewArticleNotificationsEnabled = true;
        account.SaveChanges();
        Assert.Equal("Renamed", (string?)XDocument.Load(account.OpmlFile!.FilePath).Descendants("outline").Single().Attribute("title"));
        var reopened = _manager.Reopen(account);
        Assert.Equal("Renamed", Assert.Single(reopened.FlattenedFeeds()).EditedName);
        Assert.True(Assert.Single(reopened.FlattenedFeeds()).NewArticleNotificationsEnabled);
    }

    [Fact]
    public void CustomFolderAttributesAreOnlyWrittenToTheOwnFile()
    {
        var account = _manager.CreateAccount();
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "remote<&\"";
        account.SaveChanges();
        var publicOutline = XDocument.Parse(account.ExportOpml()).Descendants("outline").Single();
        Assert.Null(publicOutline.Attribute("nnw_externalID"));
        var storedOutline = XDocument.Load(account.OpmlFile!.FilePath).Descendants("outline").Single();
        Assert.Equal(folder.ExternalID, (string?)storedOutline.Attribute("nnw_externalID"));
        Assert.Equal(folder.ExternalID, Assert.Single(_manager.Reopen(account).Folders).ExternalID);
    }

    [Fact]
    public void FailedOpmlWriteIsReportedAndNewerSettingsSurviveRestart()
    {
        var account = _manager.CreateAccount();
        account.AddFeed("https://example.com/old", "Old");
        using (var locked = new FileStream(account.OpmlFile!.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => account.AddFeed("https://example.com/new", "New"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }

        var reopened = _manager.Reopen(account);
        Assert.Equal(2, reopened.TopLevelFeeds.Count);
        Assert.Contains(reopened.TopLevelFeeds, f => f.Url == "https://example.com/new");
        Assert.Equal(2, XDocument.Load(reopened.OpmlFile!.FilePath).Descendants("outline").Count());
    }

    internal static string Document(string outlines) => $"<opml version=\"1.1\"><body>{outlines}</body></opml>";
    internal static IReadOnlyList<OpmlItem> Parse(string outlines)
        => OpmlParser.Parse(new ParserData("https://example.com/subscriptions.opml", Encoding.UTF8.GetBytes(Document(outlines)))).Children;

    public void Dispose() => _manager.Dispose();
}
