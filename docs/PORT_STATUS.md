# WinNewsWire port — status and remaining work

Companion to `PORT_SPEC.md`. Tracks what is done in this session, what is in progress,
and what remains. The user requested an autonomous pass that replicates as much Mac
NetNewsWire functionality as possible in the given environment, plus a detailed
description of what is left.

Source size reference (Mac-only scope, Swift + ObjC):

| Subsystem | Files | Lines |
|---|---:|---:|
| Modules/Account (all backends) | 147 | 19,013 |
| Modules/Account (LocalAccount + core, minus CloudKit/Feedly/NewsBlur/ReaderAPI) | ~30 | ~3,500 |
| Modules/Articles | 5 | 316 |
| Modules/ArticlesDatabase | 14 | 2,358 |
| Modules/ErrorLog | 6 | 293 |
| Modules/FeedFinder | 5 | 360 |
| Modules/RSCore (non-AppKit subset) | ~55 | ~2,800 |
| Modules/RSDatabase | 26 | 4,427 |
| Modules/RSMarkdown | 3 | 258 |
| Modules/RSParser | 77 | 5,890 |
| Modules/RSTree | 7 | 367 |
| Modules/RSWeb | 30 | 2,053 |
| Modules/Secrets | 3 | 265 |
| Modules/SyncDatabase | 5 | 302 |
| Mac/** (excluding CloudKit/Safari/Share/Scripting/CrashReporter) | ~90 | ~9,000 |
| Shared/** (excluding ShareExtension/Widget/Activity) | ~60 | ~5,500 |
| **Total Mac-scoped port target** | **~520** | **~37,000** |

That total excludes CloudKit, Feedly, NewsBlur, ReaderAPI, iOS, Safari ext, Share ext,
scripting, widget, and crash reporter, which are out-of-scope per the user.

---

## A. Status legend

- ✅ Implemented and building in this repo.
- 🟨 Partially implemented (interface exists; behaviour incomplete).
- 🔲 Not yet implemented — spec available in `PORT_SPEC.md`.
- ⛔ Explicitly out of scope.

---

## A.1 Current test-suite totals

Per-project `dotnet test <csproj> --nologo` runs at the time of this
write-up:

| Test project | Passing |
|---|---:|
| `Tests/Parsers.Tests` | 126 |
| `Tests/Core.Tests` | 4 |
| `Tests/AppShared.Tests` | 17 |
| `Tests/Web.Tests` | 31 |
| `Tests/RemoteAccounts.Tests` | 23 |
| `Tests/ArticlesDatabase.Tests` | 18 |
| `Tests/ErrorLog.Tests` | 6 |
| **Total** | **225** |

`dotnet test WinNewsWire.sln` still fails up-front with
`MSB5004: The solution file has two projects named "WinNewsWire"` — the
shell app project and the solution share a name, so the aggregate command
cannot be used. Run each test csproj individually (or script it) until
one of the two `WinNewsWire` projects is renamed.

---

## B. Pre-existing shell (already in the repo before this session)

- ✅ `WinNewsWire.csproj` WinUI 3 shell with three-pane `MainContent.xaml`.
- ✅ Minimal models (`Feed`, `FeedItem`, `FeedFolder`, `SidebarItem`).
- ✅ `RssFeedService` using `System.ServiceModel.Syndication` (basic RSS/Atom,
  no JSON Feed, no OPML, no conditional GET, no proper date parsing).
- ✅ `FeedStorageService` — JSON-file subscriptions + read-state (placeholder for the
  real `Account` module).
- ✅ Value converters (`BoolToVisibility`, `UnreadCountToVisibility`, `RelativeTime`,
  `IsReadToUnreadDot`, `StarredToGlyph`).
- ✅ `MainViewModel` — load/refresh/filter/select/mark-read flow.
- ✅ Sidebar / timeline / detail layout, search box, WebView2 article rendering with
  light+dark CSS.
- ✅ SQLite scaffolding (`ArticlesDatabase.cs`, `SyncDatabase.cs`, `FeedSettingsDatabase.cs`,
  `ErrorLogDatabase.cs`, `DatabaseManager.cs`) — table shells only, not wired.

This shell is preserved and will be progressively replaced by proper ported modules.

---

## C. This-session progress (aligned to `PORT_SPEC.md` sections)

The passes below are executed in order; earlier passes block later ones.

### Pass 1 — Parsers (`Modules/Parsers`) — ✅ complete

Spec: `PORT_SPEC.md` §1.

Delivered in this session:

- ✅ Project `WinNewsWire/Modules/Parsers/Parsers.csproj` created and added to
  `WinNewsWire.sln`. Targets `net8.0`, references `HtmlAgilityPack 1.11.67`.
- ✅ `ParserData`, `FeedType`, `ParsedFeed`, `ParsedItem`, `ParsedAuthor`,
  `ParsedAttachment`, `ParsedHub`, `FeedParserException`.
- ✅ `FeedTypeDetector.Detect(ParserData)` + byte-level probes in
  `Internal/DataProbes.cs` (UTF-8 BOM skip, allocation-free ASCII search).
- ✅ `JsonFeedParser`, `RssInJsonParser` — System.Text.Json based; honours
  kottke/pxlnv/macstories/macobserver entity-decoding special cases.
- ✅ `RssParser`, `AtomParser` on `XmlReader` — covers RSS 2.0 + RDF + Atom 1.0;
  `dc:creator`, `dc:date`, `content:encoded`, `source:markdown`, `media:thumbnail`,
  `enclosure`, `link rel=alternate|enclosure|hub|next`, `content type=html|xhtml|text`,
  `author { name, uri, email }`.
- ✅ `OpmlParser`, `OpmlDocument`, `OpmlItem`, `OpmlFeedSpecifier`.
- ✅ `HtmlMetadataParser` + `HtmlMetadata` + `HtmlLinkParser` + `HtmlLink`
  using HtmlAgilityPack.
- ✅ `Utilities/DateParser.cs` — RFC 822 + ISO 8601 with the 96-entry timezone
  abbreviation table, tolerant GIGO paths, two-digit-year → 21st century, and
  millisecond support.
- ✅ `FeedParser.Parse(ParserData)` orchestrator.
- ✅ Test project `Tests/Parsers.Tests` — xUnit. 50 original resource feeds
  copied from `NetNewsWire/Modules/RSParser/Tests/RSParserTests/Resources`.
- ✅ Ported `RSDateParserTests` (all 25 assertions) and `FeedParserTypeTests`
  (all detection cases, including `allthis-partial` unknown path).
- ✅ End-to-end smoke tests parse DaringFireball Atom, scriptingNews RSS,
  inessential JSON Feed, and ScriptingNews RSS-in-JSON.
- ✅ `AtomParserTests`, `RssParserTests`, `JsonFeedParserTests`,
  `RssInJsonParserTests`, `OpmlTests`, `HtmlMetadataTests`, `HtmlLinkTests`,
  `EntityDecodingTests` all ported.
- ✅ **126/126 tests pass in `Tests/Parsers.Tests`.**

### Pass 2 — Core / Database / Web — 🟨

Spec: `PORT_SPEC.md` §§ 2–4.

Delivered:

- ✅ `Core` project with `AppConfig`, `AppDefaults`, `Extensions`, `CoalescingQueue`,
  `MimeType`, `RelativeDateFormatter`, `Interfaces` (`IDisplayNameProvider`,
  `IRenamable`, `IOpmlRepresentable`, `IUnreadCountProvider`).
- ✅ `Database` project with `DatabaseQueue`, `SqliteExtensions`.
- ✅ `Web` project with `Downloader` (single-shot GET), `ConditionalGetInfo`,
  `HtmlMetadataDownloader`, `UserAgent`.
- ✅ `DownloadSession` — full concurrent download pipeline with:
  - Delegate-based callback pattern (`IDownloadSessionDelegate`)
  - Per-host concurrency limit (1) via `SocketsHttpHandler`
  - Pending/overflow queue (500-cap) matching NNW behaviour
  - HTTP 429 per-host retry-after tracking
  - 4xx response caching (53-hour expiry)
  - Redirect caching with captive-portal detection
  - In-memory download cache (13-minute TTL)
  - OpenRSS.org rate-limiting (one feed per refresh session)
  - Special-case user agent for rachelbythebay.com, openrss.org
  - Cache-Control max-age honouring
  - Progress reporting via `ProgressInfo`

- ✅ `BinaryDiskCache` — on-disk key-value cache with SHA256 key hashing, atomic writes.
- ✅ `NetworkMonitor` — connectivity detection via `NetworkChange.NetworkAvailabilityChanged`.
- ✅ `Markdown` module — thin Markdig wrapper (`Markdown.ToHtml()`).

Remaining from these modules:

- 🔲 `PersistentCache`, `OrderedSet<T>`.
- 🔲 `DatabaseObject` generic protocol/extensions (FMDB-centric code — significant
  translation work).
- 🔲 `OPMLFile` read-watch logic.
- 🔲 `MainThreadOperationQueue`.
- 🔲 Full `RSCore` utility coverage: `BatchUpdate`, `Cache<T>`, `MemoryPressureMonitor`,
  `MacroProcessor`, `UndoableCommand`, `IsoCountries`.

### Pass 3 — Articles / ArticlesDatabase / SyncDatabase — 🟨

Spec: `PORT_SPEC.md` §§ 5–7.

Delivered:

- ✅ `Articles` project with `Article`, `ArticleStatus`, `Author`, `Attachment`, `DatabaseID` records.
- ✅ `ArticlesDatabase` — `ArticlesDatabaseStore` with SQLite schema, CRUD,
  FTS5 search, update/fetch/mark methods, attachment tables.
- ✅ `SyncDatabase` — `SyncDatabaseStore` (~115 LOC) with pending-status queue,
  select/insert/delete operations.

Remaining:

- ✅ `ArticlesDatabase.Tests` xUnit project (19/19 passing).
- ✅ `Attachment` model with DB persistence (attachments + attachmentsLookup tables).
- ✅ Background retention cleanup (`CleanupOldArticlesAsync`, 90-day default).
- 🔲 FTS5 search wiring into the timeline search box.
- 🔲 Article change notifications (typed C# `event`s).
- 🔲 FTS5 search wiring into the timeline search box.
- 🔲 Article change notifications (typed C# `event`s).

### Pass 4 — FeedFinder / Secrets / ErrorLog — ✅ complete

Spec: `PORT_SPEC.md` §§ 8, 10, 11.

Delivered:

- ✅ `FeedFinder` (~92 LOC) with HTML-link discovery, fallback paths, scoring
  (`FeedFinder`, `FeedSpecifier`, `HtmlFeedFinder`).
- ✅ `Secrets.CredentialsManager` (~67 LOC) using DPAPI-encrypted file storage.
- ✅ `ErrorLog` (~74 LOC) with SQLite table, insert/fetch/clear.

Remaining:

- ✅ `ErrorLog.Tests` xUnit project (6/6 passing).
- ✅ `ErrorLogNotification` events (`EntryAdded` event on `ErrorLogDatabase`).
- 🔲 Secrets: `PasswordVault` (WinRT) as primary, DPAPI as fallback.
- 🔲 Secrets: `SecretKey` (bundled third-party API keys).

### Pass 5 — Account (LocalAccount) — 🟨

Spec: `PORT_SPEC.md` §12.

Delivered:

- ✅ `Account` (~221 LOC), `AccountType`, `AccountManager` (~107 LOC).
- ✅ `Feed`, `Folder`, `IAccountDelegate`.
- ✅ `LocalAccountDelegate` with feed discovery, refresh via `DownloadSession`.
- ✅ `LocalAccountRefresher` implementing `IDownloadSessionDelegate` with:
  - 29-minute minimum between checks (25-hour for OpenRSS/rachelbythebay)
  - 8-day conditional GET reset
  - Content-hash (MD5) dedup
  - Bad host filtering (twitter.com, x.com)
  - Cache-Control skip with 5-hour max clamp
  - Image magic-byte early cancel
  - HTTP error logging
- ✅ `RemoteSyncHelpers`.
- ✅ `OPMLNormalizer` — deduplicates/normalizes imported OPML.
- ✅ `CombinedRefreshProgress` — aggregated progress from multiple accounts.

Remaining:

- 🔲 `Container` protocol, `ContainerIdentifier`, `SidebarItemIdentifier`.
- 🔲 `ContainerPath`.
- 🔲 `UnreadCountProvider` protocol (interface exists in Core but not fully wired).
- 🔲 `OPMLFile` (auto-save/watch).
- 🔲 `AccountSettingsDatabase`, `AccountSettings`, `AccountSettingsImporter`.
- 🔲 `FeedSettings`, `FeedSettingsDatabase`, `FeedSettingsImporter`.
- 🔲 `AccountBehaviors` (capability flags per account type).
- 🔲 `SingleArticleFetcher`, `ArticleFetcher` protocol.
- 🔲 Replace current `FeedStorageService` usage in the shell with `AccountManager`.

Remote backends (projects are on disk, added to the solution, and wired into
`WinNewsWire.csproj` as `ProjectReference`s):

- 🟨 Feedbin (`Modules/Feedbin` — delegate + API caller + models, in `.sln`).
- 🟨 Feedly (`Modules/Feedly` — delegate + API caller + OAuth + models, in `.sln`).
- 🟨 NewsBlur (`Modules/NewsBlur` — delegate + API caller + models, in `.sln`).
- 🟨 ReaderAPI (`Modules/ReaderAPI` — delegate + API caller + models + variants, in `.sln`).
- ⛔ CloudKit.

### Pass 6 — Tree — ✅ complete

Spec: `PORT_SPEC.md` §9. Port delivered (~111 LOC): `Node`, `TreeController`.

### Pass 7 — UI parity — 🟨

Spec: `PORT_SPEC.md` §14.

Delivered:

- ✅ Three-pane `MainContent.xaml` layout (sidebar + timeline + detail).
- ✅ `MainWindow.xaml` with menu bar, search, app chrome.
- ✅ `MainViewModel` (~566 LOC) — sidebar tree, feed/article actions, rendering, filtering.
- ✅ `AddFeedDialog`, `AddFolderDialog`, `InspectorDialog`, `AddAccountDialog`,
  `OpmlCommands` (OPML import/export via file picker).
- ✅ `PreferencesWindow.xaml` — refresh interval, text size, grouping, accounts tab.
- ✅ `AboutWindow.xaml` — app info/version.
- ✅ `ErrorLogWindow.xaml` — error list with refresh/clear.
- ✅ `KeyboardShortcutsWindow.xaml` — keyboard reference window.
- ✅ Value converters (BoolToVisibility, UnreadCount, RelativeTime, StarredGlyph, etc.).
- ✅ `AppService.cs` — app bootstrap, timer wiring, error log, credential resolution.
- ✅ WebView2 article rendering with light+dark CSS.

Remaining:

- 🔲 Drag-and-drop of feeds between folders (Pasteboard → `DataPackage`).
- 🔲 Full sidebar status bar, timeline status bar.
- 🔲 Detail status bar with link hover preview.
- 🔲 Per-article theme switching + downloadable themes (`.nnwtheme` bundles).
- 🔲 Sharing (`NSSharingService`) — Windows `DataTransferManager` equivalent.
- 🔲 Unified window mode (`UnifiedWindow.storyboard`).
- 🔲 About window with animated credits.
- 🔲 NNW3 import (old NetNewsWire 3 OPML+plist bundle import) — niche.
- 🔲 "Send to MarsEdit" / "Send to Micro.blog" extension points — Mac-only IPC.

### Pass 8 — Polish — 🟨

Delivered:

- ✅ `FaviconDownloader` + `ColorHash` (~60 LOC each).
- ✅ `ArticleStringFormatter` (relative-time formatting).
- ✅ `AccountRefreshTimer` + `ArticleStatusSyncTimer` + `RefreshInterval`.
- ✅ `ArticleSorter`.
- ✅ `SmartFeedsController` + `SmartFeed` + `PseudoFeed`.
- ✅ `ArticleExtractor`.
- ✅ `ArticleRenderer` (template substitution with bundled HTML/CSS/JS assets).
- ✅ `DeleteCommand` + `MarkStatusCommand` (undo/redo).
- ✅ `OpmlExporter` + `DefaultFeedsImporter` + `Nnw3Importer`.

Remaining:

- 🔲 `FeedIconDownloader` (larger feed icons, not just favicons).
- 🔲 `FaviconGenerator` (placeholder generation from feed name/color).
- 🔲 Migration: read user's existing `feeds.json` / `state.json` into the new DB.
- 🔲 `CacheCleaner` (periodic cleanup of downloaded images/favicons).
- 🔲 `IconImage` / `SmallIconProvider` (unified icon abstraction).
- 🔲 Undo/redo manager wiring (commands exist but no `UndoManager` equivalent).

### Pass 9 — Remote backends — 🟨

Projects are present on disk, listed in `WinNewsWire.sln`, and referenced
from `WinNewsWire/WinNewsWire.csproj`. A shared `Tests/RemoteAccounts.Tests`
xUnit project covers all four (23/23 passing):

- 🟨 Feedbin (`Modules/Feedbin/` — `FeedbinAccountDelegate`, `FeedbinAPICaller`,
  `FeedbinModels`; in `.sln`).
- 🟨 Feedly (`Modules/Feedly/` — `FeedlyAccountDelegate`, `FeedlyAPICaller`,
  `FeedlyBrowserAuth`, `FeedlyModels`, OAuth types; in `.sln`).
- 🟨 NewsBlur (`Modules/NewsBlur/` — `NewsBlurAccountDelegate`, `NewsBlurAPICaller`,
  `NewsBlurModels`; in `.sln`).
- 🟨 ReaderAPI (`Modules/ReaderAPI/` — `ReaderAPIAccountDelegate`, `ReaderAPICaller`,
  `ReaderAPIModels`, `ReaderAPIVariant`; in `.sln`).

### Pass 10 — Remaining gaps — 🔲

Modules:

- ✅ `Markdown` module — `Modules/Markdown/Markdown.csproj`, thin Markdig wrapper.
- ✅ `NetworkMonitor` — connectivity detection in Web module.
- ✅ `BinaryDiskCache` — on-disk LRU cache in Core module.
- 🔲 `ArticleTheme` / `ArticleThemesManager` (theme system for `.nnwtheme` bundles).
- 🔲 `UserNotificationManager` (Windows toast notifications).

Account model:

- 🔲 `Container` protocol, `ContainerIdentifier`, `SidebarItemIdentifier`.
- 🔲 `OPMLFile` auto-save/watch.
- 🔲 `AccountSettingsDatabase`, `FeedSettingsDatabase`, importers.
- 🔲 `SingleArticleFetcher`, `ArticleFetcher` protocol.

UI:

- 🔲 Article theme switching + downloadable themes.
- 🔲 Reader mode toggle button with state management.
- 🔲 Sidebar/timeline/detail status bars.
- 🔲 Drag-and-drop of feeds between folders.
- 🔲 Sharing (`DataTransferManager`).
- 🔲 Unified single-pane window mode.
- 🔲 Full keyboard shortcut mapping (all 4 NNW plist files).
- 🔲 Per-backend account setup sheets (Feedbin login, Feedly OAuth, etc.).
- 🔲 Context menus on sidebar and timeline.
- 🔲 Inline sidebar rename.
- 🔲 Sidebar delete confirmation dialog.
- 🔲 About window with animated credits.

Tests:

- ✅ `EntityDecodingTests` (ported, included in Parsers.Tests' 126).
- ✅ Deeper parser test assertions (Atom/RSS/JSONFeed/OPML item-level fields)
  ported into `Tests/Parsers.Tests`.
- ✅ `ArticlesDatabase.Tests` (18/18 passing).
- ✅ `ErrorLog.Tests` (6/6 passing).
- 🔲 `Account.Tests` (AccountSettingsImporter, FeedSettingsImporter).

---

## D. Honest assessment — what a single autonomous session can reach

A literal line-for-line port of ~37,000 lines of Mac-scoped Swift is not possible in a
single working session; a team should expect multiple engineer-months for a
production-quality port.

**What can be delivered to a good quality bar in this session**:

1. `Modules/Parsers` (RSS, Atom, JSON Feed, RSS-in-JSON, OPML, HTML metadata, date
   parsing) with the original test corpus ported to xUnit and passing. This is the
   highest-leverage subsystem — everything else depends on it.
2. `Modules/Articles` + `Modules/ArticlesDatabase` enough to store, fetch, search, mark
   read/starred, and produce unread counts.
3. `Modules/FeedFinder` + `Modules/Web` (conditional-GET download) so the app can
   discover feeds and skip unchanged ones.
4. `Modules/Account` with a working `LocalAccount` that replaces the current
   `FeedStorageService`.
5. Shell-level wiring: Add Feed / Add Folder / Rename dialogs, OPML import/export,
   context menus, keyboard shortcuts matching the `.plist` files.

**What will remain after this session**:

- All remote account backends (Feedbin, Feedly, NewsBlur, ReaderAPI family).
- Full Preferences / Inspector / About / ErrorLog / Unified-window parity with the Mac
  storyboards.
- Favicon downloader, article-theme support, reader mode, drag-and-drop, share
  sheet, Windows notifications.
- Sidebar/timeline/detail feature details: pasteboard writers, per-row tooltips,
  tooltip-delayed hover previews, keyboard chord support, list-style prefs.
- Port of `RSDatabase`'s full FMDB abstraction — only the subset that the
  `ArticlesDatabase` needs is delivered.
- Import of NNW3 bundles, "Send to MarsEdit/Micro.blog", MarsEdit URL scheme handlers.
- Settings migration from legacy `UserDefaults` plists.

**Permanently out of scope (per user instructions)**:

- iOS app, widget, Siri intents.
- Safari extension, Share extension.
- CloudKit sync (no cross-platform SDK).
- AppleScript scripting + `.sdef` definitions.
- Sparkle auto-update + crash reporter.

---

## E. Where to pick this up

1. Open `WinNewsWire.sln`. The new `Modules/*` projects are added as class libraries.
2. `Modules/Parsers` is the foundation; anything else that needs to parse a feed or
   OPML goes through it.
3. `AccountManager` (once delivered) is the façade the UI should use. New UI code
   should not reach into `FeedStorageService` directly; that class is scheduled for
   deletion once `Account` lands.
4. Every ported test project uses xUnit; resource files live next to tests and are
   copied to the output directory. Run `dotnet test` from the solution root.
5. The `CLAUDE.md` in the original repo, and `Technotes/` folder, are good references
   for NetNewsWire's architectural conventions; they map 1:1 onto the modules above.

---

## F. Conventions adopted during the port

- Swift `Set<T>` → `IReadOnlySet<T>` / `HashSet<T>`; hashability of records relies on
  synthesized C# `record` equality (fields match Swift `hash(into:)` semantics).
- Swift async throws → `Task<T>`; Swift completion-handler APIs are not replicated;
  only the `async` overloads survive.
- Swift `NotificationCenter.default.post` → typed `event`s on the relevant manager
  singleton (e.g. `AccountManager.AccountsDidChange`).
- Swift `Date` (epoch-seconds under the hood) → `DateTime` stored as UTC
  `DateTimeKind.Utc`; DB serialization uses Unix seconds to match the Swift schema.
- Swift `URL` → `System.Uri` for absolute URLs; `string` for feed URLs (to round-trip
  exact characters as Swift does).
- Mac `NotificationCenter`/`NSWorkspace` desktop launches → `Launcher.LaunchUriAsync`.
- Mac pasteboard types → WinUI `DataPackage` with custom format ids
  (`application/x-winnewswire-feed`, `application/x-winnewswire-folder`,
  `application/x-winnewswire-article`).

---

## G. Comprehensive file-by-file coverage audit

This section enumerates **every folder in `C:\Users\qinjo\source\repos\NetNewsWire`**
(Mac-scoped, per the user's narrowing). For each item it states the port status and
— if not done — the required C# equivalents and notes. `LOC` counts are approximate.

Legend: ✅ done this session · 🟨 partial · 🔲 not started (spec exists) · ⛔ out of scope.

### G.1 `Modules/RSParser` → `WinNewsWire/Modules/Parsers` — ✅
~5,900 LOC. Delivered in full (see §C Pass 1). 53/53 xUnit tests pass.

### G.2 `Modules/RSCore` → `WinNewsWire/Modules/Core` — 🔲
~2,800 LOC Swift + Obj-C + C. File-by-file target:

| Swift file | Windows/C# target |
|---|---|
| `AppConfig.swift` | `AppConfig.cs` — paths via `ApplicationData.Current.LocalFolder` on packaged, `%APPDATA%` fallback unpackaged. |
| `AppNotifications.swift` | `AppNotifications.cs` — typed `event`/`EventHandler` replacements. |
| `Array+RSCore.swift` | `ArrayExtensions.cs`. |
| `BatchUpdate.swift` | `BatchUpdate.cs` — scoped updates coalescer. |
| `BinaryDiskCache.swift` | `BinaryDiskCache.cs` — on-disk LRU. |
| `Blocks.swift` | omit (delegates are native in C#). |
| `Bundle+RSCore.swift` | merge into `ResourceHelper.cs`. |
| `Cache.swift` | `Cache.cs` — in-memory LRU. |
| `Calendar+RSCore.swift` | `CalendarExtensions.cs`. |
| `CGImage+RSCore.swift` | ⛔ AppKit-bound — replace with `SoftwareBitmap` helpers where needed. |
| `CoalescingQueue.swift` | `CoalescingQueue.cs` — debouncer. |
| `Data+RSCore.swift` | merge into `ByteArrayExtensions.cs` (MD5/SHA helpers). |
| `Date+RSCore.swift` | `DateExtensions.cs`. |
| `DisplayNameProvider.swift` | `IDisplayNameProvider.cs` — interface. |
| `FileManager+RSCore.swift` | `FileUtilities.cs`. |
| `Geometry.swift` | omit (AppKit geometry). |
| `MacroProcessor.swift` | `MacroProcessor.cs` — simple template substitution (used by article renderer). |
| `MainThreadBlockOperation.swift`, `MainThreadOperation.swift`, `MainThreadOperationQueue.swift` | `DispatcherQueueExtensions.cs` + `MainThreadOperationQueue.cs`. UI thread work coalescer. |
| `MemoryPressureMonitor.swift` | `MemoryPressureMonitor.cs` — use `MemoryManager.AppMemoryUsageIncreased`. |
| `NotificationCenter+RSCore.swift` | omit; typed events replace it. |
| `OPMLRepresentable.swift` | `IOpmlRepresentable.cs` — interface. |
| `Platform.swift` | `Platform.cs` — OS/version probe. |
| `Renamable.swift` | `IRenamable.cs`. |
| `RSImage.swift` | `ImageHelpers.cs` (WinRT `BitmapImage`). |
| `RSProgress.swift` | `ProgressAggregator.cs`. |
| `RSScreen.swift` | omit (DPI via `DisplayInformation`). |
| `SendToBlogEditorApp.swift`, `SendToCommand.swift` | ⛔ Mac-only IPC (MarsEdit/Micro.blog URL scheme). Stub `ISendToCommand`. |
| `String+RSCore.swift` | `StringExtensions.cs`. |
| `UndoableCommand.swift` | `IUndoableCommand.cs` + lightweight undo stack (replaces `NSUndoManager`). |
| `UniformTypeIdentifiers+RSCore.swift` | `MimeTypeHelpers.cs`. |
| `URL+RSCore.swift` | `UriExtensions.cs`. |

**RSCoreObjC** (C/Obj-C):

| File | Windows/C# target |
|---|---|
| `RSIsoCountries.*` | `IsoCountries.cs` — static table. |
| `NSString+RSCore.m` | folded into `StringExtensions.cs`. |
| `RSDateExtras.m` | folded into `DateExtensions.cs`. |
| `NSData+RSCore.m` | folded into `ByteArrayExtensions.cs`. |
| `RSBackgroundColorView.m`, `RSClipView.m`, `RSGradientView.m`, `RSPlaceholderTextField.m` | ⛔ AppKit views. |

**RSCoreResources**: ⛔ AppKit nibs, asset catalog, localized strings. Replace with WinUI `ResourceDictionary` lookups.

### G.3 `Modules/RSDatabase` → `WinNewsWire/Modules/Database` — 🔲
~4,400 LOC. FMDB wrapper.

| Swift file | C# target (over `Microsoft.Data.Sqlite`) |
|---|---|
| `Database.swift` | `Database.cs` — connection factory. |
| `DatabaseQueue.swift` | `DatabaseQueue.cs` — serial write queue. |
| `DatabaseTable.swift` | `DatabaseTable<TKey,T>` base class. |
| `DatabaseObject.swift`, `DatabaseObjectCache.swift` | `IDatabaseObject<TKey>` + identity map. |
| `DatabaseLookupTable.swift` | `DatabaseLookupTable.cs`. |
| `DatabaseRelatedObjectsTable.swift` | `DatabaseRelatedObjectsTable.cs`. |
| `RelatedObjectIDsMap.swift`, `RelatedObjectsMap.swift` | one-to-many helpers. |
| `FMDatabase+Extras.swift`, `FMResultSet+Extras.swift` | `SqliteConnectionExtensions.cs`, `SqliteDataReaderExtensions.cs`. |

### G.4 `Modules/RSWeb` → `WinNewsWire/Modules/Web` — 🔲
~2,100 LOC. All HTTP plumbing over `HttpClient`.

| Swift file | C# target |
|---|---|
| `HTTPConditionalGetInfo.swift` | `ConditionalGetInfo.cs` — ETag/Last-Modified pairs. |
| `HTTPDateInfo.swift`, `CacheControlInfo.swift`, `HTTPLinkPagingInfo.swift` | parsers. |
| `HTTPMethod.swift`, `HTTPRequestHeader.swift`, `HTTPResponseCode.swift`, `HTTPResponseHeader.swift`, `HTTPResponse429.swift` | constants + helpers. |
| `Downloader.swift` | `Downloader.cs` — single-shot GET with cache. |
| `DownloadCache.swift`, `HTMLMetadataCache.swift` | `DownloadCache.cs`, `HtmlMetadataCache.cs`. |
| `DownloadSession.swift` | `DownloadSession.cs` — dedup + retry + conditional GET pipeline (the core of feed refresh). |
| `HTMLMetadataDownloader.swift` | `HtmlMetadataDownloader.cs` (uses our `HtmlMetadataParser`). |
| `MacWebBrowser.swift` | `WebBrowserLauncher.cs` → `Launcher.LaunchUriAsync`. |
| `MimeType.swift` | `MimeType.cs`. |
| `NetworkMonitor.swift` | `NetworkMonitor.cs` → `NetworkInformation.NetworkStatusChanged`. |
| `SpecialCases.swift` | `SpecialCases.cs`. |
| `Transport.swift`, `TransportJSON.swift` | `ITransport.cs` + `HttpClientTransport.cs`. |
| `UserAgent.swift` | `UserAgent.cs`. |
| `URL+RSWeb.swift`, `URLComponents+RSWeb.swift`, `URLRequest+RSWeb.swift`, `URLResponse+RSWeb.swift`, `Dictionary+RSWeb.swift`, `String+RSWeb.swift` | folded into above. |

### G.5 `Modules/Articles` → `WinNewsWire/Modules/Articles` — 🔲
~320 LOC.

| Swift file | C# target |
|---|---|
| `Article.swift` | `Article.cs` (record). |
| `ArticleStatus.swift` | `ArticleStatus.cs` (read/starred flags). |
| `Author.swift` | `Author.cs`. |
| `Attachment.swift` | `Attachment.cs`. |
| `ArticleSet.swift` | `ArticleSet.cs` — set-of-articles helpers. |

### G.6 `Modules/ArticlesDatabase` → `WinNewsWire/Modules/ArticlesDatabase` — 🔲
~2,400 LOC. **The biggest SQLite port after Account.** Key files:

`ArticlesDatabase.swift` (public façade), `ArticlesTable.swift`, `AuthorsTable.swift`,
`AttachmentsTable.swift`, `TagsTable.swift`, `StatusesTable.swift`, `SearchTable.swift`
(FTS5), `ArticlesDatabaseSchema.swift`, `DatabaseArticle.swift`, `DatabaseAuthor.swift`,
`ArticleSearchInfo.swift`, `FetchType.swift`, `UnreadCountDictionary.swift`.

Must implement: `Fetch*ForFeedID`, `FetchArticlesMatchingSearch`, `MarkArticles`,
`UpdateArticleStatuses`, retention cleanup, unread-count-per-feed aggregation.

### G.7 `Modules/SyncDatabase` → `WinNewsWire/Modules/SyncDatabase` — 🔲
~300 LOC. Pending-status queue for remote sync.

### G.8 `Modules/FeedFinder` → `WinNewsWire/Modules/FeedFinder` — 🔲
~360 LOC. `FeedFinder.swift` + `FeedSpecifier.swift` + associated scoring.

### G.9 `Modules/RSTree` → `WinNewsWire/Modules/Tree` — 🔲
~370 LOC. `Node`, `NodePath`, `TopLevelRepresentedObject`, `TreeController`. Skip `NSOutlineView+RSTree.swift` (replaced by WinUI `TreeView`).

### G.10 `Modules/ErrorLog` → `WinNewsWire/Modules/ErrorLog` — 🔲
~290 LOC. Port `ErrorLog.swift`, `ErrorLogEntry.swift`, `ErrorLogDatabase.swift` + tests.

### G.11 `Modules/Secrets` → `WinNewsWire/Modules/Secrets` — 🔲
~260 LOC. `CredentialsManager.swift` → `PasswordVault` / DPAPI fallback. `SecretKey.swift` (bundled third-party API keys).

### G.12 `Modules/RSMarkdown` → `WinNewsWire/Modules/Markdown` — 🔲
~260 LOC. Wraps MMMarkdown. Replace with `Markdig` (`AdvancedExtensions`).

### G.13 `Modules/Account` — detailed breakdown
The Account module is the largest single module (~19 k LOC across all backends).
Per user instruction, only **LocalAccount** and shared scaffolding are in scope;
remote backends are tracked so they can be added later.

**Account/Sources/Account** (shared, ~3,500 LOC) — 🔲 in scope:

`Account.swift`, `AccountBehaviors.swift`, `AccountDelegate.swift`, `AccountError.swift`,
`AccountManager.swift`, `AccountSettings.swift`, `AccountSettingsDatabase.swift`,
`AccountSettingsImporter.swift`, `ArticleFetcher.swift`, `CombinedRefreshProgress.swift`,
`Container.swift`, `ContainerIdentifier.swift`, `ContainerPath.swift`,
`DataExtensions.swift`, `Feed.swift`, `FeedSettings.swift`, `FeedSettingsDatabase.swift`,
`FeedSettingsImporter.swift`, `Folder.swift`, `OPMLFile.swift`, `OPMLNormalizer.swift`,
`SidebarItem.swift`, `SidebarItemIdentifier.swift`, `SingleArticleFetcher.swift`,
`UnreadCountProvider.swift`, `URLRequest+Account.swift`.

**Account/Sources/LocalAccount** (~400 LOC) — 🔲 in scope:
`InitialFeedDownloader.swift`, `LocalAccountDelegate.swift`, `LocalAccountRefresher.swift`.

**Account/Sources/Account/Feedbin** (~1,500 LOC) — 🔲 optional (Pass 9):
`Feedbin.swift`, `FeedbinAccountDelegate.swift`, `FeedbinAPICaller.swift`,
`FeedbinDate.swift`, `FeedbinEntry.swift`, `FeedbinImportResult.swift`,
`FeedbinStarredEntry.swift`, `FeedbinSubscription.swift`, `FeedbinTag.swift`,
`FeedbinTagging.swift`, `FeedbinUnreadEntry.swift`.

**Account/Sources/Account/Feedly** (~4,000 LOC across ~45 files) — 🔲 stretch:
OAuth authorization (`OAuthAccountAuthorizationOperation`, `OAuthAcessTokenRefreshing`,
`OAuthAuthorizationCodeGranting`, `OAuthAuthorizationClient+Feedly`), `FeedlyAccountDelegate`
(+OAuth), `FeedlyAPICaller`, `FeedlyModel`, `FeedlyFeedContainerValidator`,
`FeedlyMainThreadOperation*`, `DownloadProgress`, plus **24 `Operations/*.swift`**
(FeedlyAddExistingFeedOperation through FeedlyUpdateAccountFeedsWithItemsOperation)
and **5 `Services/*.swift`** (Get Collections/Entries/StreamContents/StreamIds + MarkArticles).

**Account/Sources/Account/NewsBlur** — 🔲 stretch.
The operation-based refresh pipeline is split between `Modules/NewsBlur` (API client)
and `Modules/Account/NewsBlur/NewsBlurAccountDelegate.swift` +
`Internals/NewsBlurAccountDelegate+Internal.swift`.

**Account/Sources/Account/ReaderAPI** — 🔲 stretch (covers FreshRSS, BazQux, Inoreader,
TheOldReader, FreshRSS variants):
`ReaderAPIAccountDelegate.swift`, `ReaderAPICaller.swift`, `ReaderAPIEntry.swift`,
`ReaderAPISubscription.swift`.

**Account/Sources/Account/CloudKit** — ⛔ out of scope (all 13 CloudKit files).

### G.14 `Modules/NewsBlur` — 🔲 stretch
Separate package for the NewsBlur API client (headers/models/requests). Same treatment as Feedbin.

### G.15 `Modules/CloudKitSync` — ⛔ out of scope.

---

## H. Shared/** — shared macOS glue (app-layer)

Lives outside the modules. All file counts are .swift unless noted.

### H.1 `Shared/Article Rendering` — 🔲
`ArticleRenderer.swift`, `ArticleRenderingSpecialCases.swift`, `ArticleTextSize.swift`,
`WebViewConfiguration.swift`, plus bundled web assets: `template.html`, `stylesheet.css`,
`core.css`, `main.js`, `newsfoot.js`. Port renderer to C# class that materialises
article HTML into the `WebView2` (reusing the JS/CSS verbatim).

### H.2 `Shared/ArticleStyles` — 🔲
`ArticleTheme.swift`, `ArticleTheme+Notifications.swift`, `ArticleThemeDownloader.swift`,
`ArticleThemePlist.swift`, `ArticleThemesManager.swift`. Port to `ArticleThemesManager.cs`
that loads `.nnwtheme` bundle directories (`Info.plist` → `Theme.json`) from
`%LOCALAPPDATA%\WinNewsWire\Themes\`. Theme downloading from URL.

### H.3 `Shared/Article Extractor` — 🔲
`ArticleExtractor.swift` + `ArticleExtractorButtonState.swift`. Calls Mercury Parser
endpoint (public Postlight service). Port to `ArticleExtractor.cs` using `HttpClient`.

### H.4 `Shared/SmartFeeds` — 🔲
`SmartFeed.swift`, `SmartFeedDelegate.swift`, `SmartFeedsController.swift`,
`UnreadFeed.swift`, `TodayFeedDelegate.swift`, `StarredFeedDelegate.swift`,
`PseudoFeed.swift`, `SearchFeedDelegate.swift`, `SearchTimelineFeedDelegate.swift`,
`SmartFeedPasteboardWriter.swift`. Port to C# with identical fetch semantics.

### H.5 `Shared/Commands` — 🔲
`DeleteCommand.swift`, `MarkStatusCommand.swift`, `MarkCommandValidationStatus.swift`.
Back these by a lightweight undo/redo stack (replaces `NSUndoManager`).

### H.6 `Shared/Activity` — ⛔ mostly (Handoff/NSUserActivity). Keep `ActivityType`
enum; no-op the rest.

### H.7 `Shared/Favicons` — 🔲
`FaviconDownloader.swift`, `FaviconGenerator.swift`, `SingleFaviconDownloader.swift`,
`ColorHash.swift`. Port to C# + WinRT `BitmapImage`; cache under `LocalFolder`.

### H.8 `Shared/Tree` — 🔲
`FolderTreeControllerDelegate.swift`, `SidebarTreeControllerDelegate.swift`.
Glue between `Modules/Tree` and the sidebar outline.

### H.9 `Shared/Importers` — 🔲
`DefaultFeedsImporter.swift` + `DefaultFeeds.opml`. Runs once on first launch;
ported 1:1 including the bundled OPML.

### H.10 `Shared/Exporters` — 🔲
`OPMLExporter.swift` — called by the "Export Subscriptions…" menu.

### H.11 `Shared/ExtensionPoints` — ⛔/🔲
`SendToMarsEditCommand.swift`, `SendToMicroBlogCommand.swift`. Both rely on Mac URL-scheme
IPC. Stub for Windows; keep interface so we can add Windows targets (e.g. Open Live Writer,
WordPress) later.

### H.12 `Shared/Extensions` — 🔲
`AddFeedDefaultContainer.swift`, `ArticleStringFormatter.swift` (relative time format!),
`ArticleUtilities.swift`, `CacheCleaner.swift`, `IconImage.swift`,
`Node+Extensions.swift`, `NSAttributedString+Extensions.swift` → `RichTextHelpers.cs`,
`RSImage+Extensions.swift`, `SmallIconProvider.swift`.

### H.13 `Shared/Timer` — 🔲
`AccountRefreshTimer.swift`, `ArticleStatusSyncTimer.swift`, `RefreshInterval.swift`.
Port to `System.Threading.Timer` / `DispatcherTimer`.

### H.14 `Shared/Timeline` (model) — 🔲
`ArticleArray.swift`, `ArticleSorter.swift`, `FetchRequestOperation.swift`,
`FetchRequestQueue.swift`. The actual timeline data pipeline.

### H.15 `Shared/UserNotifications` — 🔲
`UserNotificationManager.swift`. Port to `ToastNotificationManager` (`Microsoft.Toolkit.Uwp.Notifications`).

### H.16 `Shared/Widget` — ⛔ iOS/macOS WidgetKit. Not applicable on Windows.

### H.17 `Shared/Settings`, `Shared/Resources`, `Shared/Images` — 🟨
Images will be replaced by WinUI asset bundle. Settings defaults → `ApplicationData.LocalSettings`.

### H.18 `Shared/ShareExtension` — ⛔ out of scope.

---

## I. Mac/** — the Mac app itself

### I.1 `Mac/MainWindow` (57 files) — 🔲
This is the bulk of the UI port. Broken out:

- **AddFeed**: `AddFeedController.swift`, `AddFeedWindowController.swift`,
  `FolderTreeMenu.swift` → `AddFeedDialog.xaml` + folder picker flyout.
- **AddFolder**: `AddFolderWindowController.swift` → `AddFolderDialog.xaml`.
- **Sidebar** (10 files): `SidebarViewController.swift`,
  `SidebarViewController+ContextualMenus.swift`, `SidebarOutlineView.swift`,
  `SidebarOutlineDataSource.swift`, `SidebarStatusBarView.swift`,
  `SidebarDeleteItemsAlert.swift`, `SidebarWindowState.swift`,
  `UnreadCountView.swift`, `PasteboardFeed.swift`, `PasteboardFolder.swift`,
  `Sidebar/Cell/*`, `Sidebar/Keyboard/*`, `Sidebar/Renaming/*`
  → `SidebarView.xaml` + `SidebarViewModel` + `MenuFlyout` context menus + inline
  rename + drag-drop writers for the custom clipboard formats.
- **Timeline** (9 files): `TimelineViewController.swift`,
  `TimelineViewController+ContextualMenus.swift`, `TimelineTableView.swift`,
  `TimelineTableRowView.swift`, `TimelineContainerView.swift`,
  `TimelineContainerViewController.swift`, `TimelineWindowState.swift`,
  `ArticlePasteboardWriter.swift`, `TimelineTableView.xib`, `Timeline/Cell/*`,
  `Timeline/Keyboard/*` → `TimelineView.xaml` + `TimelineItemTemplate.xaml`.
- **Detail** (9 files): `DetailViewController.swift`, `DetailWebViewController.swift`,
  `DetailWebView.swift`, `DetailContainerView.swift`, `DetailIconSchemeHandler.swift`
  (custom scheme for inline favicons — map onto `WebView2`'s
  `CoreWebView2.WebResourceRequested`), `DetailStatusBarView.swift`,
  `DetailWindowState.swift`, `blank.html`, `page.html`, `main_mac.js`,
  `Detail/Keyboard/*`.
- **Keyboard**: `MainWindowKeyboardHandler.swift` + all `Timeline/Keyboard/*`,
  `Sidebar/Keyboard/*`, `Detail/Keyboard/*` — port the four shortcut plist files
  (`GlobalKeyboardShortcuts.plist`, `DetailKeyboardShortcuts.plist`,
  `SidebarKeyboardShortcuts.plist`, `TimelineKeyboardShortcuts.plist`) to a single
  `KeyboardShortcuts.json` consumed by a `KeyboardAcceleratorRegistry`.
- **OPML**: `ImportOPMLWindowController.swift`, `ExportOPMLWindowController.swift`,
  `ImportOPMLSheet.xib`, `ExportOPMLSheet.xib` → `ImportOpmlDialog.xaml`,
  `ExportOpmlDialog.xaml` + `FileOpenPicker`/`FileSavePicker`.
- **NNW3**: `NNW3Document.swift`, `NNW3ImportController.swift`, `NNW3OpenPanelAccessoryView.xib`,
  `NNW3OpenPanelAccessoryViewController.swift` — niche "NetNewsWire 3 bundle import"; can be deferred.
- `MainWindowController.swift` + `MainWindowState.swift` → `MainWindow.xaml.cs`,
  `MainWindowState.cs`.
- `ArticleExtractorButton.swift`, `IconView.swift`,
  `SharingServiceDelegate.swift`, `SharingServicePickerDelegate.swift` →
  `ArticleExtractorButton` user control + `SharingHelper` (uses `DataTransferManager`).

### I.2 `Mac/Preferences` (21 files) — 🔲
- **General**: `GeneralPrefencesViewController.swift` → `GeneralPreferencesPage.xaml`.
- **Accounts** (15 files): `AccountsPreferencesViewController.swift`,
  `AccountCell.swift`, `AccountsDetailView.swift`, `AccountsDetailViewController.swift`,
  `AddAccountsView.swift`, `AddAccountHelpView.swift`,
  `AccountsAddLocal[*.swift/.xib]`, `AccountsAddCloudKit[*.swift/.xib]` (⛔),
  `AccountsFeedbin[*.swift/.xib]`, `AccountsNewsBlur[*.swift/.xib]`,
  `AccountsReaderAPI[*.swift/.xib]` → `AccountsPreferencesPage.xaml` +
  `AddLocalAccountDialog`, `AddFeedbinAccountDialog`, etc.
- **Advanced**: `AdvancedPreferencesViewController.swift` → `AdvancedPreferencesPage.xaml`.
- Base: `PreferencesWindowController.swift`, `PreferencesControlsBackgroundView.swift`,
  `PreferencesTableViewBackgroundView.swift` → `PreferencesWindow.xaml`.

### I.3 `Mac/Inspector` (6 files) — 🔲
`InspectorWindowController.swift`, `Inspector.storyboard`, `FeedInspectorViewController.swift`,
`FolderInspectorViewController.swift`, `BuiltinSmartFeedInspectorViewController.swift`,
`NothingInspectorViewController.swift` → `InspectorWindow.xaml` with a content switcher
and one page per selection kind.

### I.4 `Mac/About` (4 files) — 🔲
`AboutWindowController.swift` + `Credits.html` + animated reveal → `AboutDialog.xaml`.

### I.5 `Mac/ErrorLog` (1 file) — 🔲
`ErrorLogWindowController.swift` → `ErrorLogWindow.xaml` bound to
`ErrorLogDatabase.GetEntries()`.

### I.6 `Mac/Resources` + `Mac/Base.lproj` — 🟨
Icons/assets/localization. Replace with WinUI `Assets/` images and `.resw` files.
`MainMenu.storyboard` → `MainMenuBar.xaml` (WinUI `MenuBar`).

### I.7 `Mac/CloudKitStats` (10 files) — ⛔ out of scope.

### I.8 `Mac/CrashReporter` (3 files) — ⛔ out of scope (Sparkle).

### I.9 `Mac/SafariExtension` — ⛔ out of scope.

### I.10 `Mac/ShareExtension` — ⛔ out of scope.

### I.11 `Mac/Scripting` — ⛔ out of scope (AppleScript `.sdef`).

---

## J. Top-level project folders

| Folder | Status | Notes |
|---|---|---|
| `Appcasts/` | ⛔ | Sparkle auto-update XML. |
| `AppleScript/` | ⛔ | Sample `.applescript` files (Excel, Mail, OmniFocus, Safari). |
| `AppStore/` | ⛔ | App Store configs. |
| `buildscripts/`, `scripts/`, `xcconfig/` | ⛔ | Build tooling (Xcode). |
| `Intents/` | ⛔ | Siri intents (`AddWebFeedIntentHandler.swift`). |
| `iOS/` | ⛔ | Entire iOS app — per user instruction. |
| `Widget/` | ⛔ | iOS widget extension. |
| `NetNewsWire.xcodeproj/` | ⛔ | Xcode project. |
| `Technotes/` | 🟨 | Reference docs — keep as architectural reference; not code. |
| `Tests/NetNewsWireTests/` | 🔲 | Mac app-level tests — to port to xUnit. |
| `Tests/NetNewsWire-iOSTests/` | ⛔ | iOS tests. |

---

## K. Parser test files still to port (resources already copied)

Source: `Modules/RSParser/Tests/RSParserTests/*.swift`. Port targets live in
`Tests/Parsers.Tests/`.

| Swift test | Status | Notes |
|---|---|---|
| `RSDateParserTests.swift` | ✅ | 25 assertions ported to `DateParserTests.cs`. |
| `FeedParserTypeTests.swift` | ✅ | All detection cases + `allthis-partial`. |
| `AtomParserTests.swift` | 🔲 | Item-field assertions (Daring Fireball, OneFootTsunami, research.swtch.com). |
| `RSSParserTests.swift` | 🔲 | Item-field assertions + `<guid isPermaLink>` edge cases. |
| `JSONFeedParserTests.swift` | 🔲 | Special-case entity decoding (kottke/pxlnv/macstories/macobserver). |
| `RSSInJSONParserTests.swift` | 🔲 | Scripting News JSON parsing. |
| `OPMLTests.swift` | 🔲 | Huge subscription list round-trip. |
| `HTMLMetadataTests.swift` | 🔲 | Feed-link discovery, favicon, og:image. |
| `HTMLLinkTests.swift` | 🔲 | Anchor parsing. |
| `EntityDecodingTests.swift` | 🔲 | Named-entity edge cases. |

---

## L. Final answer to "does the remaining work encompass all of NetNewsWire?"

**Yes — for the Mac-scoped subset the user requested.** The audit above covers every
folder in `C:\Users\qinjo\source\repos\NetNewsWire` and classifies each one as
✅ done, 🔲 speced-but-not-yet-implemented, 🟨 partial, or ⛔ explicitly out of scope.

**What is in scope and remains to be implemented** (~36,500 LOC of Swift/Obj-C → C#):

1. `Modules/Core`, `Modules/Database`, `Modules/Web`, `Modules/Articles`,
   `Modules/ArticlesDatabase`, `Modules/SyncDatabase`, `Modules/FeedFinder`,
   `Modules/Tree`, `Modules/ErrorLog`, `Modules/Secrets`, `Modules/Markdown`.
2. `Modules/Account` (shared + LocalAccount). Feedbin/NewsBlur/ReaderAPI/Feedly are
   speced as "stretch".
3. All of `Shared/**` except the iOS widget, ShareExtension, Handoff activity.
4. All of `Mac/**` except CloudKitStats, CrashReporter, Safari/Share/Scripting
   extensions.
5. Port of 8 remaining parser test files (resources already copied).
6. `Tests/NetNewsWireTests/` (app-level tests).

**What is permanently out of scope per user instruction** (~41,000 LOC):
iOS, iOS tests, Widget, Intents, AppleScript, SafariExtension, ShareExtension,
CloudKit / CloudKitSync / CloudKitStats, Sparkle Appcasts, CrashReporter, Scripting,
build tooling.

