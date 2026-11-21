using System.Collections;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinNewsWire.AppShared.Scripting;
using WinNewsWire.Automation;
using Xunit;

namespace WinNewsWire.ComServer.Tests;

public sealed class ObjectModelTests
{
    private static readonly ScriptingReference Account = new(ScriptingObjectKind.Account, "account");
    private static readonly ScriptingReference Feed = new(ScriptingObjectKind.Feed, "account", FeedID: "feed");
    private static readonly ScriptingReference Article = Feed with { Kind = ScriptingObjectKind.Article, ArticleID = "publisher-id" };
    private static readonly ScriptingReference Folder = new(ScriptingObjectKind.Folder, "account", 42);

    [Fact]
    public void CollectionsAreLiveOneBasedEnumerableAndScoped()
    {
        ScriptingReference[] members = [Feed];
        var transport = new Transport(request => ScriptingProtocol.Result(null, request.Command == "count" ? members.Length
            : request.Property == "feeds" ? members : request.Property == "id" ? "feed" : "Publication"));
        var session = new AutomationSession(transport);
        var collection = (ScriptCollection)((ScriptApplication)session.Wrap(new())).Feeds;
        Assert.Equal(1, collection.Count);
        Assert.Equal(Feed, Assert.IsType<ScriptFeed>(collection[1]).Reference);
        Assert.Equal(Feed, Assert.IsType<ScriptFeed>(collection["Publication"]).Reference);
        Assert.Equal(Feed, Assert.IsType<ScriptFeed>(collection.ByID("feed")).Reference);
        var enumerator = collection.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(Feed, Assert.IsType<ScriptFeed>(enumerator.Current).Reference);
        Assert.False(enumerator.MoveNext());
        Assert.False(collection.Exists(0));
        Assert.False(collection.Exists("missing"));
        Assert.Equal(Errors.InvalidArgument, Assert.Throws<COMException>(() => collection[true]).HResult);
        Assert.Equal(Errors.InvalidArgument, Assert.Throws<COMException>(() => collection[1.5]).HResult);
        members = [];
        Assert.Equal(0, collection.Count);
        Assert.Equal(Errors.NotFound, Assert.Throws<COMException>(() => collection[1]).HResult);
    }

    [Fact]
    public void FolderIDsRemainNumericAndDistinctFromIndices()
    {
        var session = new AutomationSession(new Transport(request => ScriptingProtocol.Result(null, 42)));
        var folders = new ScriptCollection(session, () => [Folder]);
        Assert.Equal(42, Assert.IsType<ScriptFolder>(folders.ByID(42)).ID);
        Assert.Equal(Errors.NotFound, Assert.Throws<COMException>(() => folders.ByID("42")).HResult);
        Assert.Equal(Errors.NotFound, Assert.Throws<COMException>(() => folders.ByID(42.5)).HResult);
    }

    [Fact]
    public void ArticleReferencesDatesNullsAndWritableFlagsRetainTheirTypes()
    {
        var date = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var transport = new Transport(request => request.Command == "set" ? ScriptingProtocol.NoResult(null)
            : ScriptingProtocol.Result(null, request.Property switch
            {
                "publishedDate" or "arrivedDate" => date,
                "modifiedDate" or "url" => (object?)null,
                "id" => "publisher-id", "feed" => Feed, "read" => true, _ => false
            }));
        var session = new AutomationSession(transport);
        var article = (ScriptArticle)session.Wrap(Article);
        Assert.Equal("publisher-id", article.ID);
        Assert.Equal(date.ToLocalTime(), article.PublishedDate);
        Assert.Equal(date.ToLocalTime(), article.DateArrived);
        Assert.Null(article.Url);
        Assert.Null(article.ModifiedDate);
        Assert.Equal(Feed, Assert.IsType<ScriptFeed>(article.Feed).Reference);
        article.Read = false;
        article.Starred = true;
        var writes = transport.Requests.Where(request => request.Command == "set").ToArray();
        Assert.Equal(["read", "starred"], writes.Select(request => request.Property));
        Assert.All(writes, request => Assert.Equal(Article, request.Target));
        Assert.False(writes[0].Value!.Value.GetBoolean());
        Assert.True(writes[1].Value!.Value.GetBoolean());
    }

    [Fact]
    public void MakeSupportsDataPropertiesAliasesAndExplicitLocation()
    {
        var transport = new Transport(_ => ScriptingProtocol.Result(null, Feed));
        var session = new AutomationSession(transport);
        var app = (ScriptApplication)session.Wrap(new());
        var folder = session.Wrap(Folder);
        app.Make("webFeed", folder, "https://example.test/rss", new Hashtable { ["name"] = "Example" });
        var request = Assert.Single(transport.Requests);
        Assert.Equal(new ScriptingRequest("make", Folder, NewKind: ScriptingObjectKind.Feed,
            Name: "Example", Url: "https://example.test/rss"), request);
        app.Make("feed", Type.Missing, Type.Missing, new Hashtable { ["url"] = "https://example.test/other" });
        Assert.Equal("https://example.test/other", transport.Requests[^1].Url);
        Assert.Equal(new(), transport.Requests[^1].Target);
        app.Make("folder", Type.Missing, Type.Missing, Type.Missing);
        Assert.Equal("", transport.Requests[^1].Name);
        Assert.Throws<COMException>(() => app.Make("account", null!, null!, null!));
        Assert.Throws<COMException>(() => app.Make("feed", "not a location", null!, null!));
    }

