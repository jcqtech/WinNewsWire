using System.Runtime.InteropServices;
using System.Text.Json;
using WinNewsWire.AppShared.Scripting;

namespace WinNewsWire.Automation;

[ComVisible(false)]
public abstract class ScriptObject
{
    internal AutomationSession Session { get; }
    internal ScriptingReference Reference { get; }

    private protected ScriptObject(AutomationSession session, ScriptingReference reference)
        => (Session, Reference) = (session, reference);

    private protected string Text(string property) => (string)Session.Get(Reference, property)!;
    private protected bool Flag(string property) => (bool)Session.Get(Reference, property)!;
    private protected object Collection(string property) => Session.Collection(Reference, property);
    private protected void SetFlag(string property, bool value)
        => Session.Send(new("set", Reference, property, JsonSerializer.SerializeToElement(value)));
    public bool Exists => Session.Send(new("exists", Reference))!.Value.GetBoolean();
    private protected void DeleteObject() => Session.Send(new("delete", Reference));
    private protected object MakeFeed(string url, string name)
        => Session.Make(ScriptingObjectKind.Feed, Reference, string.IsNullOrEmpty(name) ? null : name, url);
}

[ComVisible(true), Guid(AutomationIdentity.ClassID), ProgId(AutomationIdentity.ProgID),
 ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IApplication))]
public sealed class ScriptApplication : ScriptObject, IApplication
{
    internal ScriptApplication(AutomationSession session) : base(session, new()) { }
    public string Name => Text("name");
    public string Version => Text("version");
    public object? CurrentArticle => Session.Get(Reference, "currentArticle");
    public object SelectedArticles => Collection("selectedArticles");
    public object SelectedFeeds => Collection("selectedFeeds");
    public object Accounts => Collection("accounts");
    public object Articles => SelectedArticles;
    public object Feeds => Collection("feeds");
    public object WebFeeds => Feeds;
    public void OpenLocation(string url) => Session.Send(new("openLocation", Url: url));
    public object Make(string newKind, object at, object withData, object withProperties)
        => Session.Make(newKind, at, withData, withProperties);
    public void Delete(object target)
    {
        if (target is ScriptCollection) Session.RequireCollection(target).Delete();
        else Session.Send(new("delete", Session.RequireObject(target).Reference));
    }
    public new bool Exists(object? target)
    {
        if (target is null) return false;
        if (target is ScriptCollection) return Session.RequireCollection(target).References().All(item =>
            Session.Send(new("exists", item))!.Value.GetBoolean());
        return Session.RequireObject(target).Exists;
    }
    public int Count(object collection) => Session.RequireCollection(collection).Count;
    public object CreateFeed(string url, string name = "") => MakeFeed(url, name);
    public object CreateFolder(string name) => Session.Make(ScriptingObjectKind.Folder, Reference, name, null);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IAccount))]
public sealed class ScriptAccount : ScriptObject, IAccount
{
    internal ScriptAccount(AutomationSession session, ScriptingReference reference) : base(session, reference) { }
    public string Name => Text("name");
    public string ID => Text("id");
    public string AccountType => Text("accountType");
    public bool Active { get => Flag("active"); set => SetFlag("active", value); }
    public object AllFeeds => Collection("allFeeds");
    public string OpmlRepresentation => Text("opmlRepresentation");
    public object Feeds => Collection("feeds");
    public object Folders => Collection("folders");
    public object AllWebFeeds => AllFeeds;
    public object WebFeeds => Feeds;
    public object CreateFeed(string url, string name = "") => MakeFeed(url, name);
    public object CreateFolder(string name) => Session.Make(ScriptingObjectKind.Folder, Reference, name, null);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IFolder))]
public sealed class ScriptFolder : ScriptObject, IFolder
{
    internal ScriptFolder(AutomationSession session, ScriptingReference reference) : base(session, reference) { }
    public string Name => Text("name");
    public int ID => (int)Session.Get(Reference, "id")!;
    public string OpmlRepresentation => Text("opmlRepresentation");
    public object Feeds => Collection("feeds");
    public object Articles => Collection("articles");
    public object WebFeeds => Feeds;
    public object CreateFeed(string url, string name = "") => MakeFeed(url, name);
    public void Delete() => DeleteObject();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IFeed))]
public sealed class ScriptFeed : ScriptObject, IFeed
{
    internal ScriptFeed(AutomationSession session, ScriptingReference reference) : base(session, reference) { }
    public string Name => Text("name");
    public string ID => Text("id");
    public string Url => Text("url");
    public string HomePageUrl => Text("homePageUrl");
    public string IconUrl => Text("iconUrl");
    public string FaviconUrl => Text("faviconUrl");
    public string OpmlRepresentation => Text("opmlRepresentation");
    public object Authors => Collection("authors");
    public object Articles => Collection("articles");
    public void Delete() => DeleteObject();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IArticle))]
public sealed class ScriptArticle : ScriptObject, IArticle
{
    internal ScriptArticle(AutomationSession session, ScriptingReference reference) : base(session, reference) { }
    public string ID => Text("id");
    public string Title => Text("title");
    public string? Url => (string?)Session.Get(Reference, "url");
    public string? ExternalUrl => (string?)Session.Get(Reference, "externalUrl");
    public string? Permalink => (string?)Session.Get(Reference, "permalink");
    public string Contents => Text("contents");
    public string Html => Text("html");
    public string Summary => Text("summary");
    public object? PublishedDate => Session.Get(Reference, "publishedDate");
    public object ArrivedDate => Session.Get(Reference, "arrivedDate")!;
    public object? ModifiedDate => Session.Get(Reference, "modifiedDate");
    public bool Read { get => Flag("read"); set => SetFlag("read", value); }
    public bool Starred { get => Flag("starred"); set => SetFlag("starred", value); }
    public bool Deleted => Flag("deleted");
    public string ImageUrl => Text("imageUrl");
    public object? Feed => Session.Get(Reference, "feed");
    public object Authors => Collection("authors");
    public object? DatePublished => PublishedDate;
    public object DateArrived => ArrivedDate;
    public object? DateModified => ModifiedDate;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IAuthor))]
public sealed class ScriptAuthor : ScriptObject, IAuthor
{
    internal ScriptAuthor(AutomationSession session, ScriptingReference reference) : base(session, reference) { }
    public string Name => Text("name");
    public string ID => Text("id");
    public string Url => Text("url");
    public string AvatarUrl => Text("avatarUrl");
    public string EmailAddress => Text("emailAddress");
    public string Email => EmailAddress;
}
