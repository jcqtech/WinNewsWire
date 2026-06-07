# WinNewsWire port of NetNewsWire (Mac) — full specification

This document is the authoritative spec for porting NetNewsWire's **Mac** app to Windows
(C# + WinUI 3). It records:

- How each NetNewsWire subsystem works and what its public surface is
- How that subsystem will be expressed in C#/.NET/WinUI
- Which subsystems are explicitly **out of scope** per the user's instructions
  (iOS, Safari/Share extensions, CloudKit sync, AppleScript scripting, Sparkle updater)

Companion documents:

- `PORT_STATUS.md` — what is implemented right now and what is left to do.

Units: every subsection below corresponds to either a Swift package under
`NetNewsWire/Modules/`, or a folder under `NetNewsWire/Mac/` or `NetNewsWire/Shared/`.

---

## 0. Target environment

- **Solution**: `WinNewsWire.sln` (existing).
- **Shell app**: `WinNewsWire/WinNewsWire/WinNewsWire.csproj` — WinUI 3, WinAppSDK 1.8, .NET 8.
- **Packaging**: `WinNewsWire (Package).wapproj` (existing).
- **New projects** (added during the port):
  - `WinNewsWire/Modules/Parsers/Parsers.csproj` — port of `RSParser`.
  - `WinNewsWire/Modules/Core/Core.csproj` — port of `RSCore` (non-AppKit parts).
  - `WinNewsWire/Modules/Database/Database.csproj` — port of `RSDatabase`.
  - `WinNewsWire/Modules/Web/Web.csproj` — port of `RSWeb`.
  - `WinNewsWire/Modules/Articles/Articles.csproj` — port of `Articles`.
  - `WinNewsWire/Modules/ArticlesDatabase/ArticlesDatabase.csproj` — port of `ArticlesDatabase`.
  - `WinNewsWire/Modules/SyncDatabase/SyncDatabase.csproj` — port of `SyncDatabase`.
  - `WinNewsWire/Modules/FeedFinder/FeedFinder.csproj` — port of `FeedFinder`.
  - `WinNewsWire/Modules/Tree/Tree.csproj` — port of `RSTree`.
  - `WinNewsWire/Modules/ErrorLog/ErrorLog.csproj` — port of `ErrorLog`.
  - `WinNewsWire/Modules/Secrets/Secrets.csproj` — port of `Secrets`.
  - `WinNewsWire/Modules/Account/Account.csproj` — port of `Account` (local + optional Feedbin).
  - `WinNewsWire/Modules/Markdown/Markdown.csproj` — thin wrapper over Markdig, mirrors `RSMarkdown`.
  - `WinNewsWire/Tests/*.Tests.csproj` — xUnit test projects, one per module.

- **Third-party dependencies**:
  - `Microsoft.Data.Sqlite` (already present) for SQLite; FTS5 available.
  - `HtmlAgilityPack` for HTML parsing (replaces libxml2 + `RSSAXHTMLParser`).
  - `Markdig` for Markdown → HTML (replaces `RSMarkdown`/`md4c`).
  - `CommunityToolkit.Mvvm` (already present) for `ObservableObject`, `[RelayCommand]`.
  - `xunit` + `xunit.runner.visualstudio` for tests.
  - `System.Security.Cryptography.ProtectedData` for DPAPI; `Windows.Security.Credentials` (WinRT) for Credential Manager.

---

## 1. RSParser → `Modules/Parsers`

Feed parsing: RSS, Atom, JSON Feed, RSS-in-JSON, OPML, HTML metadata, HTML link, date parsing.

### 1.1 Public types (one-to-one port)

| Swift type | C# type | Notes |
|---|---|---|
| `ParserData` | `ParserData` (record) | `string Url`, `byte[] Data`. |
| `FeedType` | `FeedType` (enum) | `Rss`, `Atom`, `JsonFeed`, `RssInJson`, `Unknown`, `NotAFeed`. |
| `ParsedFeed` | `ParsedFeed` (record) | identical fields. |
| `ParsedItem` | `ParsedItem` (record) | `Equals` by `(syncServiceID) ?? (uniqueID, feedURL)`. |
| `ParsedAuthor` | `ParsedAuthor` (record) | |
| `ParsedAttachment` | `ParsedAttachment` (record) | `Create(…)` static returns null if URL empty. |
| `ParsedHub` | `ParsedHub` (record) | |
| `FeedParserError` | `FeedParserException` (exception) | cases become subclasses or error codes. |
| `FeedParser` | `static class FeedParser` | `CanParse`, `MightBeAbleToParseBasedOnPartialData`, `Parse`. |
| `JSONFeedParser` | `static class JsonFeedParser` | |
| `RSSInJSONParser` | `static class RssInJsonParser` | |
| `RSSParser` | `static class RssParser` | XML. |
| `AtomParser` | `static class AtomParser` | XML. |
| `RSOPMLDocument`/`RSOPMLItem`/`RSOPMLParser` | `OpmlDocument` / `OpmlItem` / `OpmlParser` | |
| `RSHTMLMetadata`/`RSHTMLMetadataParser` | `HtmlMetadata` / `HtmlMetadataParser` | via HtmlAgilityPack. |
| `RSHTMLLinkParser` / `RSHTMLLink` | `HtmlLinkParser` / `HtmlLink` | |
| `RSDateParser` | `static class DateParser` | see § 1.6. |

### 1.2 `feedType(ParserData)` probe

Port of `FeedType.swift` + ObjC `NSData+RSParser.m`. The ObjC helpers look at the first
~128 bytes of the payload using small hand-rolled case-insensitive byte searches. In C#
the same check runs against `ReadOnlySpan<byte>`:

- `IsProbablyJson`: skip BOM + whitespace; first non-whitespace is `{` or `[`.
- `IsProbablyJsonFeed`: substring match (case-insensitive) on `"version"` plus `"https://jsonfeed.org/version/"` within the first 4 KB.
- `IsProbablyRssInJson`: JSON containing `"rss"` → `"channel"` tokens at the head.
- `IsProbablyRss`: XML + contains `<rss` or `<rdf:RDF` (or `http://purl.org/rss/1.0/`) within the header.
- `IsProbablyAtom`: XML + `<feed` + `http://www.w3.org/2005/Atom`.

### 1.3 RSS + Atom

Mac implementation uses a libxml2 SAX parser (`RSSAXParser`) with per-format subclasses
(`RSRSSParser`, `RSAtomParser`). Port to C# `XmlReader`:

- Build a streaming parser that reads elements and dispatches on tag name; preserves
  namespace awareness; handles `content:encoded`, `dc:creator`, `dc:date`, `media:*`,
  `itunes:*`, `atom:link rel="hub"`, `atom:link rel="next"`, `<author><name>…</name></author>`,
  `atom:summary`/`atom:content` with `type="html|xhtml|text"`.
- Produce an intermediate `RSParsedFeed`-equivalent (`TransitionalFeed` in C#), then run
  `RSParsedFeedTransformer` equivalent to turn into `ParsedFeed`/`ParsedItem`.

### 1.4 JSON Feed + RSS-in-JSON

- Strict JSON Feed parser (v1): required `version`, `items`. Item fields: `id`, `url`,
  `external_url`, `title`, `content_html`, `content_text`, `summary`, `image`,
  `banner_image`, `date_published`, `date_modified`, `authors`/`author`, `tags`,
  `attachments[]`.
- RSS-in-JSON parser: accepts the Dave Winer-style `{ "rss": { "channel": { "item": [...] } } }`.
- Use `System.Text.Json` with `JsonDocument`/`JsonElement` to minimize allocations and
  match the Swift `JSONUtilities` helpers.

### 1.5 OPML

- `OpmlParser` yields an `OpmlDocument` (root) and `OpmlItem` tree. Each item records
  `Title`, `Attributes`, `Children` (folder) or `FeedSpecifier` (leaf with `type`, `xmlUrl`,
  `htmlUrl`, `title`, `text`).
- OPML export is in `Shared/Exporters/OPMLExporter` (see § 12).

### 1.6 Date parsing

Port of `RSDateParser.m` — hand-rolled because `DateTimeOffset.TryParse` is too lax and
too slow. Matches:

- RFC 822 (`Wed, 01 Mar 2017 14:22:00 +0000`) with tolerant weekday/month lookup and
  broken-timezone fallbacks (`PST`, `UT`, etc. — table lifted verbatim).
- W3C / ISO 8601 (`2017-03-01T14:22:00Z`, `2017-03-01T14:22:00+00:00`, fractional seconds).
- Some pathological feed dates: all-digit `yyyymmdd`, leading whitespace, ordinal months.

Expose: `DateTime? TryParse(ReadOnlySpan<char>)`, `DateTime? TryParse(ReadOnlySpan<byte>)`.
Tests: port every case in `RSDateParserTests.swift` verbatim.

### 1.7 HTML metadata / link

- `HtmlMetadataParser` walks with HtmlAgilityPack. Collects `<link rel="alternate"
  type="application/rss+xml|atom+xml|json">`, `<link rel="icon|shortcut icon|apple-touch-icon">`,
  `<meta property="og:image|twitter:image|og:title|og:description">`, favicon from site root,
  `<link rel="canonical">`, etc.
- `HtmlLinkParser` extracts `<a href>` link list (used by reader-mode style previews).

### 1.8 Tests

Port every `Tests/RSParserTests/*.swift` file to xUnit (e.g.
`AtomParserTests.cs`, `RssParserTests.cs`, `JsonFeedParserTests.cs`,
`OpmlTests.cs`, `RsDateParserTests.cs`, `HtmlMetadataTests.cs`, `HtmlLinkTests.cs`,
`FeedParserTypeTests.cs`, `EntityDecodingTests.cs`, `RssInJsonParserTests.cs`).

**Test resources**: copy every file under `Modules/RSParser/Tests/RSParserTests/Resources/`
(over 50 sample feeds — `DaringFireball.atom`, `macworld.rss`, `allthis.json`, `Subs.opml`,
etc.) into `Tests/Parsers.Tests/Resources/` with `CopyToOutputDirectory=PreserveNewest`.

---

## 2. RSCore → `Modules/Core`

Catch-all Swift utility grab-bag. The AppKit-only half is discarded.

### 2.1 In-scope (port to C#)

- `String+RSCore` (trim, rs_md5Hashed, strip HTML, truncate; note `NSAttributedString`
  parts are AppKit and get discarded).
- `URL+RSCore` (absolute URL resolution — implement via `new Uri(base, rel)`).
- `Data+RSCore` (md5, is gzip, is text).
- `OrderedSet<T>` — reuse `System.Collections.Generic.HashSet` + `List<T>` in a small
  wrapper.
- `Set+RSCore` (union/subtract helpers — LINQ is sufficient).
- `BinaryDiskCache` — simple filesystem-backed binary cache keyed by string/URL.
- `FileUtilities` (temp dir, atomic write).
- `MainThreadOperation` + `MainThreadOperationQueue` — port using `DispatcherQueue`
  instead of `OperationQueue`.
- `Logger` (wrap `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions`).
- `Result<T>` — use `OneOf` style or just C# `(T? value, Exception? error)` record.

### 2.2 AppKit files (discarded or replaced in-shell)

Discarded: `NSImage`, `NSMenu`, `NSOutlineView`, `NSPasteboard`, `NSResponder`,
`NSTableView`, `NSToolbar`, `NSView`, `NSWindow`, `Keyboard.swift`. Equivalents exist
natively in WinUI (`MenuFlyout`, `TreeView`, `DataPackage`, `KeyboardAccelerator`,
`CommandBar`, etc.) and are used directly in the shell app rather than behind a
`Modules/Core` abstraction.

---

## 3. RSDatabase → `Modules/Database`

Thin async wrapper over SQLite. Port the non-FMDB subset:

- `DatabaseQueue`: serialises connections on a dedicated thread; exposes
  `Task RunInDatabase(Action<SqliteConnection>)` and
  `Task<T> RunInDatabaseThrowing<T>(…)`.
- `DatabaseTable` — base class exposing typed `Fetch`, `Update`, `Insert`, `Delete`
  helpers keyed off `databaseIdKey`, `databaseTableName`.
- `DatabaseObject` protocol → C# `IDatabaseObject`.
- `RelatedObjectsMap` — `Dictionary<string, List<IDatabaseObject>>`.
- Prepared statement helpers & NSDate conversion (`Int64 unixTime` ↔ `DateTime`).
- SQL helpers: `WhereClauseBuilder`, `InClause` generator.

FMDB-specific bits (`FMDatabase`, `FMResultSet`) are replaced by
`Microsoft.Data.Sqlite`'s `SqliteConnection`/`SqliteDataReader`.

Tests: port `RSDatabaseTests` (smoke tests for queue + `DatabaseTable`).

---

## 4. RSWeb → `Modules/Web`

HTTP pipeline and feed/HTML downloaders.

- `HTTPConditionalGetInfo` — `Etag`, `LastModified`, encoded-for-storage form.
- `HTTPDownloadSession` / `DownloadSession` — wraps `HttpClient` with
  delegate-style callbacks; dedupes in-flight requests; honours `304 Not Modified`.
- `OneShotDownload` — single `HttpClient.GetAsync` with user-agent, timeout, redirect,
  and gzip decompression. Exposes `DownloadResult(byte[] data, HttpResponseMessage resp)`.
- `HTMLMetadataDownloader` — fetch URL, parse `HtmlMetadata`, cache on disk.
- `MacWebBrowserExtensionPoint` — dropped.
- `UserAgent` builder (`WinNewsWire/<ver> (Windows; <osver>)`).
- `URLRequest+RSWeb` utilities → `HttpRequestMessage` extension methods.

Tests: port any round-trip tests with `HttpMessageHandler`-based fakes.

---

## 5. Articles → `Modules/Articles`

- `Article` (record): `ArticleID`, `AccountID`, `FeedID`, `UniqueID`, `Title`,
  `ContentHTML`, `ContentText`, `URL`, `ExternalURL`, `Summary`, `ImageURL`,
  `BannerImageURL`, `DatePublished`, `DateModified`, `Authors`, `Status`.
- `ArticleStatus`: `ArticleID`, `Read`, `Starred`, `DateArrived`.
- `Author`: `AuthorID`, `Name`, `URL`, `AvatarURL`, `EmailAddress`.
- `DatabaseID`: produces a stable 16-char hex id via MD5 over a delimiter-joined key.

No persistence here — that lives in `ArticlesDatabase`.

---

## 6. ArticlesDatabase → `Modules/ArticlesDatabase`

SQLite schema (ported verbatim from `Constants.swift` / `ArticlesTable.swift`):

- `articles(articleID PK, feedID, uniqueID, title, contentHTML, contentText, url, externalURL, summary, imageURL, bannerImageURL, datePublished, dateModified, authorsCount) `
- `statuses(articleID PK, read, starred, dateArrived)`
- `authors(authorID PK, name, url, avatarURL, emailAddress)`
- `authorsLookup(authorID, articleID)`
- `search` FTS5 virtual table with `title`, `body` indexed columns.

Operations:

- `Update(parsedItems, webFeedID, account) -> ArticleChanges` — insert/update articles,
  diff against prior set, emit `NewArticles`/`UpdatedArticles` for notifications.
- `FetchArticles(feedIDs)`, `FetchUnreadArticles(...)`, `FetchStarredArticles(...)`,
  `FetchArticlesMatching(query, feedIDs)`, `FetchUnreadCounts`, `FetchAllUnreadCounts`,
  `FetchArticlesAsync(...)` (all the variants the Mac timeline calls).
- `MarkRead`, `MarkUnread`, `MarkStarred`, `MarkUnstarred`, `CreateStatusesIfNecessary`.
- Background cleanup: delete old unread-and-unstarred articles past the retention window
  (default 90 days, from prefs).

Tests: an xUnit suite that spins up a temp SQLite file; covers insert / update /
search / unread-count / cleanup paths.

---

## 7. SyncDatabase → `Modules/SyncDatabase`

Tiny SQLite table of pending `SyncStatus` rows (`articleID`, `key`, `flag`, `selected`,
`accountID`) that a remote account delegate drains when online. Four statuses (`Read`,
`Unread`, `Starred`, `Unstarred`) backed by `syncStatus` table. Port 1-1.

---

## 8. FeedFinder → `Modules/FeedFinder`

- `FeedSpecifier` (`Title`, `UrlString`, `Source`, `OrderFound`) with a `Score` getter
  deriving from the source (userEntered, html link rel=alternate, html link
  `rel=alternate` + type=atom/rss/json, `.rss`/`.atom`/`.xml` extension) and order.
- `HTMLFeedFinder` — parse HTML, extract candidate feed URLs, resolve absolute.
- `FeedFinder` — given a URL, fetch, detect whether it's already a feed (see § 1.2); if
  not, download as HTML, run `HTMLFeedFinder`, try a handful of common fallback paths
  (`/feed`, `/rss`, `/atom.xml`), score and return the best.

Tests: port `FeedFinderTests` using HttpClient-backed fakes.

---

## 9. Tree → `Modules/Tree`

- `Node` — node with `Representedobject`, `ParentNode`, `ChildNodes`, `IsGroupItem`,
  `CanHaveChildNodes`, `IsLeaf`.
- `TreeController` — delegate-driven rebuild of the tree; `rebuild()` repopulates
  children from the delegate, preserving existing node identity when possible.
- `TreeControllerDelegate` — `ChildNodesFor(Node parent)`.

Used by sidebar + folder picker.

---

## 10. ErrorLog → `Modules/ErrorLog`

SQLite-backed table of `ErrorLogEntry(date, accountID, accountName, message)`. Exposes
`ErrorLogDatabase.Log(...)`, `ErrorLogDatabase.FetchAll()`, `ErrorLogDatabase.Clear()`
and a `Notifications` class that raises events when new entries arrive.

Tests: port `ErrorLogDatabaseTests`.

---

## 11. Secrets → `Modules/Secrets`

Thin wrapper storing per-account credentials. Mac version uses the macOS keychain via
`SecItem*` APIs. Port uses `Windows.Security.Credentials.PasswordVault` (primary) with
DPAPI-encrypted file fallback for unpackaged/dev runs.

Exposes:

- `CredentialsManager.RetrieveCredentials(service, username)` → `Credentials?`
- `CredentialsManager.StoreCredentials(service, Credentials)`
- `CredentialsManager.RemoveCredentials(service, username)`

Where `Credentials` is `record(Kind Kind, string Username, string Secret)`
(`Kind` = `BasicPassword`, `ReaderAPIKey`, `OAuthAccessToken`, `OAuthRefreshToken`,
`NewsBlurBasic`, `NewsBlurSessionId`).

---

## 12. Account → `Modules/Account`

The big one. In-scope (per user): **Local (On My Mac) account only**, with Feedbin as a
stretch goal if time allows. Feedly/NewsBlur/ReaderAPI/CloudKit are out of scope.

### 12.1 Core types

- `Account` — owns one feed tree, one `ArticlesDatabase`, one `SyncDatabase`, one
  `IAccountDelegate`. Fields: `AccountID`, `Type`, `Name`, `IsActive`, `Folders`,
  `TopLevelFeeds`, `DefaultName`, `DateCreated`.
- `AccountType` enum: `OnMyMac` (the port's rename of `onMyMac`/`OnMyPC`), `Feedbin`,
  `Feedly`, `NewsBlur`, `ReaderAPI`, `CloudKit` (marker only, unimplemented).
- `AccountManager` — singleton owning the set of active `Account`s, writes to
  `AccountSettingsDatabase`. `Instance`, `Accounts`, `ActiveAccounts`,
  `CreateAccount(Type, …)`, `DeleteAccount(account)`.
- `Folder` — has `Account`, `Name`, `ExternalID`, `TopLevelFeeds`,
  inherits `Container`. Can be named at creation and renamed via `RenameFolder`.
- `Feed` — `AccountID`, `URL`, `WebFeedID` (sync-specific id), `HomePageURL`,
  `EditedName`, `Name`, `IconURL`, `FaviconURL`, `UnreadCount`. Has `ParentContainer`.
- `Container` — protocol for anything that can own feeds/folders (`Account` or
  `Folder`).
- `OPMLFile` — reads/writes `Subscriptions.opml` inside account's dataFolder.
- `OPMLNormalizer` — dedupes/normalises imported OPML before applying to an account.
- `ContainerIdentifier` / `SidebarItemIdentifier` — stable identifiers (`Smartfeed`,
  `Account`, `Folder`, `WebFeed`).
- `UnreadCountProvider` — protocol; Account, Folder, Feed, and SmartFeed all implement
  it and propagate changes via `NotificationCenter`.

### 12.2 `IAccountDelegate`

Equivalent of `AccountDelegate.swift`. Methods (simplified):

- `Task<RefreshProgress> Refresh(Account)` — fetch all subscribed feeds for this
  account, parse, merge into articles DB.
- `Task SendArticleStatus(Account)` / `Task ReceiveArticleStatus(Account)` — noop for
  local.
- `Task<Feed> CreateFeed(Account, url, name, container, ValidateFeed)` — runs
  `FeedFinder`, downloads initial, adds to container.
- `Task RenameFeed(Account, feed, name)`, `RemoveFeed`, `MoveFeed`,
  `RestoreFeed`, `RestoreFolder`.
- `Task CreateFolder(...)`, `RenameFolder(...)`, `RemoveFolder(...)`.
- `Task MarkArticles(articles, statusKey, flag)`.
- `Task ImportOPML(account, url)`.
- Lifecycle: `AccountDidInitialize`, `AccountWillBeDeleted`.

### 12.3 LocalAccountDelegate

- `LocalAccountRefresher` — batches feed URLs into `n=6` concurrent downloads, uses
  `HTTPConditionalGetInfo` from DB to short-circuit unchanged feeds, calls
  `FeedParser.Parse`, writes to DB.
- `InitialFeedDownloader` — used by `CreateFeed` first run.

### 12.4 Feedbin (stretch)

All files under `Account/Feedbin/*`: `FeedbinAPICaller`,
`FeedbinAccountDelegate`, `FeedbinSubscription`, `FeedbinEntry`, `FeedbinTag`,
`FeedbinTagging`, `FeedbinStarredEntry`, `FeedbinUnreadEntry`, `FeedbinImportResult`,
`FeedbinDate`. REST endpoints over HTTPS Basic auth. Would be a ~1,500-line C# port.

### 12.5 CloudKit — **OUT OF SCOPE**

Everything under `Account/CloudKit/*` is discarded. `AccountType.CloudKit` remains as an
enum value but `AccountManager.CreateAccount(CloudKit)` throws `NotSupportedException`.

### 12.6 Tests

Port Feedbin folder/content/sync tests (when Feedbin is ported) and
`AccountSettingsImporterTests`, `FeedSettingsImporterTests`.

---

## 13. RSMarkdown → `Modules/Markdown`

Swift version vendors `md4c`. Windows port delegates to the `Markdig` NuGet package
(`Markdig.Markdown.ToHtml(string)`); public API `Markdown.ToHtml(string)` matches
`RSMarkdown.markdownToHTML`. Small enough that there is no standalone test project.

---

## 14. Mac app shell → `WinNewsWire` (WinUI)

### 14.1 App lifecycle (`Mac/AppDelegate.swift`, `AppDefaults.swift`)

- `App.xaml.cs` wires `AccountManager.Instance`, schedules `AccountRefreshTimer` and
  `ArticleStatusSyncTimer`, migrates settings on first run.
- `AppDefaults.cs` — typed wrapper over `ApplicationData.LocalSettings` (or
  `IConfiguration` for unpackaged). Keys are verbatim ports of `AppDefaults.Key.*`
  strings so user preferences round-trip on reinstalls.

### 14.2 Main window (`Mac/MainWindow/*`)

Three-pane layout (already present in the shell):

- **Sidebar** (`SidebarViewController`, `SidebarOutlineDataSource`,
  `SidebarTreeControllerDelegate`, `UnreadCountView`, `SidebarCell`) →
  `Views/Sidebar/*` backed by `TreeView` + `DataTemplate`. Context menu
  (`SidebarViewController+ContextualMenus.swift`) becomes a `MenuFlyout`.
- **Timeline** (`TimelineViewController`, `TimelineTableCellView`,
  `TimelineCellLayout`, `TimelineCellData`, `UnreadIndicatorView`,
  `ArticleSorter`, `FetchRequestQueue`) → `Views/Timeline/*` backed by
  `ListView` + a custom `TimelineItemTemplate`. `ArticlePasteboardWriter` → drag/drop
  via `DataPackage`.
- **Detail** (`DetailViewController`, `DetailWebView`, `DetailWebViewController`,
  `DetailIconSchemeHandler`, `ArticleExtractorButton`, `SharingServiceDelegate`) →
  `Views/Detail/*` using `WebView2` with a custom URL scheme handler for inline
  icon/image resources. Article HTML rendered via `Shared/Article Rendering/template.html`
  and CSS (copied into `Assets/ArticleRendering/`).
- **Article rendering** — port `ArticleRenderer` (template substitution) and ship the
  existing `core.css`, `stylesheet.css`, `main.js`, `newsfoot.js`, `template.html`
  unchanged under `Assets/ArticleRendering/`.

### 14.3 Sheets & dialogs

- `AddFeedWindowController` → `AddFeedDialog.xaml` `ContentDialog`.
- `AddFolderWindowController` → `AddFolderDialog.xaml`.
- `RenameWindowController` → `RenameDialog.xaml`.
- `ImportOPMLWindowController` / `ExportOPMLWindowController` → file pickers only.
- `SidebarDeleteItemsAlert` → `ContentDialog` confirmation.

### 14.4 Preferences (`Mac/Preferences/*`)

- `PreferencesWindowController` → `PreferencesWindow.xaml` using `NavigationView`.
- **General**: default browser, refresh interval, open-in-app, article theme.
- **Accounts**: list of configured accounts with add/remove. Add sheet offers On My PC /
  Feedbin (if ported). CloudKit / Feedly / NewsBlur / ReaderAPI entries show
  "Not available on Windows" rows that are disabled.
- **Advanced**: enable crash reporting (n/a), export diagnostics, clear caches.

### 14.5 Inspector

`Inspector` window follows the feed/folder/smart-feed selection.

- `FeedInspectorViewController` → `FeedInspectorPage.xaml`: name, home page URL,
  feed URL, icon, notification toggle.
- `FolderInspectorViewController` → `FolderInspectorPage.xaml`: name.
- `BuiltinSmartFeedInspectorViewController` → read-only pane describing built-ins.
- `NothingInspectorViewController` → "Nothing selected" placeholder.

### 14.6 About / ErrorLog / Keyboard

- About window → `AboutWindow.xaml` showing version, credits (port of `Credits.rtf`
  content as plain markdown) and link list (`LinkLabel`, `LinksTextView` → `HyperlinkButton`).
- Error log → `ErrorLogWindow.xaml` bound to `ErrorLogDatabase.FetchAll()`.
- Keyboard shortcuts (`Resources/*KeyboardShortcuts.plist`) → `Input/*Shortcuts.xml`
  files loaded at startup and bound via `KeyboardAccelerator`s.

### 14.7 Smart feeds (`Shared/SmartFeeds`)

`Today`, `All Unread`, `Starred` already exist in the shell; port the
`PseudoFeed`/`SmartFeed`/`SmartFeedDelegate` abstraction so Inspector, keyboard and
context menus can treat smart feeds uniformly. `Search` smart feed backed by FTS query.

### 14.8 Favicons (`Shared/Favicons/*`, `Shared/Images/*`)

`FaviconDownloader` + `SingleFaviconDownloader` + `FeedIconDownloader` + `ImageDownloader`
+ `ColorHash`. Port the downloader pipeline; icons are cached in
`%LocalAppData%/WinNewsWire/Favicons/`. `ColorHash` generates a stable color from a feed
URL for the placeholder when no favicon is available.

### 14.9 Article extractor (reader mode)

`Shared/Article Extractor/*` delegates to Mercury Parser's Postlight server in the Mac
version. Windows port will call the same public endpoint
(`https://uptime-mercury-api.azurewebsites.net/parser?url=...`) and render the returned
`content` HTML through the same `ArticleRenderer` template.

### 14.10 Timers

- `AccountRefreshTimer` — drives periodic refresh on a user-selected interval.
- `ArticleStatusSyncTimer` — flushes pending sync to remote accounts.
- `RefreshInterval` enum (`Manual`, `Every10Min`, `Every30Min`, `Hourly`,
  `Every4Hours`, `Every8Hours`, `Daily`).

---

## 15. Out-of-scope (explicitly dropped)

Per user direction, none of the following are ported:

- `iOS/` — entire iOS app (~30 kLOC).
- `Widget/` — iOS/macOS widget extension.
- `Intents/` — Siri/Shortcuts intents.
- `Mac/SafariExtension/` — Safari "Subscribe to feed" extension.
- `Mac/ShareExtension/` + `Shared/ShareExtension/` — share extension.
- `Mac/Scripting/` + `NetNewsWire.sdef` — AppleScript scripting.
- `Mac/CloudKitStats/` + `Modules/Account/Sources/Account/CloudKit/` +
  `Modules/CloudKitSync/` — CloudKit sync.
- `Mac/CrashReporter/` — Sparkle-only.
- `Appcasts/`, `AppleScript/`, `AppStore/`, `buildscripts/` — build/release infra.
- `Shared/Activity`, `Shared/UserNotifications` — macOS Handoff / `UNUserNotification`
  (a WinUI `AppNotification` equivalent could be added later but is not in scope now).
