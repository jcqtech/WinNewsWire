using WinNewsWire.Account;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Scripting;

public sealed record ScriptingSelection(Article? CurrentArticle, IReadOnlyList<Article> SelectedArticles,
    IReadOnlyList<(Feed Feed, int? FolderID)> SelectedFeeds)
{
    public static ScriptingSelection Empty { get; } = new(null, [], []);
}

public sealed class ScriptingService
{
    private readonly Func<IReadOnlyCollection<Account.Account>> _accounts;
    private readonly Func<Account.Account> _defaultAccount;
    private readonly Func<ScriptingSelection> _selection;
    private readonly Action<AppActivationRequest> _openLocation;
    private readonly string _version;

    public ScriptingService(AccountManager manager, Func<ScriptingSelection> selection,
        Action<AppActivationRequest> openLocation, string version)
        : this(() => manager.Accounts, () => manager.DefaultAccount, selection, openLocation, version) { }

    internal ScriptingService(Func<IReadOnlyCollection<Account.Account>> accounts, Func<Account.Account> defaultAccount,
        Func<ScriptingSelection> selection, Action<AppActivationRequest> openLocation, string version)
    {
        _accounts = accounts;
        _defaultAccount = defaultAccount;
        _selection = selection;
        _openLocation = openLocation;
        _version = version;
    }