    [Fact]
    public void ProjectionFilteringBulkDeleteAndStandardCommandsUseRealReferences()
    {
        var other = Feed with { FeedID = "other", FolderID = 42 };
        var transport = new Transport(request => request.Command switch
        {
            "getMany" => ScriptingProtocol.Result(null, request.Targets!.Select(target => target == Feed ? "One" : "Two").ToArray()),
            "exists" => ScriptingProtocol.Result(null, true),
            _ => ScriptingProtocol.NoResult(null)
        });
        var session = new AutomationSession(transport);
        var collection = new ScriptCollection(session, () => [Feed, other]);
        Assert.Equal(["One", "Two"], collection.Get("Name"));
        var filtered = (ScriptCollection)collection.Where("name", "Two");
        Assert.Equal(other, Assert.IsType<ScriptFeed>(filtered[1]).Reference);
        var app = (ScriptApplication)session.Wrap(new());
        Assert.Equal(2, app.Count(collection));
        Assert.True(app.Exists(collection));
        app.Delete(filtered);
        Assert.Equal(other, Assert.Single(transport.Requests, request => request.Command == "delete").Target);
        app.OpenLocation("https://example.test/rss");
        Assert.Equal("openLocation", transport.Requests[^1].Command);
        Assert.False(app.Exists(null));
        Assert.Throws<COMException>(() => app.Delete(new ScriptCollection(session, () => [Feed, Article])));
        Assert.Single(transport.Requests, request => request.Command == "delete");
    }

    [Theory]
    [InlineData("notFound", Errors.NotFound)]
    [InlineData("invalidRequest", Errors.InvalidArgument)]
    [InlineData("operationFailed", Errors.OperationFailed)]
    public void ProtocolFailuresBecomeExplicitComErrors(string code, int hresult)
    {
        var session = new AutomationSession(new Transport(_ => ScriptingProtocol.Failure(null, code, "Failure detail")));
        var error = Assert.Throws<COMException>(() => ((ScriptAccount)session.Wrap(Account)).Name);
        Assert.Equal(hresult, error.HResult);
        Assert.Equal("Failure detail", error.Message);
        var collection = new ScriptCollection(session, () => [Feed]);
        if (code == "notFound") Assert.False(collection.Exists("missing"));
        else Assert.Throws<COMException>(() => collection.Exists("missing"));
    }

    [Fact]
    public void InterfacesExposeOnlyDictionaryWritesAndStableDispIDs()
    {
        var interfaces = new[] { typeof(IApplication), typeof(IAccount), typeof(IFolder), typeof(IFeed), typeof(IArticle), typeof(IAuthor) };
        var writable = interfaces.SelectMany(type => type.GetProperties()
            .Where(property => property.CanWrite).Select(property => type.Name + "." + property.Name));
        Assert.Equal(["IAccount.Active", "IArticle.Read", "IArticle.Starred"], writable);
        foreach (var type in interfaces.Append(typeof(IObjectCollection)))
        {
            Assert.Equal(ComInterfaceType.InterfaceIsDual, type.GetCustomAttributes(typeof(InterfaceTypeAttribute), false)
                .Cast<InterfaceTypeAttribute>().Single().Value);
            var members = type.GetMembers().Where(member => member.MemberType is System.Reflection.MemberTypes.Property
                || member.MemberType == System.Reflection.MemberTypes.Method && !((System.Reflection.MethodInfo)member).IsSpecialName);
            var ids = members.Select(member => member.GetCustomAttributes(typeof(DispIdAttribute), false)
                .Cast<DispIdAttribute>().Single().Value).ToArray();
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
    }

    [Fact]
    public void LargeProjectionsUseBoundedBatchesAndPagesRetainSnapshotMembership()
    {
        var members = Enumerable.Range(0, 300).Select(index => Feed with { FeedID = "feed-" + index }).ToArray();
        var transport = new Transport(request => request.Command == "getMany"
            ? ScriptingProtocol.Result(null, request.Targets!.Select(target => target.FeedID).ToArray())
            : throw new InvalidOperationException("Unexpected per-object request."));
        var session = new AutomationSession(transport);
        var collection = new ScriptCollection(session, () => members);
        Assert.Equal(members.Select(item => item.FeedID), collection.Get("id"));
        Assert.Equal([128, 128, 44], transport.Requests.Select(request => request.Targets!.Length));
        var page = (ScriptCollection)collection.GetPage(129, 128);
        members = [];
        Assert.Equal(128, page.Count);
        Assert.Equal("feed-128", Assert.IsType<ScriptFeed>(page[1]).Reference.FeedID);
        Assert.Equal("feed-255", Assert.IsType<ScriptFeed>(page[128]).Reference.FeedID);
        Assert.Throws<COMException>(() => collection.GetPage(0, 10));
        Assert.Throws<COMException>(() => collection.GetPage(1, 4097));
    }

    private sealed class Transport(Func<ScriptingRequest, ScriptingResponse> send) : IAutomationTransport
    {
        internal List<ScriptingRequest> Requests { get; } = [];
        public ScriptingResponse Send(ScriptingRequest request) { Requests.Add(request); return send(request); }
    }
}