    public async Task<ScriptingResponse> HandleAsync(ScriptingRequest request, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var target = request.Target ?? new ScriptingReference();
            ValidateReference(target);
            switch (request.Command)
            {
                case "get":
                    return ScriptingProtocol.Result(request.ID, await GetAsync(target, request.Property ?? "properties", ct));
                case "count":
                    var values = await GetAsync(target, request.Property ?? throw new ArgumentException("Count requires a collection property."), ct);
                    return values is IReadOnlyCollection<ScriptingReference> collection
                        ? ScriptingProtocol.Result(request.ID, collection.Count)
                        : throw new ArgumentException("Only object collections can be counted.");
                case "exists":
                    try { await ResolveAsync(target, ct); return ScriptingProtocol.Result(request.ID, true); }
                    catch (KeyNotFoundException) { return ScriptingProtocol.Result(request.ID, false); }
                case "make":
                    return ScriptingProtocol.Result(request.ID, await MakeAsync(request, target, ct));
                case "delete":
                    await DeleteAsync(target, ct);
                    return ScriptingProtocol.NoResult(request.ID);
                case "set":
                    await SetAsync(request, target, ct);
                    return ScriptingProtocol.NoResult(request.ID);
                case "openLocation":
                    var location = request.Url ?? throw new ArgumentException("Open Location requires a URL.");
                    if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
                        throw new ArgumentException("Open Location requires an absolute URL.");
                    _openLocation(AppActivationRequest.FromUri(uri));
                    return ScriptingProtocol.NoResult(request.ID);
                default:
                    throw new ArgumentException("Unknown automation command: " + request.Command);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (KeyNotFoundException error) { return ScriptingProtocol.Failure(request.ID, "notFound", error.Message); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return ScriptingProtocol.Failure(request.ID, "invalidRequest", error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
            or Microsoft.Data.Sqlite.SqliteException or FeedFinder.FeedNotFoundException or Secrets.CredentialsException)
        {
            return ScriptingProtocol.Failure(request.ID, "operationFailed", error.Message);
        }
    }

    private Account.Account AccountFor(ScriptingReference reference)
        => _accounts().FirstOrDefault(account => account.AccountID == reference.AccountID)
            ?? throw new KeyNotFoundException("The account was not found.");

    private static Feed FeedFor(Account.Account account, ScriptingReference reference)
        => account.FlattenedFeeds().FirstOrDefault(feed => feed.FeedID == reference.FeedID)
            ?? throw new KeyNotFoundException("The feed was not found.");

    private static Folder FolderFor(Account.Account account, ScriptingReference reference)
        => account.Folders.FirstOrDefault(folder => folder.FolderID == reference.FolderID)
            ?? throw new KeyNotFoundException("The folder was not found.");

    private async Task<object> ResolveAsync(ScriptingReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference.Kind == ScriptingObjectKind.Application) return this;
        var account = AccountFor(reference);
        if (reference.Kind == ScriptingObjectKind.Account) return account;
        if (reference.Kind == ScriptingObjectKind.Folder) return FolderFor(account, reference);
        var feed = FeedFor(account, reference);
        if (reference.FolderID is not null && !FolderFor(account, reference).Feeds.Contains(feed))
            throw new KeyNotFoundException("The feed is not in the specified folder.");
        if (reference.Kind == ScriptingObjectKind.Feed) return feed;
        Article? article = null;
        if (reference.ArticleID is not null)
        {
            var matches = (await account.Database.FetchArticlesAsync(feed.FeedID))
                .Where(article => article.UniqueID == reference.ArticleID).Take(2).ToArray();
            if (matches.Length == 0) throw new KeyNotFoundException("The feed's article was not found.");
            if (matches.Length != 1) throw new InvalidOperationException("The article's feed-supplied ID is ambiguous.");
            article = matches[0];
        }
        if (reference.Kind == ScriptingObjectKind.Article)
            return article ?? throw new ArgumentException("An article reference requires its feed-supplied article ID.");
        if (reference.Kind == ScriptingObjectKind.Author)
            return ((article is null ? feed.Authors : article.Authors)?.AsEnumerable() ?? Enumerable.Empty<Author>())
                .FirstOrDefault(author => author.AuthorID == reference.AuthorID)
                ?? throw new KeyNotFoundException("The author was not found.");
        throw new ArgumentException("The object kind is not supported.");
    }

    private async Task<object?> GetAsync(ScriptingReference reference, string property, CancellationToken ct)
    {
        var value = await ResolveAsync(reference, ct);
        if (value is ScriptingService)
        {
            var selection = _selection();
            return property switch
            {
                "name" => AppConfig.AppName,
                "version" => _version,
                "accounts" => _accounts().Select(AccountReference).ToArray(),
                "feeds" => _accounts().Where(account => account.IsActive)
                    .SelectMany(account => account.TopLevelFeeds.Select(feed => FeedReference(feed, null))).ToArray(),
                "currentArticle" => selection.CurrentArticle is { } current ? ArticleReference(current) : null,
                "selectedArticles" or "articles" => selection.SelectedArticles.Select(article => ArticleReference(article)).OfType<ScriptingReference>().ToArray(),
                "selectedFeeds" => selection.SelectedFeeds.Select(item => FeedReference(item.Feed, item.FolderID)).ToArray(),
                "properties" => new { name = AppConfig.AppName, version = _version, currentArticle = selection.CurrentArticle is { } article ? ArticleReference(article) : null },
                _ => throw UnknownProperty(property)
            };
        }
        if (value is Account.Account accountValue)
            return property switch
            {
                "id" => accountValue.AccountID,
                "name" => accountValue.NameForDisplay,
                "accountType" => AccountTypeCode(accountValue.Type),
                "active" => accountValue.IsActive,
                "feeds" => accountValue.TopLevelFeeds.Select(feed => FeedReference(feed, null)).ToArray(),
                "allFeeds" => accountValue.FlattenedFeeds().Select(feed => FeedReference(feed, null)).ToArray(),
                "folders" => accountValue.Folders.Select(folder => new ScriptingReference(ScriptingObjectKind.Folder,
                    accountValue.AccountID, folder.FolderID)).ToArray(),
                "opmlRepresentation" => accountValue.OpmlString(0),
                "properties" => new { id = accountValue.AccountID, name = accountValue.NameForDisplay,
                    accountType = AccountTypeCode(accountValue.Type), active = accountValue.IsActive },
                _ => throw UnknownProperty(property)
            };
        if (value is Folder folderValue)
        {
            if (property == "articles")
            {
                var account = AccountFor(reference);
                var articles = new List<Article>();
                foreach (var feed in folderValue.Feeds)
                {
                    ct.ThrowIfCancellationRequested();
                    articles.AddRange(await account.Database.FetchArticlesAsync(feed.FeedID));
                }
                return OrderedArticles(articles).Select(article => ArticleReference(article, folderValue.FolderID)).ToArray();
            }
            return property switch
            {
                "id" => folderValue.FolderID,
                "name" => folderValue.Name ?? "",
                "feeds" => folderValue.Feeds.Select(feed => FeedReference(feed, folderValue.FolderID)).ToArray(),
                "opmlRepresentation" => folderValue.OpmlString(0),
                "properties" => new { id = folderValue.FolderID, name = folderValue.Name ?? "" },
                _ => throw UnknownProperty(property)
            };
        }
        if (value is Feed feedValue)
        {
            if (property == "articles")
                return OrderedArticles(await AccountFor(reference).Database.FetchArticlesAsync(feedValue.FeedID))
                    .Select(article => ArticleReference(article, reference.FolderID)).ToArray();
            return property switch
            {
                "id" => feedValue.FeedID,
                "name" => feedValue.Name ?? "",
                "url" => feedValue.Url,
                "homePageUrl" => feedValue.HomePageUrl ?? "",
                "iconUrl" => feedValue.IconUrl ?? "",
                "faviconUrl" => feedValue.FaviconUrl ?? "",
                "opmlRepresentation" => feedValue.OpmlString(0),
                "authors" => (feedValue.Authors?.AsEnumerable() ?? Enumerable.Empty<Author>())
                    .Select(author => reference with { Kind = ScriptingObjectKind.Author, AuthorID = author.AuthorID }).ToArray(),
                "properties" => new { id = feedValue.FeedID, name = feedValue.Name ?? "", url = feedValue.Url,
                    homePageUrl = feedValue.HomePageUrl ?? "", iconUrl = feedValue.IconUrl ?? "", faviconUrl = feedValue.FaviconUrl ?? "" },
                _ => throw UnknownProperty(property)
            };
        }
        if (value is Article articleValue)
            return property switch
            {
                "id" => articleValue.UniqueID,
                "title" => articleValue.Title ?? "",
                "url" => articleValue.RawLink ?? articleValue.RawExternalLink,
                "permalink" => articleValue.RawLink,
                "externalUrl" => articleValue.RawExternalLink,
                "contents" => articleValue.ContentText ?? "",
                "html" => articleValue.ContentHtml ?? "",
                "summary" => articleValue.Summary ?? "",
                "publishedDate" => articleValue.DatePublished,
                "modifiedDate" => articleValue.DateModified,
                "arrivedDate" => articleValue.Status.DateArrived,
                "read" => articleValue.Status.Read,
                "starred" => articleValue.Status.Starred,
                "deleted" => false,
                "imageUrl" => articleValue.RawImageLink ?? "",
                "feed" => ParentFeedReference(articleValue),
                "authors" => (articleValue.Authors?.AsEnumerable() ?? Enumerable.Empty<Author>())
                    .Select(author => reference with { Kind = ScriptingObjectKind.Author, AuthorID = author.AuthorID }).ToArray(),
                "properties" => new { id = articleValue.UniqueID, title = articleValue.Title ?? "", url = articleValue.RawLink ?? articleValue.RawExternalLink,
                    permalink = articleValue.RawLink, externalUrl = articleValue.RawExternalLink, contents = articleValue.ContentText ?? "",
                    html = articleValue.ContentHtml ?? "", summary = articleValue.Summary ?? "", publishedDate = articleValue.DatePublished,
                    modifiedDate = articleValue.DateModified, arrivedDate = articleValue.Status.DateArrived,
                    read = articleValue.Status.Read, starred = articleValue.Status.Starred, deleted = false, imageUrl = articleValue.RawImageLink ?? "" },
                _ => throw UnknownProperty(property)
            };
        if (value is Author authorValue)
            return property switch
            {
                "id" => authorValue.AuthorID,
                "name" => authorValue.Name ?? "",
                "url" => authorValue.Url ?? "",
                "avatarUrl" => authorValue.AvatarUrl ?? "",
                "emailAddress" => authorValue.EmailAddress ?? "",
                "properties" => new { id = authorValue.AuthorID, name = authorValue.Name ?? "", url = authorValue.Url ?? "",
                    avatarUrl = authorValue.AvatarUrl ?? "", emailAddress = authorValue.EmailAddress ?? "" },
                _ => throw UnknownProperty(property)
            };
        throw new ArgumentException("The object cannot be queried.");
    }

    private async Task<ScriptingReference> MakeAsync(ScriptingRequest request, ScriptingReference target, CancellationToken ct)
    {
        if (request.NewKind is not (ScriptingObjectKind.Feed or ScriptingObjectKind.Folder))
            throw new ArgumentException("Only feeds and folders can be created.");
        if (target.Kind is not (ScriptingObjectKind.Application or ScriptingObjectKind.Account or ScriptingObjectKind.Folder))
            throw new ArgumentException("Create feeds or folders in an account or folder.");
        var account = target.Kind == ScriptingObjectKind.Application ? _defaultAccount() : AccountFor(target);
        var folder = target.Kind == ScriptingObjectKind.Folder ? FolderFor(account, target) : null;
        if (request.NewKind == ScriptingObjectKind.Folder)
        {
            if (folder is not null) throw new NotSupportedException("Nested folders are not supported.");
            var created = await account.CreateFolderAsync(request.Name ?? throw new ArgumentException("A folder name is required."), ct);
            return new(ScriptingObjectKind.Folder, account.AccountID, created.FolderID);
        }
        var url = request.Url ?? throw new ArgumentException("A feed URL is required.");
        var normalized = SharedFeedUrl.Parse(url);
        if (account.ExistingFeedWithUrl(normalized) is { } existing)
        {
            var existingFolder = folder?.Feeds.Contains(existing) == true ? folder
                : account.TopLevelFeeds.Contains(existing) ? null : account.Folders.FirstOrDefault(candidate => candidate.Feeds.Contains(existing));
            return FeedReference(existing, existingFolder?.FolderID);
        }
        var feed = await account.CreateFeedAsync(normalized, request.Name, folder, ct)
            ?? throw new FeedFinder.FeedNotFoundException();
        return FeedReference(feed, folder?.FolderID);
    }

    private async Task DeleteAsync(ScriptingReference target, CancellationToken ct)
    {
        if (target.Kind is not (ScriptingObjectKind.Feed or ScriptingObjectKind.Folder))
            throw new NotSupportedException("Only feeds and folders can be deleted.");
        var resolved = await ResolveAsync(target, ct);
        var account = AccountFor(target);
        if (resolved is Folder folder) await account.RemoveFolderAsync(folder, ct);
        else if (resolved is Feed feed)
            await account.RemoveFeedAsync(feed, target.FolderID is null ? null : FolderFor(account, target), ct);
        else throw new NotSupportedException("Only feeds and folders can be deleted.");
    }

    private async Task SetAsync(ScriptingRequest request, ScriptingReference target, CancellationToken ct)
    {
        if (request.Value is not { ValueKind: System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False } value)
            throw new ArgumentException("The writable scripting properties require a Boolean value.");
        bool flag = value.GetBoolean();
        var resolved = await ResolveAsync(target, ct);
        if (resolved is Account.Account account && request.Property == "active") { account.IsActive = flag; return; }
        if (resolved is Article article && request.Property is "read" or "starred")
        {
            var owner = AccountFor(target);
            if (request.Property == "read" && !flag && !article.IsAvailableToMarkUnread(owner.Type))
                throw new NotSupportedException("This service cannot mark the selected article unread.");
            await owner.MarkAsync([article.ArticleID], request.Property == "read" ? ArticleStatus.Key.Read : ArticleStatus.Key.Starred, flag, ct);
            await owner.RecalculateUnreadCountsAsync();
            return;
        }
        throw new NotSupportedException("That scripting property is read-only.");
    }

    private static ScriptingReference AccountReference(Account.Account account) => new(ScriptingObjectKind.Account, account.AccountID);
    private static ScriptingReference FeedReference(Feed feed, int? folderID)
        => new(ScriptingObjectKind.Feed, feed.AccountID, folderID, feed.FeedID);
    private ScriptingReference? ParentFeedReference(Article article)
    {
        var account = _accounts().FirstOrDefault(account => account.AccountID == article.AccountID);
        var feed = account?.FlattenedFeeds().FirstOrDefault(feed => feed.FeedID == article.FeedID);
        return feed is null ? null : FeedReference(feed, account!.Folders.FirstOrDefault(folder => folder.Feeds.Contains(feed))?.FolderID);
    }
    private ScriptingReference? ArticleReference(Article article, int? folderID = null)
        => ParentFeedReference(article) is { } parent
            ? parent with { Kind = ScriptingObjectKind.Article, FolderID = folderID ?? parent.FolderID, ArticleID = article.UniqueID } : null;
    private static IEnumerable<Article> OrderedArticles(IEnumerable<Article> articles)
        => articles.OrderByDescending(article => article.DatePublished ?? article.DateModified ?? article.Status.DateArrived);
    private static ArgumentException UnknownProperty(string property) => new("Unknown scripting property: " + property);
    private static void ValidateReference(ScriptingReference reference)
    {
        if (!Enum.IsDefined(reference.Kind)) throw new ArgumentException("The object kind is invalid.");
        if (reference.Kind == ScriptingObjectKind.Application)
        {
            if (reference.AccountID is not null || reference.FolderID is not null || reference.FeedID is not null
                || reference.ArticleID is not null || reference.AuthorID is not null)
                throw new ArgumentException("The application reference has no container or object IDs.");
            return;
        }
        if (string.IsNullOrWhiteSpace(reference.AccountID)) throw new ArgumentException("An account ID is required.");
        if (reference.Kind == ScriptingObjectKind.Account)
        {
            if (reference.FolderID is not null || reference.FeedID is not null || reference.ArticleID is not null || reference.AuthorID is not null)
                throw new ArgumentException("An account reference cannot include child IDs.");
            return;
        }
        if (reference.Kind == ScriptingObjectKind.Folder)
        {
            if (reference.FolderID is null || reference.FeedID is not null || reference.ArticleID is not null || reference.AuthorID is not null)
                throw new ArgumentException("A folder reference requires only account and folder IDs.");
            return;
        }
        if (string.IsNullOrEmpty(reference.FeedID)) throw new ArgumentException("A feed ID is required.");
        if (reference.Kind == ScriptingObjectKind.Feed && (reference.ArticleID is not null || reference.AuthorID is not null))
            throw new ArgumentException("A feed reference cannot include article or author IDs.");
        if (reference.Kind == ScriptingObjectKind.Article && (reference.ArticleID is null || reference.AuthorID is not null))
            throw new ArgumentException("An article reference requires its feed-supplied ID and no author ID.");
        if (reference.Kind == ScriptingObjectKind.Author && string.IsNullOrEmpty(reference.AuthorID))
            throw new ArgumentException("An author ID is required.");
    }

    private static string AccountTypeCode(AccountType type) => type switch
    {
        AccountType.OnMyMac => "Locl", AccountType.CloudKit => "Clkt", AccountType.Feedly => "Fdly",
        AccountType.Feedbin => "Fdbn", AccountType.NewsBlur => "NBlr", AccountType.FreshRSS => "Frsh",
        AccountType.Inoreader => "Inrd", AccountType.BazQux => "Bzqx", AccountType.TheOldReader => "Tord",
        _ => throw new NotSupportedException("The account type has no scripting representation.")
    };
}
