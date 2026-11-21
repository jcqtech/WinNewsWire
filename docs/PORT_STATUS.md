# WinNewsWire port status

Current comparison: NetNewsWire `fb086eb7684e9fdb90855c4e033a6beba4325c5e`,
checked out at `D:\NetNewsWire` on 2026-10-16.

**This is a working parity improvement pass, not a completed 1:1 port.**
The ranked findings and remaining work are in [PARITY.md](PARITY.md).
The current suite-by-suite inventory is in [TEST_AUDIT.md](TEST_AUDIT.md).
`PORT_SPEC.md` is a historical specification, not a current completion checklist.

## Application bugbash (2026-10-29)

The launched Debug application was exercised repeatedly against an isolated copy
of the supplied 50-feed profile (12,525 stored articles), without changing the
live subscriptions or reading history. The final user-journey run covered all 50
feeds, 236 article reads, 188 read/star actions, 12 embedded publisher pages,
200 rapid article selections, three feed/folder creation and rename cycles,
delete/cancel/undo/redo, and three OPML export/reimport round trips through the
actual Windows file dialogs. Malformed OPML and picker cancellation were included.
A fresh process then recovered all 53 subscriptions, their edited titles and
folder memberships, 20 saved article statuses, and the selected unread/starred
article.

| Reproduced problem | Repair |
| --- | --- |
| OPML export wrote the file, then displayed an empty failure dialog because the legacy cached-file completion API returned `0x80070490`. | Use the Windows App SDK save picker and the existing atomic UTF-8 file writer. Preserve the normal filename/type choices and report an error code when Windows supplies no message. |
| Add Feed accepted a `file:` URL after normalization turned it into a bogus HTTP host. | Reuse the activation URL validator for typed subscription input. Keep bare hosts, ports, and `feed:`/`feeds:` wrappers; reject non-web schemes and multiple-line input. |
| Opening and closing readers accumulated native graphics resources; a selected article's event handler could also retain a closed reader. | After saving and draining the window, clear custom title-bar regions and reset native title-bar rendering before destroying the HWND. Unsubscribe the selected article's handler during content shutdown. |
| An unfinished native Find operation could block later searches and window closure. | Cancel superseded searches and searches belonging to a closing document/window. Bound native search completion and report a timeout explicitly. A held-native-operation regression verifies replacement, cancellation, and close without waiting for that operation. |

The window-resource investigation separated plain WinUI, Mica, icons,
accelerators, animated icons, WebView2, main content, and custom title bars.
The custom title bar reproduced the native growth independently of the reader.
Before cleanup, eight reader windows added roughly 480-520 GDI objects; resetting
the title bar removed that growth. In the final populated-profile run, ten reader
and ten utility windows closed with **111 GDI objects before and after** and no
retained closed managed windows after collection. This is a scoped lifecycle
result, not a claim that the entire application has no possible leaks.

The repeatable workload is `Windows\Diagnostics\VerifyReader.ps1 -Bugbash`
with `-OpmlFixture`, `-ProfileDirectory`, a new `-ResultsDirectory`, and
`-UseInstalledRuntime`. It copies the supplied private profile, starts the full
runtime, drives native sidebar/file-picker input and real controls, captures
resource/response measurements, and verifies a fresh-process restart. Never point
the workload at a profile containing remote account credentials.

Remaining limits: cold, script-heavy publisher pages still showed occasional
roughly 200-250 ms animation-frame gaps on the default GPU path. These browser
callbacks are not screen-presentation or physical-touchpad latency measurements.
The user's rendering preferences were not changed. The native browser's
whole-word search also rejects multiword phrases in this runtime; the Find option
now explains that phrase searches require Whole Words to be off, rather than
silently substituting a different search engine.

## Layout

```text
Modules\
  Account\
    Account.csproj
    Sources\Account\
      LocalAccount\
      Feedbin\
      Feedly\
      NewsBlur\
      ReaderAPI\
    Tests\
      Account.Tests\
      RemoteAccounts.Tests\
  ActivityLog\
  Articles\
  ArticlesDatabase\
  Core\
  Database\
  ErrorLog\
  FeedFinder\
  HTMLMetadata\
  Images\
  Markdown\
  NewsBlur\
  Parsers\
  Secrets\
  SyncDatabase\
  Tree\
  Web\
Shared\
  AppShared.csproj
  ArticleRendering\
  Extensions\
  Exporters\
  Importers\
  SmartFeeds\
  Timeline\
  ...
Windows\
  WinNewsWire.csproj
  ...
Packaging\
  WinNewsWire (Package).wapproj
Tests\
  WinNewsWire.Tests\
```

Modules use `Sources\<module>` and module-owned `Tests` folders. The shared
application layer is separate from Windows UI code, as in upstream's
`Modules`/`Shared`/`Mac` split. `Core`, `Parsers`, `Web`, `Database`, and `Tree`
correspond to upstream's `RSCore`, `RSParser`, `RSWeb`, `RSDatabase`, and `RSTree`.

Feedbin, Feedly, and Reader API no longer live in separate assemblies that depend
back on Account. Their implementations and all account delegates now live under
Account. The standalone NewsBlur API/model module remains separate. Delegate
construction belongs to Account, not the WinUI application bootstrap.

Backend projects target `net8.0`; DPAPI methods are explicitly Windows-specific.
The shell remains WinUI 3 / .NET 8 for Windows. Duplicate unused shell database,
subscription-storage, RSS-service, and feed-model implementations were removed.
This did not delete or relocate user data.

The solution's `Any CPU` configurations map the WinUI application and MSIX
project to `x64`; those projects do not define an `Any CPU` platform. Libraries
and tests retain their `Any CPU` mappings. Choose the explicit `x64`, `x86` or
`ARM64` solution platform when selecting an application/package architecture.

For direct Visual Studio debugging, set `WinNewsWire` as the startup project and
use its `WinNewsWire (Unpackaged)` launch profile. Framework-dependent builds
include the Windows App SDK bootstrap module initializer before WinUI
`Application.Start`; the installed matching runtime is still required.
The initializer explicitly does nothing when MSIX already supplies package
identity, so `WinNewsWire (Package)` remains the startup project for deployed
package debugging and identity-dependent activation checks. Self-contained
probe builds retain their existing registration-free initialization path.

## Implemented changes

- OPML parsing no longer skips outlines after unknown XML elements or a document
  title. Normalization preserves the parser tree while flattening account folders.
- Local refresh keys subscriptions by the canonical request URL, so an imported
  URL with an uppercase scheme is not downloaded and then silently discarded.
- Manual imports preserve custom names and additive root/multiple-folder
  membership. Exports are sorted, use the shared representable objects, omit fake
  "Untitled" names, strip illegal XML characters, and preserve Unicode.
- Local OPML is restored at startup, retaining metadata from `Settings.json`.
  Newer settings recover a failed OPML write instead of reverting subscriptions.
  Settings and OPML writes use atomic replacement; import write errors are visible.
- Import/export supports account choice and remembered selections. Imports use
  account delegates, report failures, support cancellation, refresh articles, and
  update the sidebar. Feedbin and Reader API expose native import APIs.
  Feedly retains its API implementation but hides account-level import, matching
  upstream's current capability flag. NewsBlur import is unavailable rather than
  a misleading local-only operation.
- Timeline sorting uses shared upstream-style date/feed/title/unread/starred
  ordering with independent per-window parameters. Legacy Group by Feed becomes
  feed-name sorting, rather than a second conflicting sort setting. Full-text search indexes actual articles and
  authors, updates edits, backfills older databases, and respects feed scope.
  Ctrl+A no longer marks articles read.
- Upstream title sanitization/formatting and HTML stripping are used instead of
  identity-only text caches and duplicate strip implementations. Browser-opening
  commands use the shared web/mail scheme contract.
- Today uses a rolling 24-hour window and arrival-date fallback. Article updates
  report real changes, preserve Markdown/attachments, and share status identities.
  Status repair and keyed sync acknowledgements preserve pending local changes.
- Remote account metadata persists credential lookup usernames and custom Reader
  API servers, without storing secrets. Unknown/failing delegates do not silently
  become local accounts. Feedbin import/refresh maps feed IDs correctly, restores
  multiple tag memberships, and preserves queued edits on failed sends.
- Feed/folder create, rename, delete, move and restore now use account delegates
  and native service endpoints. Sidebar and inspector edits no longer make
  local-only changes to remote subscriptions. Deletion and moves support async
  undo/redo and preserve other memberships where the service supports them.
- Timeline multi-selection, bulk status commands, per-scope read filters,
  current/all-account search, multiple-article sharing, and focus-aware
  keyboard/copy/select-all handling are implemented.
- Sidebar multi-selection uses an extended-selection outline ListView, avoiding
  TreeView's cascading descendant selection. Combined scopes retain their own
  read filters, deduplicate articles and support scoped search, bulk commands,
  stable rebuilds and folder expansion. Mixed deletion undo restores folders
  before their feed memberships.
- Active account preferences use `AccountSettings` over an atomic defaults
  adapter (`AccountPreferences.json`). Feed preferences use `FeedSettings` and
  the upstream-compatible `FeedSettings.db` schema. Legacy Windows JSON,
  XML/binary property lists, and legacy account credential-lookup databases
  have migration coverage. `Settings.json` remains the subscription snapshot.
- Feedly collection reconciliation matches external IDs across renames, preserves
  shared feeds and settings, and removes stale memberships. Canonical feed-ID
  repairs migrate article references without changing article IDs and replay
  safely after interrupted writes. OAuth refreshes coalesce and rejected tokens
  are not retried until credentials change.
- Feedly, Reader API and NewsBlur refreshes now reconcile complete remote
  membership/status snapshots and retrieve missing bodies. History paging,
  failed-page watermarks, Reader API token renewal/quota conservation, and
  Feedly continuation/download caps have regressions. Feedbin follows every
  history page and repeats its backdate window after a failed page.
- `ActivityLog` mirrors the upstream lifecycle, owner identity, messages and
  500-entry completed history. Account edits and refresh stages feed the Windows
  Activity window. The shared operation queue has the original dependency,
  ordering and cancellation cases; view-model updates use its UI context.
- Native theme ZIP fixtures and metadata keys are supported. Theme installation
  validates before replacing an existing theme and does not derive filesystem
  destinations from metadata. URL installation and Windows filename/alias checks
  are included. Batch downloads bypass the one-shot cache and await parsing;
  429 cancellation affects the exact host, including active tasks.
- The current byte-oriented XML/SAX and HTML scanner layers are ported. RSS and
  Atom share the native item/identifier model; Atom honors nested xml:base and
  preserves raw XHTML. HTML discovery supports typeless feeds and raw-text blocks.
  Date/entity behavior and all 330 upstream parser test functions are covered.
  Parser source/tests now follow `Feeds`, `Html`, `Xml`, `Json` and `Utilities`.
- Parser fixtures preserve the exact committed bytes, including mixed line endings
  and legacy encodings. Export from the pinned Git tree with
  `-c core.protectNTFS=false -c core.autocrlf=false`; copying a Windows checkout
  can copy altered CRLF bytes. `.gitattributes` protects the imported fixtures.
- Known legacy RSS/RDF/Atom/JSON article IDs migrate without losing their stable
  article IDs, status or arrival dates. The local parser hash marker is now
  `v4-native-parsers:`; old hashes trigger unconditional retrieval when due.
- Article display and reader mode use the shared renderer and selected theme,
  native macros/scripts, bylines, base URLs and all five font sizes. WinUI inline
  formatting, image luminance and text measurement have platform counterparts.
  Custom themes use their actual mapped stylesheet; host processing runs through
  `ExecuteScriptAsync` even when article JavaScript is disabled. Main-world
  callbacks are suppressed in that mode, so host reader events use a named CDP
  isolated world with `Runtime.enable`, a world-scoped binding and context plus
  navigation guards. Star shortcuts, typing in input/contenteditable controls,
  hover, Space scrolling and reader F11 work without enabling publisher scripts.
  Scroll is also queried explicitly before saving or re-rendering.
- Sidebar cross-account transfers copy feeds/folders, reusing existing
  subscriptions without deleting the source. Browser URL drops open the add-feed
  dialog with the target account/folder; empty-space/smart-scope drops use the
  permanent local account. Explicit remote destinations are retained, and
  ownership/activity/folder restrictions are checked again after reading the URL.
  Add-feed defaults are remembered and
  normalized URLs use the shared helper.
- The default account has the stable `OnMyMac` ID and sorts before other accounts,
  rather than following directory enumeration. It is not removable through
  Preferences or the backend; additional local accounts remain removable.
  Invalid default metadata is reported instead of creating a replacement profile,
  and failed manager construction releases already adopted resources.
- Edit exposes separate Copy Article URL / Copy External URL commands for
  multi-selection, with native enablement and Ctrl+Shift+C / Ctrl+Alt+C.
  Missing links are skipped without substituting article links for external links;
  ordinary Copy and the existing Copy Link alias remain. Clipboard failures are
  reported and logged.
- Go exposes Today, All Unread and Starred with Ctrl+1/2/3. It clears database
  search even when reselecting the current smart scope and focuses a visible
  sidebar. Next Unread now advances through the current timeline before moving
  to another visible unread sidebar scope; account headings and expanded folders
  are skipped, while collapsed folders and smart feeds participate.
- Next Unread queues repeated commands, waits for automatic read persistence and
  current timeline replacement, refreshes scope counts at boundaries, and cancels
  on window shutdown or a superseding user selection. N, Space, menus and the
  isolated reader use the same operation. Synthetic page-generated key events
  cannot invoke the reader's native commands.
- Help includes the application support folder and Windows project-support
  destination; launcher failures are visible. The upstream NetNewsWire News
  subscription shortcut is intentionally omitted from the Windows Help menu.
  Its source-port account APIs remain available.
- Unread Counts offers native Show / Show dot / Hide modes. Sidebar dots are
  eight DIP, accessible names omit hidden counts and speak "unread" for dot mode,
  and numeric header/footer counts are suppressed outside count mode. Actual
  article statuses, unread counts and filters do not change. Existing windows
  update in place, and failed preference writes restore the previous selection.
- The sidebar uses readable small section headings, 32-DIP rows, a 10-DIP
  content gutter, aligned 20-DIP account/smart headings and 52-DIP top-level
  feed titles, plain right-aligned unread counts, and rounded selection with an
  accent bar. Folder disclosure, favicons, extended selection and inline rename remain. The native
  `DefaultListViewItemStyle` supplies the selection chrome; disabled section
  headings are not faded.
- Account headings are non-selectable static labels without disclosure,
  selection, or hover highlighting, just like Smart Feeds. Both headings sit
  farther left than the child icons and labels. Sun, reading-list, and
  outlined-star glyphs identify Today, All Unread, and Starred. Account identities
  remain available for saved scopes and drop destinations, but account children
  cannot be collapsed. Folder and smart-feed glyphs are 14 DIP within fixed
  16-DIP slots; favicons remain 16 DIP, and all icon slots have a six-DIP gap
  before their labels. Folders display a folder glyph instead of an empty favicon
  slot; their inset chevrons are clear of both the
  window edge and the selection accent. Recycled feed rows retain their favicons.
  The scrollbar rail extends four DIP into the divider gutter without moving
  feed rows or sidebar header controls. The adjacent resize hit target stays eight DIP
  wide and remains clear of the scrollbar.
- The shared unread-display strings use a neutral `.resx` resource. This is the
  beginning of a localization mechanism, not a claim that all existing English
  UI has been localized. The corresponding pinned native strings have no
  translated localizations in the source catalog.
- New Folder now remembers its destination account, rejects empty names before
  submission, validates the selected account again on acceptance and leaves
  preferences unchanged on cancellation. Feed/folder destination helpers share
  the manager's canonical-local-first ordering.
- Toolbar configuration is shared across windows and layouts under the native
  `MainWindowToolbar` identifier. Show/hide, add/remove/reorder, reset/cancel and
  icon/text display modes are retained, but there is no longer a standalone
  command strip. Refresh/Add are in the feeds header; Mark All Read and the
  article read filter are beside the timeline title and its small unread subtitle.
  Article commands remain in the detail header. Column layout combines only
  timeline/article commands at the right of one header above the column names.
  Refresh, Add and the feed read filter stay in the feeds header at unchanged
  positions. Hidden-sidebar commands remain reachable in overflow. Hide Toolbar
  collapses the entire pane headers, including the timeline title and unread
  subtitle, and removes their reserved space. A floating ellipsis at the upper
  right provides Show Toolbar, Customize Toolbar, and display-mode options. It
  stays clear of Find, column-header controls, and scrollbar rails, and shares
  the hidden header's background material over an opaque themed base so article
  colors cannot bleed through. This applies to standard,
  column, and reading-only layouts and to newly opened windows; the fixed
  title-bar controls and column names remain available.
- Back, the existing feeds-sidebar toggle, and search are fixed title-bar controls.
  The left-to-right order is Back, sidebar toggle, WinNewsWire title, then search;
  there is no title-bar image. The large taskbar icon is the original `wnw.ico`,
  while secondary windows retain the small flattened `wnw-flat.ico`.
- Reader-wide keyboard accelerators suppress WinUI's automatically generated
  container tooltip, which otherwise can appear as a stray `N` over article
  content. Shortcuts and explicit command-button tooltips remain enabled.
- The title-bar left inset is eight DIP plus any native caption exclusion.
  Back has a smaller 14-DIP-equivalent glyph, using an 18-DIP canvas for the
  padded native animation. The menu remains 16 DIP and both hit targets remain
  32 DIP. Native `AnimatedBackVisualSource` and
  `AnimatedGlobalNavigationButtonVisualSource` supply the right-anchored Back
  shrink, horizontal menu compression, cubic easing and release rebound through
  normal button states. They inherit foreground/theme changes, honor Windows
  animation settings and retain static glyph fallbacks; no custom timers or
  per-frame layout changes are used.
  Search remains available without showing or changing hidden/customized header
  commands. Native input exclusions use physical pixels converted from current
  XAML bounds/DPI, refresh on resize/scale changes and leave caption buttons clear.
  Compact windows retain the earlier 700-DIP title-collapse behavior.
- The main window's native minimum width follows the article pane's 300-DIP
  minimum, plus its existing margins, border, and native frame at the current
  display scale. Standard layout drops the hidden timeline/divider's width
  constraint when compact; column and reading-only layouts use the same reader
  minimum. Restored windows no longer grow to an unrelated 640-pixel floor.
  At compact widths, rightmost menu headings move into a trailing menu with a
  centered, 20-DIP ellipsis icon. Each hidden heading expands into its original
  command hierarchy.
  Headings return in order as space becomes available; horizontal scrolling is
  no longer needed. Linked overflow entries preserve native commands, shortcut
  labels, checked/enabled states, and dynamic theme/window submenus without
  reparenting WinUI-owned menu controls.
- Delete removes selected feeds/folders through the existing confirmation and
  grouped undo only when the sidebar has focus. It leaves timeline/reader focus,
  search fields, embedded-browser inputs, and inline renaming alone. Repeated
  presses cannot stack deletion confirmations.
- Header customization preserves saved version-1 fixed/structural identifiers,
  but exposes only movable header commands; old tracking separators and flexible
  spaces are no longer rendered. Command order is respected within each pane and
  across the combined column header. Failed writes retain the previous state.
- Changing the article read filter waits for already-started automatic read
  writes before fetching rows, and abandons stale requests after a scope/filter
  change. Without that ordering, rapidly reading articles and immediately
  filtering could leave other read rows visible. The current article is retained
  intentionally; tooltips explain that exception and unavailable filter scopes.
- Column layout has no divider between the title and table-header rows. Their
  actual bounds remain contiguous, including larger text and all command display
  modes, while content continues scrolling behind the acrylic.
- Open in Web View is between Share and Open in Browser by default. Version-1
  configurations with Share or Open in Browser gain that adjacent command;
  version-2 saves preserve subsequent removal/reordering and existing visibility.
  Empty or unrelated custom command sets are not reset.
- Read-state actions use the Fluent System Icons TextBox and TextBox Checkmark
  16 Regular paths: plain textbox for Mark as Unread, checkmarked textbox for
  Mark as Read. The separate Mark All as Read command uses a plain checkmark
  consistently in its toolbar, overflow, and context-menu entries.
  Path text is shared, but every control owns its geometry; WinUI does not allow
  the same geometry instance to be parented by multiple `PathIcon` controls.
  The customization dialog
  reserves space for both its outer scrollbar and its command-list scrollbar;
  the bounded command list remains non-virtualized.
- Article links open inside WinNewsWire by default. Back restores previous
  article/feed selections, search/read-filter state and reading position without
  rebuilding a same-scope timeline. Each window has its own in-memory history.
  Webpage history uses native browser entries, including form state, fragment/
  SPA history and redirects, and remains distinct when articles and websites
  are interleaved. Open in Browser remains explicit and honors browser preferences.
- Publisher webpages use a separate WebView2, without the article renderer's
  host bindings or theme mappings. Trusted article clicks are handled by its
  isolated world; synthetic clicks and unrequested document navigation are
  rejected. Webpages have normal scripting independently of the article-script
  preference. Find, speech, Share and explicit browser opening target the
  displayed document, and webpage errors remain visible with Back available.
- Entering Web View covers the reused browser with an opaque loading surface and
  keeps its opacity at zero until the matching navigation completes. This also
  prevents cached pixels from showing during WinUI's delayed native visibility
  changes. Failures stay covered with an explicit error instead of exposing the
  previous article's webpage. Pending Share/Open in Browser actions use the
  requested address, not the retained document's address/title. Native history
  and form state are preserved; no blank navigation or browser recreation is used.
- Column rows use thin gray bottom dividers instead of alternating backgrounds,
  with three-DIP top/five-DIP bottom padding. Sorting headers have no press-scale
  animation or boxed top/side borders, no unread/star header icons, and thin
  chevrons. Narrow tables remain left-aligned. Timeline status joins the bottom
  detail footer in column layout, with counts on the left and article/webpage
  details right-aligned. Standard-layout footer alignment is unchanged; titles
  and footer text trim rather than wrap.
- The timeline and column-label headers overlay the scrolling list with acrylic.
  Insets track their measured heights, so the first row remains unobscured and
  content can subsequently scroll behind the glass. A separate header row with
  only a flat background underneath cannot produce the former blur effect.
- The default article theme uses a slightly wider 48-em reading column (formerly
  44 em) and 40-pixel desktop side padding (formerly 48 pixels). The maximum stays
  responsive to the available pane width; custom theme styles are unchanged.
- Customization uses a non-virtualized panel for its bounded command list.
  Insert/move actions lay out the selected row and bring that actual container
  into view rather than calling virtualized `ListView.ScrollIntoView` immediately
  after collection mutation.
- Current Activity is distinct from the historical Activity Log. It updates at
  half-second intervals, orders running before pending work, lingers completed/
  failed entries for two seconds and stops its timer when idle or closed.
  Immutable activity snapshots keep concurrent transitions consistent and make
  single-row state changes visible. Ctrl+7 / Ctrl+8 open the respective windows.
- ArticlesDatabase exposes native per-feed latest update dates with published,
  then modified, then arrival-date precedence before aggregation. Its Account
  facade respects account operation lifetime. Dinosaurs now uses these dates
  for calendar-month filtering, native sorting and a resizable stale-feed window.
  It supports threshold persistence/blank input, icons, sidebar/browser/copy/help
  actions and confirmed removal from every subscription membership with undo/redo.
  These new shared/control cases remain execution-blocked.
- Remote accounts can reconnect without replacing their identities or cached
  articles. Credential bundles are DPAPI-protected, atomically replaced and keyed
  by collision-resistant server/username scopes. Legacy files migrate on write;
  FreshRSS credentials are endpoint-scoped.
- Feedbin persists conditional validators only after applying snapshots, handles
  304s across restart and uses native POST/DELETE status methods. Feedly performs
  local discovery after an empty service search and downloads initial articles.
  Discovery resolves redirects against the response URL and logs its native
  activity stages.
- Local download sessions persist across refreshes, including redirects and
  rate limits. OpenRSS has a persisted one-hour allowance. Download cancellation
  drains callbacks; account shutdown drains operations before closing SQLite.
  Timers no longer overlap their own work, and status refreshes share the account
  operation gate. Account-removal cleanup failures are reported explicitly.
- Remote callers distinguish owned and borrowed HTTP clients. Account shutdown
  closes delegates before storage, and Feedly drains outstanding OAuth work even
  after waiter cancellation. Reconnect closes the replaced delegate; failed
  construction closes factory-owned resources without closing an already adopted
  account or masking its original error.
- Large article results load author/attachment relationships in bounded SQL
  batches. One-shot and batch downloads share a tested 64 MiB response bound.
- Native-style memory-pressure cleanup releases derived text/download/favicon
  caches without discarding live article-status identity. The desktop monitor
  uses Win32 `CreateMemoryResourceNotification` and waits for high-memory recovery
  before rearming; WinRT `MemoryManager` subscription failed in the unpackaged
  runtime. Startup image-cache cleanup follows the native three-day cadence,
  targets only named image caches and reports failures without advancing its marker.
- Active image caches use native `Caches\Images` / `Caches\Favicons` directories
  and lowercase URL-MD5 filenames. Legacy Windows cache hits are copied lazily
  without network access or deletion; native entries take precedence. The
  raw-key binary cache uses atomic writes and recreates a purged directory.
- Serialized SQLite connections use native DELETE journaling and NORMAL
  synchronization. Existing WAL data is recovered by SQLite before switching
  modes, and a blocked transition is reported without deleting sidecars. Failed
  store initialization closes its owned connection.
- Account Stats includes inactive accounts, totals and numeric sorting. Article,
  unread and starred counts are feed-scoped; status counts include orphaned rows.
  Explicit vacuum serializes maintenance of the owned article, feed-settings,
  remote-sync and error-log databases without creating a local sync database.
  Global HTML/image metadata databases also participate in explicit maintenance.
- Account Stats columns resize through a composed WinUI control with keyboard
  increments and a range-value accessibility pattern. Header and row dimensions
  stay aligned, minimums fit the localized labels, and saved widths survive
  reopening. Windows uses a 1000-DIP upper bound for these utility columns.
- Sidebar disclosure belongs only to folders. Old saved account-collapse flags
  are ignored without losing folder expansion state. Hide/Show Read Feeds is
  window-local, preserves selected feed/parent exceptions, and always retains
  smart feeds and account labels. Newly active accounts expose their children,
  and account names sort using the shared comparer.
- Feeds can be dragged before/after other feeds to reorder them, onto folders
  to move them inside, or back onto root feed rows to move them out. Insertion
  lines and folder outlines distinguish the drop targets. A collapsed target
  folder opens to reveal the moved selection. Same-account placement and
  ordering form one undo/redo action, with rollback if saving the order fails.
  Existing cross-account copy rules and service restrictions remain intact.
  Manual feed order is a local, window-shared UI preference (`sidebarFeedOrder`)
  keyed by account and folder identity and containing feed URLs. It survives
  restart and folder renames; new feeds follow the manually ordered entries.
  Containers without a saved order and folder headings retain their alphabetical
  ordering. Account storage and native OPML serialization are unchanged, so this
  Windows display order is not exported or synchronized to a feed service.
- Account name and active-state setters publish native-style notifications only
  after the primary preference write succeeds. The manager forwards these to
  existing windows and removes event subscriptions on replacement/removal/disposal.
- Native column layout uses the global `useColumnLayout` preference. The timeline
  is above the article, with a remembered split height, 120/200-DIP pane minimums
  and a 40% first-use split. Its five columns have separate unread/star indicators,
  bold unread text, untitled-body fallbacks, feed icons and native dates. Header
  sorting is window-local; resizing/order are saved separately. Switching layouts
  preserves the browser, selection and normal timeline width.
- Full-screen presentation has a root F11 accelerator and a reader shortcut.
  It preserves normal/maximized bounds and restores only after activation.
  Reading Only remains a separate Windows presentation option.
- Multiple main windows have independent sort/search/selection/undo state, shared
  article-status updates, per-window sharing handles and synchronized appearance.
  File > New Window uses Ctrl+Alt+N. Typed window/sidebar/timeline/detail state
  restores bounds, maximization, pane widths/visibility, selected scopes, read
  filters, folder expansion, a selected article, reader mode and scroll. Search
  text is not persisted. Missing IDs are ignored, off-screen geometry is clamped,
  and an initializing window retains its original saved state.
- System close, menu close and Quit use one asynchronous coordinator:
  `Window.Close()` itself bypasses `AppWindow.Closing`. Failed state writes leave
  windows open and report the error. Non-last close saves the remaining windows;
  last close retains its final snapshot, while Quit captures every open window
  before closing any. Shared runtime work drains before the last window closes.
  Outstanding browser operations and the CDP Runtime domain are drained before
  destroying WebView2. Geometry is applied before restored article loading.
- Timeline refresh applies the current selection, not a stale pre-fetch snapshot,
  so a click during an asynchronous refresh is not discarded. Restoring an
  unread article does not mark it read, and a selected read article can remain an
  exception to its unread scope.
- Favicon state survives timeline row replacement, so a status-only refresh no
  longer reloads the reader. Reader extraction completion follows article
  identity instead of discarding the result when the row object is rebound.
- Status changes queue only genuinely changed IDs; normal queue-write failures
  roll back local state so retries remain possible. This does not provide a
  crash-atomic transaction across the separate article and sync databases.

### Implemented but awaiting execution approval

The latest status-sync changes add the native two-minute active/thirty-minute idle
schedule, queued-edit wakeups, Feedly's thirty-minute no-change backoff and
unread-only polling, Inoreader send-only timer sync, and background flushing above
100 pending changes. They compile, including in the Release package, but
Application Control now prevents their Account/Shared tests from executing.
These changes are not yet runtime-verified.

The permanent-local-account and external-drop changes also await original
Account/application execution. Ten new Account regressions cover identity,
sorting, removal, restart/data preservation and construction failures. The new
URL-copy command probe captures WinRT data packages without touching the system
clipboard; its compile success is not a runtime or OS clipboard result.

Browser discovery/selection, duplicate-name path labels, background preference
inversion and safe native launch arguments are implemented. The 25 Web tests
include the nine exact upstream browser-path functions. The earlier application
policy block prevented the browser UI check, but the 2026-10-20 installed-runtime
run now executes it successfully. Ordinary links use embedded browsing; explicit
Open in Browser uses the selected external browser. Launch requests are captured
in the probe: no live external browser was launched or system default changed.

URL/file activation and single-instance ownership now compile, and the package's
registrations are inspected against its actual executable. Deployed/two-process
behavior still needs an approved runtime; the parsing and notification-identity
tests are not substitutes for that check.

### Activation entry points

Installing the package registers `feed`, `feeds`, `x-netnewswire-feed`,
`x-winnewswire-feed`, `netnewswire`, `winnewswire` and `nnw`. It offers file opening
for `.opml` and `.nnwtheme`, without taking generic XML/ZIP associations. Source
builds do not write protocol/file registrations to the machine.

Supported application links include:

- `winnewswire://subscribe?url=https%3A%2F%2Fexample.com%2Ffeed`
- `netnewswire://theme/?url=https%3A%2F%2Fexample.com%2Ftheme.zip`
- `nnw://showunread`, `nnw://showtoday`, `nnw://showstarred`
- `winnewswire://showunread?account=ACCOUNT_ID&id=ARTICLE_ID`

Feed activations show the existing add-feed sheet. OPML files retain account
selection and progress/cancellation. Article links select the requested smart
scope, clear search, retain a selected read-article exception and mark the opened
article read. Invalid, missing or ambiguous activation inputs surface an error.
Notification activation also accepts the earlier account/article argument keys.

The executable accepts URLs or absolute file paths, with optional `--subscribe`,
`--import-opml` and `--install-theme` commands. For example:

```powershell
& '.\WinNewsWire.exe' --subscribe 'feeds://example.com/rss'
& '.\WinNewsWire.exe' --import-opml 'D:\Feeds\Subscriptions.opml'
& '.\WinNewsWire.exe' --install-theme 'D:\Themes\Example.nnwtheme'
```

Use absolute file paths when another instance is already running: Windows launch
redirection does not carry the originating working directory. A relative path
without that context is rejected rather than read from the wrong directory.
Theme file activation currently accepts archives, not unpacked `.nnwtheme`
directories. The normal process uses AppInstance redirection before starting the
shared runtime; the private probe does not participate in production instancing.

### Preference compatibility

Refresh intervals now use the native integer representation: 1 = manual,
3 = thirty minutes, 4 = one hour, 5 = two hours, 6 = four hours and 7 = eight
hours. `refreshIntervalVersion = 1` identifies that representation.
Unversioned Windows values 0/1/2/3/4/5/6 migrate atomically to 1/3/3/4/5/6/7.
This preserves manual mode and chosen cadences except for the native removal of
ten-minute polling, which becomes thirty minutes. A previously existing Windows
preferences file without an explicit interval retains its former implicit
one-hour choice; a fresh profile uses the native two-hour default.

Migration rereads the file strictly before modifying it, preserves unrelated
keys, rejects unknown encoding versions and never advances its marker after a
failed write. Native values are not reinterpreted on later launches. Do not drop
the marker or cast old raw JSON integers directly to the new enum. Standalone
`AppDefaults` instances can explicitly call `MigrateRefreshInterval`; the shared
application defaults perform this before use. Saving creates a missing parent
directory, but failed writes still preserve the previous data and notifications.

### Find in Article

Ctrl+F opens the article-body Find bar; Ctrl+Alt+F focuses database Article Search.
F3 / Shift+F3 (also Ctrl+G / Ctrl+Shift+G) advance/reverse matches, Ctrl+E uses
selected article text and Ctrl+J reveals the selected range. This is an
intentional shortcut correction from the old Ctrl+F database-search behavior.

Find uses the stable WebView2 Find API, including native case sensitivity,
whole-word matching, highlighting, match counts and wrapping. The SDK is pinned
to 1.0.3405.78, the first stable release of that API; full compatibility requires
WebView2 Runtime 139.0.3405.78 or newer. Missing runtime capabilities are reported
rather than replaced by a success-shaped JavaScript fallback. The dependency
upgrade is for this API, not an alternate application execution path.

Browser-chrome accelerators are disabled so the app and trusted reader own Find,
refresh and other commands; standard text editing/navigation remains available.
Queued search operations reject obsolete queries/documents, preserve the query
through rerendering and stop on close/shutdown. Selection/scroll queries still
use the existing host DOM access; the actual text search is performed by
WebView2, with publisher JavaScript left disabled. These paths compile but their
engine/control probe remains execution-blocked.

### Retention compatibility

The new `CleanupDatabaseAtStartupAsync` API requires an explicit
`ArticleRetentionStyle`; nothing calls it automatically yet. `PreserveHistory`
does not delete any records. `SyncSystem` removes only read, unstarred bodies
older than ninety days, using the native 365/300/225/150/90-day progression and
two-second age-cleanup budget. It retains orphan status rows until 180 days,
and then only removes read, unstarred statuses. `FeedBased` does not age-purge
subscribed bodies at startup, and removes unstarred orphan statuses after thirty
days. Native feed-update pruning remains a separate `deleteOlder` contract.

Both destructive modes remove content and statuses for unsubscribed feeds when
the subscribed-ID set is nonempty. That includes starred articles, so the
existing Windows profiles have deliberately not been opted in. Empty subscribed
sets do not mean "delete everything." The implementation preserves referenced
authors/attachments, removes stale search rows, batches IDs and commits all
changes atomically; failures do not evict cached status identities.

This supplies the native engine, not a completed profile migration. The older
Windows `CleanupOldArticlesAsync` compatibility method is not the native policy
and is not wired into application startup. An explicit profile choice/migration
is still required before enabling native deletion.

### Windows scripting equivalent

The out-of-process COM adapter adds `WinNewsWire.Application`, seven fixed-IID
dual IDispatch interfaces, a generated version-1.0 type library, native-style object
collections and per-user/MSIX activation. It reuses the service below rather than
opening account stores in the COM process. PowerShell 5.1/7, VBScript and JScript
fixture clients exercise real child-process activation and x86/x64 callers.
See `Windows\Automation\COM.md` for the complete dictionary mapping, examples,
deployment instructions and the distinction between fixture and installed-app
verification. The earlier compile-only evidence below is historical.

Large-dataset follow-up adds feed-scoped indexed article resolution, metadata-only
article membership/counts, 128-reference live property batches and bounded
`GetPage` exports. At 5,000 articles, full title projection fell from 175.37 s to
0.38 s and unread filtering from 252.27 s to 0.30 s in the real COM fixture.
Paging still materializes all metadata and is not a transactional SQL cursor.
An isolated developer-registered package passed cold activation, two-window UI
selection, real RSS refresh/failure, restart persistence and actual Access VBA
early-bound scripting plus a 321-row Excel export/reopen. Normal signed-MSIX
installation remains blocked by certificate trust/elevation in this session;
Excel-hosted VBA and authenticated remote-account sync remain unverified.
See the COM guide for exact scenarios, measurements and remaining limits.

Local automation is disabled by default and requires an explicit Preferences
opt-in. The service follows the pinned scripting dictionary's accounts, folders,
feeds, articles and authors, including active-main-window selection. Commands
are get/count/exists/make/delete/set/openLocation; writable properties are limited
to account active state and article read/starred state. Feed/folder creation and
deletion use the existing Account mutation paths. Credentials and arbitrary
code evaluation are not exposed.

Article references use the feed's `UniqueID`, not the database article ID.
References validate their account/feed/folder context and ambiguous IDs fail
explicitly. Unlike a broken native existing-feed specifier, creating an already
subscribed URL returns a usable reference to its actual membership.

The Windows transport uses a current-user SID access rule and
`PIPE_REJECT_REMOTE_CLIENTS`; .NET's `CurrentUserOnly` flag alone does not supply
the latter. Length-prefixed UTF-8 messages have request/response limits and
deadlines. Endpoint opt-in changes coalesce, failures are surfaced, and shutdown
cancels/drains requests before account stores close. No listener is created when
the preference is disabled.

`Windows\Automation\Invoke-WinNewsWire.ps1` is a PowerShell 7 client, bundled with
the protocol README. This is a local JSON/pipe replacement for the native
scripting object model, not AppleEvent compatibility. Core transport/opt-in
tests execute; original Shared service cases and the application/selection
probe compile only. No current-user or profile identifier is logged or stored
in the pipe name in clear text.

### Speech and typography

Edit > Speech now supports Start Speaking and Stop Speaking with the installed
Windows speech engine. Selected article text takes precedence; otherwise the
currently rendered body is used, including reader mode and script-disabled
documents. There is one playback session across main windows. Replacing it,
changing the owning article, closing that reader or quitting cancels and drains
the owned synthesizer stream, media source and player. Closing a different reader
does not stop the active owner. Speech failures are logged and shown.

The conditional speech probe substitutes playback to inspect requested text,
cancellation, ownership and error handling without playing audio. Its successful
compilation is not a voice/speaker test.

The pinned native `fontSize(for:)` getter unconditionally returns Medium for
sidebar/timeline/detail UI sizing. Its setter stores a value, but the getter
ignores that value. No configurable list-size feature was invented from these
unused keys; actual platform font/layout comparison remains unverified.

### Incoming sharing and browser handoff

The package declares a Windows Share Target for `Uri` and `WebLink` only, not
arbitrary files or rich-text content. Shared URLs use the existing feed-scheme
normalization and reject file, script, theme-installation and application-action
payloads. Cold-start sharing reports receipt before account initialization;
processing remains serialized in the primary app instance. The user still
chooses an account, folder and optional name in New Feed. A typed subscription
result prevents caught creation errors from being reported as completed shares.
Cancellation reports a cancelled share rather than creating a subscription.

Packaged desktop Share Target activation is supported on Windows 10 version
2004 and later. The normal application's older minimum OS version is not a claim
that this integration works on earlier versions. OS share-broker lifecycle,
completion/window behavior and redirection between processes remain unverified
until the package can be installed and executed on an approved machine.

`Windows\BrowserExtension` contains the separate Manifest V3 Edge/Chrome source,
also included as content in the MSIX. It forwards the active HTTP(S) page URL,
just as the pinned Safari extension does; app-owned FeedFinder discovers feeds.
Only `activeTab` and `storage` permissions are used. There are no content scripts,
host permissions or background workers. The dedicated protocol is
`x-winnewswire-feed:`; the system-default-reader option uses `feed:`.

The popup requires an explicit handoff click and stores its preference locally
in that browser, not in the app's JSON preferences. It does not assert that
Windows accepted the protocol or that a feed was subscribed. No extension has
been installed into a user browser or published to a store. See its README for
source testing/loading instructions; tests are excluded from the app package.

### Metadata cache modules

`Modules\HTMLMetadata` and `Modules\Images` contain the native schema/table/model
ownership and module-local tests. They use `HTMLMetadata.db` and `ImageMetadata.db`
under the data directory. Their dates are Unix seconds, not the Apple epoch used
by feed settings. JSON columns retain native names, including `secureURL`.

HTML metadata has a 149-hour success lifetime, eleven-day 4xx suppression,
five-hour pre-response-failure suppression, a three-hour attempt gate and
thirty-day cleanup. Image failures/no-favicon verdicts retry at five days;
image-failure rows are retained for thirty-three days. The tests exercise exact
boundaries, restart behavior, stale success, write failures and cancellation.
Both stores also run native `vacuumIfNeeded` on startup, using the shared
`RSDatabaseInfo(key, value)` table and Unix `lastVacuumDate`. The interval is
thirteen days; a failed vacuum does not advance its date. Explicit vacuum remains
available independently of the startup schedule.

The UI's feed adapter uses declared icons before remembered/homepage icons and
discovers favicons through metadata before the root fallback. Failed candidates
advance, transient failures do not blacklist a homepage, and successful URL maps
are committed after image retrieval. Active files live in `Caches\Images` and
`Caches\Favicons`, named with the lowercase MD5 of the original URL and no
extension. `BinaryDiskCache` uses the supplied filename key without another hash.
Legacy `Favicons` SHA1/extension files, including older `.svg`/`.image` names,
are read on a miss and copied to the native location without deleting the original.
The standalone legacy `SingleFaviconDownloader` API remains available, but the UI
uses the module-owned pipeline. Raster responses cannot select the SVG loader
merely because their URL has an SVG extension.

`Core.WindowsImage` adapts the native `RSImage.scaledImageData` algorithm to Windows
Imaging Component without additional image-library packages. It selects the
first largest-dimension frame, keeps valid small single-frame bytes unchanged,
and extracts/resizes other sources to PNG at
`ceil(48 * WindowsScreen.MaxScreenScale())`. Palette color, alpha and EXIF
orientation are preserved. Successful HTTP bodies must actually decode before
they enter the image cache or URL maps; malformed bodies are logged and suppressed
for the native five-day failure interval. Existing oversized disk images are
resampled locally; corrupt cache entries are replaced by a new download.

These are native Windows codec tests, not signature/header-only checks. They cover
actual output pixels and multi-frame ICO/GIF behavior. They do not establish all
platform codec equivalences or prove the WinUI display path. Unsupported declared
formats fail explicitly and use the existing fallback behavior; SVG metadata
candidates remain excluded as upstream.

## Build and tests

### Required reader regression check

On an interactive Windows desktop, from the repository root in PowerShell 7:

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1
& .\Windows\Diagnostics\VerifyReader.ps1 -LiveAtlantic
& .\Windows\Diagnostics\VerifyReader.ps1 -HeaderLayout -LiveAtlantic
& .\Windows\Diagnostics\VerifyReader.ps1 -HeaderLayout -LiveAtlantic -UseInstalledRuntime
& .\Windows\Diagnostics\VerifyReader.ps1 -HeaderLayout -SidebarLayout -Navigation -UseInstalledRuntime
```

The first command uses deterministic loopback fixtures; the second also
subscribes to the live Atlantic feed in the probe's private profile and displays
every returned article. `-ResultsDirectory` accepts a new or empty absolute
directory for retained TRX, UI results, screenshots and shutdown state. The
script never stops another application instance or uses the user's profile.
Dependencies must already be restored; missing dependencies or execution
restrictions fail the gate rather than being treated as successful validation.

The initial `-HeaderLayout` run added three real-control groups: title-bar/pane geometry and input
regions, column sorting/resizing/restoration, and shared header customization,
overflow and fixed search controls. The combined run passed all 13 UI groups
(including the live feed), alongside 36 Images cases. Forty original Shared
toolbar cases also pass, including seven new header-routing/compatibility cases.
Captures cover regular, column, narrow-table and compact-title layouts. DPI
rounding is checked at 100/125/150/200%, and actual native input regions are
checked at the current desktop scale and after resizing/increasing search text.
These checks do not change the machine's DPI or establish every multi-monitor,
RTL or high-contrast combination.

The subsequent theme/acrylic regression adds a fourth header group (14 UI groups
with the live feed). It adds Article Theme at the end of the customization list,
moves/removes/re-adds it four times, saves and opens the actual theme menu, and
checks visible color contribution through acrylic in both timeline layouts.
The installed-runtime run passes alongside all 36 Images cases.
`-UseInstalledRuntime` uses the same framework-dependent bootstrap as normal
Debug instead of the probe's SDK-bundled WinUI. `runtime.json` records the loaded
DLL path/version and WebView version; the runner rejects a local bundled runtime
when this option is requested.

The earlier 2026-10-20 sidebar/navigation run executed 20 UI groups: 17 passed, but the
overall gate was **not green**. Windows Application Control denied the existing
`WinNewsWire.ActivityLog.dll` (`0x800711C7`), preventing both favicon integration
groups from completing. The sidebar batch-delete/undo group also fails; the same
failure was reproduced before changing the sidebar styling. No denied assembly
was renamed/replaced, and no signing or application-control policy was changed.
These failures remain separate from the passing requested presentation and
navigation groups.

`-SidebarLayout` checks actual light/dark selection pixels, measured original
spacing, plain unread counts, disclosure/filter restoration and count/dot/hidden
modes. `-Navigation` checks article/feed/search Back, multiple-article selection,
interleaved webpage history, form/SPA state, new-window links, script isolation,
synthetic-click rejection, visible-page Find/share data, redirects/error recovery,
independent window histories and native taskbar/small-icon pixels. Forty-seven
Shared toolbar cases and all 36 Images unit cases execute successfully.
The ordinary Debug build also succeeds in a separate output directory because
the user's standard Debug instance is running; that instance and the installed
application were not stopped or overwritten.

The column-header follow-up adds a fifth `-HeaderLayout` group. All five header
groups pass, including unchanged feed-control positions across six display/font
combinations, a title/table junction within floating-point precision of zero
physical pixels, real read-filter toggles, selection retention, failed-save
rollback and hidden-sidebar overflow. The rapid-reading regression failed before
the automatic-read/filter ordering fix; all eight cycles pass afterward. The
targeted run was 12/14 UI groups; the two favicon integration failures were
policy-blocked. All 56 related toolbar/timeline unit cases and 36 Images
cases pass. The ordinary Debug configuration compiles separately without
replacing running user instances.

The subsequent title-button/account-header polish adds a sixth header group.
Actual rendered glyph pixels verify press compression, Back's rightward shrink,
release overshoot and settling, interrupted/disabled-state recovery, fixed hit
targets and unchanged header layout. Light/dark account-row checks verify
vertical centering, inset disclosure, absence of the blue mark and retained
collapse/selection behavior. The combined header/sidebar/navigation/performance
gate passes all 24 UI groups and 36 Images cases. Ordinary Debug compiles to a
separate output directory while the user's Debug instance remains running.

### UX performance

The later 2026-10-20 performance investigation used a private 2,000-article feed
with regular/column article switching, viewport scrolling, resizing and a
16-ms UI-dispatcher heartbeat. At the start of this investigation Smart App
Control was already Off; no security setting was changed by the assistant.
The original reader/favicon cases now execute successfully.

The investigation found unnecessary work introduced by the UI changes:

- Every article selection remeasured all header commands and cleared/recreated
  their grid columns. Measurements now invalidate only for relevant content,
  display or size changes, and existing column definitions are reused.
- Title-bar input regions were recomputed on unrelated list layouts. They now
  update when title-bar/control geometry or DPI changes.
- Each visible raster icon forced layout and copied a rendered image back from
  the compositor to classify brightness. Cached WIC pixel analysis now runs off
  the UI thread, keyed by file path/modification time/size, without those readbacks.
  Concurrent requests coalesce; file changes and memory-pressure cleanup
  invalidate the cache. Vector/non-file sources retain their guarded fallback.
- Handled cancellation exceptions wrote first-chance stack traces synchronously.
  Default first-chance tracing now ends after main-content initialization and
  ignores cancellation subclasses. Unhandled and explicitly reported errors
  remain logged. Set `WINNEWSWIRE_TRACE_FIRST_CHANCE=1` before launch only when
  intentionally collecting verbose runtime diagnostics.
- Hidden publisher webpages kept running. They now suspend after WinUI's
  200-ms native-visibility delay, resume on return, and reject stale suspension
  requests during rapid Back/navigation changes. DOM/form state is retained.

Representative before/after measurements for the same workload:

| Work | Before | After |
|---|---:|---:|
| Full toolbar measurements over 32 article switches | 32 | 0 |
| Header-column rebuilds over those switches | 96 | 0 |
| Raster-icon compositor readbacks over 40 scroll steps | 269 | 0 |
| Managed allocations during those scroll steps | 5.96 MB | 3.21 MB |
| First-chance log writes for 50 handled cancellations | 50 | 0 |
| Maximum realized rows out of 2,000 | 38 | 38 |

The regular-switching main-process CPU sample fell from 2.14 to 1.06 seconds
over 16 switches; CPU and wall time vary with warm-up and other system work.
Median full reader-document readiness remained about 0.25 seconds, so these
results do not claim that WebView rendering/network latency was eliminated.
Virtualization was already functioning and was preserved.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -Performance -UseInstalledRuntime
& .\Windows\Diagnostics\VerifyReader.ps1 -Performance -HeaderLayout -Navigation -LiveAtlantic -UseInstalledRuntime
```

The focused performance command passes 36 Images cases and 11 UI groups. The
combined live run passes all 20 UI groups, including all 25 Atlantic articles,
header customization/geometry, read filtering, Back navigation, cached icon
contrast/invalidation, suspension/resume and exact persisted window state.
All 45 native-image/luminance unit cases pass, and ordinary Debug builds in its
normal output directory. `performance.json` retains raw measurements. This is
scoped performance/behavior evidence, not certification of the entire parity
backlog or a portable FPS guarantee.

### Article-body scrolling investigation

The earlier scrolling workload exercised the **timeline list**, not scrolling
inside the article WebView. The follow-up adds `-ArticleScrolling` to cover a
long image/link article, Reader View, the current and previous reading widths,
browser-generated gestures and guarded native wheel input to the private window.
It records browser animation-frame intervals, long tasks, style/layout work,
input delay, UI-dispatcher gaps and host callback counts in `article-scrolling.json`.
Native wheel input stops if foreground/pointer ownership changes; it does not
send clicks or keystrokes to other applications.

Repeated steady-scroll samples at both widths had approximately 17-ms 95th
percentile browser frame intervals on the 60-Hz test display. A first native
interaction occasionally included a 67-84-ms frame gap; that sample also includes
initial focus/input setup. Neither those observations nor the browser's frame
callbacks prove that every screen-presentation/debugger/driver scenario is smooth.
The reported sustained lag was not reproduced, and the wider article layout was
not reverted or claimed to be its cause.

The investigation did confirm unnecessary host work: around 180-204 scroll
notifications per gesture and whole-sidebar/timeline retallies on link-hover
exit. Scroll telemetry is now coalesced at 100 ms with a final `scrollend` flush,
reducing representative gestures to 27-30 notifications. Duplicate hover
messages are suppressed, and hover exit updates only detail text. Pending
document navigation rejects stale scroll messages/capture, while explicit
capture still preserves position for Back, theme changes and shutdown.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -ArticleScrolling -Navigation -UseInstalledRuntime
```

`-ArticleFixture` optionally accepts an existing absolute JSON fixture path with
`Title`, `Content`, `Url`, `ReaderMode` and optional `TextSize` fields. The runner
does not read the user's profile to create it. In this investigation a saved
article was exported through a read-only connection and exercised in a private
profile; copied fixture/profile data was removed afterward. The checks require
real scroll movement, bounded telemetry, no hover retallies, final-position
accuracy, disabled-script interactions and immediate article/Back restoration.
This is measured overhead reduction, not a claim that the reported visual lag
is fully resolved.

### Concurrent refresh and actual browser presentation

The follow-up investigation supersedes the earlier use of approximately 17-ms
JavaScript frame intervals as evidence of smooth visible scrolling. A renderer
callback can run on time while the compositor/GPU presents the scroll much later.
The earlier probe also did not start the complete application runtime and
generated wheel input from the UI thread, which could pause its input generator
along with the UI being tested.

The new workload launches an instrumented **Debug** executable on the installed
runtime, copies the isolated 50-feed/12,525-article profile, starts
`AppService.Start` (timers, notifications and memory monitoring), and sends native
wheel input from an independent thread at approximately 64 events/second. It
stops on loss of foreground/cursor ownership. Full-refresh phases reset the
private feeds' conditional-fetch/content-hash/cache state and invoke the actual
Reload button while a real publisher page or YouTube page scrolls.

Two separate causes were established:

1. **Application-side refresh work.** NetNewsWire's `ArticlesTable` caches
   immutable article instances by ID; the Windows store recreated article bodies,
   authors and attachments on every query. A traced workload sampled over 1 GiB
   of string allocation and stop-the-world GC pauses up to 188 ms. The UI also
   rebuilt sidebar image controls and replaced whole timeline collections.
   The port now caches articles with committed-write, deletion, remap and shared
   author/attachment invalidation. Memory pressure drops article bodies without
   detaching canonical status objects held by visible rows. Unchanged sidebar
   trees and timeline rows survive refresh; changes merge in place, redundant
   flat-list resets are skipped, and download bursts coalesce for 500 ms.
2. **WebView2 GPU rasterization.** Before the feed runtime started, Chromium
   `EventLatency` showed scroll-to-presentation delays up to 272 ms despite
   approximately 17-ms p99 JavaScript frames. One critical path spent about
   189 ms between receiving the compositor frame and starting its draw, behind
   a 185-ms `CrGpuMain` task associated with the publisher renderer. A
   process-local diagnostic `--disable-gpu-rasterization` comparison reduced
   scroll-presentation p99 from about 153 ms to 49 ms and maximum from 272 ms to
   50 ms. This identifies the rasterization path; it does not prove a particular
   driver defect. The flag is **not shipped or persisted**.

Final normal-hardware-acceleration measurements while all feeds were refreshed:

| Measured property | Publisher page | YouTube page |
|---|---:|---:|
| Native wheel events during the 24-second sample | 1,533 | 1,534 |
| Actual scroll-to-presentation p99 | 33.7 ms | 50.1 ms |
| Actual scroll-to-presentation maximum | 45.7 ms | 50.5 ms |
| Browser frame-interval p99 | 17.1 ms | 17.1 ms |
| Maximum UI heartbeat gap | 41.9 ms | 59.8 ms |
| Timeline replacements / sidebar rebuilds | 0 / 0 | 0 / 0 |

The corresponding pre-fix full-refresh UI gap was about 202 ms, with timeline
replacement and two sidebar rebuilds. Full-refresh managed allocation fell from
about 1,047 MiB to 629-648 MiB. Network timings vary, so these numbers are not a
network-speed claim. The distinct initial-page GPU stalls remain; feed-refresh
isolation is verified, not universal zero-jank browser rendering.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -RefreshScrolling -BrowserTrace `
  -UseInstalledRuntime -OpmlFixture 'C:\path\subscriptions.opml' `
  -ProfileDirectory 'C:\path\isolated-loaded-profile'
```

`ProfileDirectory` is copied into a new results folder before execution; the
source is not modified. `refresh-scrolling.json` records all stages.
The refresh-isolation checks require both live refresh samples, independent
native input, no timeline replacement, UI gaps at most 100 ms, frame p99 at most
34 ms and wheel-dispatch p99 at most 50 ms.

With `-BrowserTrace`, the gate now also measures actual compositor presentation
on the **first scroll and concurrent-refresh scroll of both sites**, rather than
merely collecting traces. `MeasureBrowserPresentation.ps1` pairs completed
`GESTURE_SCROLL_UPDATE` async events using scoped IDs and handles ID reuse.
`browser-presentation.json` records p95, p99, maximum and over-100-ms counts.
Each of the four samples must contain at least 100 completed updates, p99 at
most 75 ms and maximum at most 100 ms. Missing/unusable traces fail explicitly.
The retained default-GPU first-scroll traces exceed this budget; passing the
host-only checks is not sufficient to declare the browser smooth.

Existing traces can be measured without rebuilding or launching the app:

```powershell
& .\Windows\Diagnostics\MeasureBrowserPresentation.ps1 `
  -TracePath 'C:\path\initial-scroll-browser-trace.json'
```

Ninety ArticlesDatabase tests and 23 UI groups pass, including retained row
identity, visible new articles, coalesced notifications, cache rollback/remap,
memory-pressure status identity, sidebar filter exceptions, headers and browser
history. The complete Account run passed 326 cases and exposed an unrelated
existing `UnsupportedAccountFilesAreKept` expectation: it expects a replacement
default local profile, whereas `AccountManager` deliberately refuses to replace
an existing non-local `OnMyMac` profile. That assertion was not changed.

Microsoft's [browser-flags guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/webview-features-flags)
reserves these flags for diagnosis, not production defaults. The explicit
compatibility option below is therefore experimental and disabled by default,
not a supported WebView2 rendering contract. No system GPU/driver settings,
security policy or subscriptions are changed. NetNewsWire uses WKWebView and opens clicked links
externally; its backend/view-update mechanisms apply here, but it does not
provide a Windows GPU-rasterization fix for the separately requested embedded
publisher browser.

#### Further rendering isolation

The follow-up used the same 50-feed profile, real publisher page, approximately
609-by-416 CSS-pixel viewport and guarded native wheel input. The native HWND
comparison received input directly in `Chrome_RenderWidgetHostHWND`; an earlier
WinUI-window prototype that received no wheel events was rejected, not counted
as a successful performance result.

| Comparison | Presentation p99 | Maximum | Updates over 100 ms |
|---|---:|---:|---:|
| Native HWND host, ordinary GPU rendering | 106.3 ms | 246.1 ms | 8 |
| Fresh WebView2 browser profile | 158.6 ms | 253.5 ms | 15 |
| WebView2 155.0.4283.45, matching the installed Edge engine | 159.7 ms | 245.5 ms | 13 |
| Hidden article renderer completely closed | 156.9 ms | 229.1 ms | 13 |
| Fresh cache outside the session directory | 204.4 ms | 360.7 ms | 19 |
| Diagnostic shader-disk-cache disablement | 145.7 ms | 261.4 ms | 14 |
| Standalone Edge 155.0.4283.45, isolated guest window | 50.5 ms | 85.6 ms | 0 |

Earlier comparisons also retained stalls on the NVIDIA adapter, with supported
Fluent overlay scrollbars, without WinUI shadows/corners, and after reusing the
browser profile. Both Edge and WebView2 reported Intel ANGLE D3D11/GaneshGL with
GPU rasterization/compositing enabled in the matched-device comparisons.
These results do not establish a specific driver or runtime-version defect.
They do rule out replacing the WinUI host, downgrading the runtime, deleting
browser data or closing the hidden reader as demonstrated remedies.
The unsuccessful host/settings prototypes were removed.

Detailed `gpu.angle` tracing subsequently located the blocking operation:
`RasterDecoderImpl::DoEndRasterCHROMIUM::Flush` spent about 263 ms waiting on
ANGLE's Direct3D shader compilation. Two vertex `D3DCompile` tasks alone used
about 71/55 ms, with additional pixel compilations. The GPU process already had
AboveNormal priority, priority boosting enabled, and no execution-speed power
throttling. This is a shader-compilation stall, not demonstrated CPU starvation
or a need to force a different adapter.

#### Per-app compatibility rendering

**Preferences > Advanced > Compatibility rendering (experimental)** enables
software page rasterization using `--disable-gpu-rasterization`. GPU compositing
and video decoding remain enabled. It can use more CPU and depends on a browser
flag that may change in future runtimes; turn it off to return to ordinary GPU
rasterization.

The `useSoftwareRasterization` preference is off by default and persists in
`AppDefaults.json`. Changes require an application restart. `WebViewEnvironment`
captures the choice once per process and shares one environment across reader,
publisher and additional-window controls, preventing conflicting options when
opening a window after changing the preference. The normal user-data-folder
selection and WinUI application language are preserved; no browser history,
cookies or account data are deleted or migrated.

The saved setting, with **no rendering environment override**, passed the
strengthened presentation gate in Debug with all 50 feeds and 12,525 stored
articles, including full runtime startup and actual Reload-button refreshes:

| Persisted compatibility-mode sample | Presentation p99 | Maximum |
|---|---:|---:|
| First publisher scroll | 33.7 ms | 47.8 ms |
| Publisher scroll during full refresh | 33.7 ms | 46.4 ms |
| First YouTube scroll | 49.7 ms | 50.5 ms |
| YouTube scroll during full refresh | 33.8 ms | 43.9 ms |

None of those four samples had an update over 100 ms. Maximum host UI gaps
during the two forced refreshes were 60.0/79.4 ms, with no timeline replacement
or sidebar rebuild. Thirty preference tests and 27 focused UI groups pass,
including visible settings, failed-save rollback, restart notices, unchanged
running options, browser-profile sharing, navigation and toolbar behavior.
A separate long-session run passed 300 article visits, 12 webpage round trips
and closing eight main/eight secondary windows, with no retained closed windows
or models and no pending browser tasks. Managed memory plateaued near 35 MiB
after the initial fixture-cache fill; this is not a claim about every site's
own long-running JavaScript behavior.

The original GPU-rasterization stall remains reproducible when compatibility
mode is off. Recheck the actual-presentation gate after WebView2 updates rather
than assuming a diagnostic flag remains effective.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -CompatibilityRendering `
  -RefreshScrolling -BrowserTrace -UseInstalledRuntime `
  -OpmlFixture 'C:\path\subscriptions.opml' `
  -ProfileDirectory 'C:\path\isolated-loaded-profile'
```

`-CompatibilityRendering` writes only the copied/private profile's preference.
It does not set rendering environment variables or change the live user profile.

#### Wheel-animation latency and precision-touchpad limits

The later report distinguished two-finger scrolling from scrollbar dragging and
arrow keys. The previous `SendInput` workload injects **mouse-wheel events**; it
does not emulate a Windows precision touchpad. Good frame-presentation numbers
therefore did not establish finger tracking or correct inertia.

Reexamining the recorded wheel/scroll positions found a separate delay:
after the requested direction reversed, the page could continue moving the old
way for roughly 119-137 ms. A process-local comparison with browser smooth
scrolling disabled reduced the same direction-change delays to 0.7-1.6 ms.
This demonstrates additional wheel-animation lag, not a confirmed diagnosis of
the physical touchpad's complete input path.

**Preferences > Advanced > Direct browser scrolling (experimental)** provides
a separate, default-off `useDirectBrowserScrolling` preference. It requests
`--disable-smooth-scrolling`, independently of the rasterization workaround.
Both options are captured for the process's shared browser environment and
require restart. The setting can make mouse-wheel and keyboard scrolling less
animated; it does not change Windows touchpad configuration, intercept wheel
events in publisher JavaScript, or disable WinUI button animations. Like the
rasterization option, it depends on a browser flag rather than a supported
WebView2 scrolling-preference API.

The saved-setting test with all 50 feeds passed both presentation and motion
checks, with no rendering/scrolling environment override:

| Sample | Maximum direction-change response | Last-scroll tail |
|---|---:|---:|
| First publisher scroll | 1.9 ms | 0.6 ms |
| Publisher during full refresh | 1.9 ms | 0.8 ms |
| First YouTube scroll | 0.3 ms | 0.3 ms |
| YouTube during full refresh | 0.7 ms | 0.2 ms |

The largest direction-change delay across all eight stages was 16.9 ms.
An independent run with direct scrolling enabled and software rasterization
disabled measured 1.7/2.3-ms reversals. The input option does not require the
rendering workaround or silently change its saved choice.
These are **DOM scroll-response** measurements, not finger-to-pixel latency.
The separate presentation traces had p99 approximately 33.6-33.7 ms and maxima
33.9-94.2 ms. The tail is measured after the last delivered wheel event, not
after physical finger lift; OS-generated touchpad inertia is not identified
by that metric.

The probe now records horizontal deltas, delta units, control-key wheel events,
direction reversals and 400 ms of settling after injected input stops.
Pinch/control-wheel events and zero-vertical-delta events are excluded from the
vertical reversal calculation. Refresh overlap ends when input ends, not when
the settling period finishes. `InputKind = InjectedMouseWheel` and
`PrecisionTouchpadVerified = false` explicitly record the automation limit.
Actual hardware capture/confirmation is still required before claiming that
the reported two-finger behavior is resolved.

Thirty-two preference tests and 16 native-control groups pass with direct
scrolling enabled; 16 groups also pass with it off. The checks include saved
settings, rollback, restart boundaries, native Up/Down keys, actual native
scrollbar dragging and browser navigation. No tests install a touchpad driver
or synthesize unsupported touchpad contacts.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -DirectBrowserScrolling `
  -CompatibilityRendering -RefreshScrolling -BrowserTrace -UseInstalledRuntime `
  -OpmlFixture 'C:\path\subscriptions.opml' `
  -ProfileDirectory 'C:\path\isolated-loaded-profile'
```

### Interruptible wheel inertia

The later 2026-11-07 clarification keeps wheel inertia rather than removing it.
The native article list now cancels the previous manipulation at its displayed
offset before passing a reversal to Windows' normal inertial scrolling. This
also covers wheel input over the native scrollbar; touch/pen momentum remains.

Both browser panes use one replaceable easing target. New opposite input
replaces the remaining target from the current position without restarting the
animation-frame schedule. Nested scroll containers, RTL/Shift-wheel input,
page-owned wheel handlers, keyboard input, reduced-motion preferences, and
scrollbar interactions retain their own behavior. The optional direct-browser
workaround still disables animation; it is off for the requested inertial mode.

The article hook runs in the reader's trusted isolated world after document
setup, including when publisher JavaScript is disabled. Intermediate animation
frames do not send scroll-position notifications to the managed host; the final
position is reported, and navigation/shutdown still capture directly. Favicon
load state no longer changes the generated document when an article is rebound.

The focused acceptance gate passed 12 cases and 180 reversals while motion was
still coasting: native response was below 75 ms and composited browser response
below 39 ms, with 223-302 ms of coasting retained. The final functional run
passed 29 UI groups and 36 Images cases. At that stage, the separate 32-case
full-refresh stress gate still reported occasional end-to-end delays of about
87-100 ms. Those host/refresh pauses were investigated separately and are
addressed by the subsequent heavy-refresh changes below.

`-InertialScrolling -InertiaFocus` selects the targeted contract within
`VerifyReader.ps1 -RefreshScrolling -RapidScrolling`. `-ControlledScrollPages`
uses a stable article through the production renderer plus a loopback webpage,
while the populated 50-feed account remains available for refresh workloads.
Input comes from `Tools\WheelInput`, a minimal separate console process.
Composited frames are captured by a separate Node.js process over a private
loopback DevTools endpoint enabled only in diagnostic builds. No application GC
policy or system timer policy is changed.

### Heavy-refresh pause investigation (2026-11-09)

Local RSS refresh now runs in an owned background process, using the existing
HTTP, parser, account-refresh and articles-database implementations. The final
32-case rapid-scrolling gate passes without loosening its response budgets.
The portable backend still supports its original in-process refresher; Windows
selects the isolated implementation before account initialization.

The NetNewsWire comparison uses revision
`fb086eb7684e9fdb90855c4e033a6beba4325c5e`. Its `FeedParser` uses a serial background
parse queue and `ArticlesDatabase` uses a serial database queue. The Mac timeline
coalesces fetch/merge requests for 0.5 seconds, with a two-second maximum, and
formats `TimelineCellData` when a cell is requested rather than preparing every
off-screen row. Status/icon changes reload the relevant visible cells; selection
notifications compare article identities so refreshed content does not reset
the document being read. Swift's reference counting does not introduce .NET's
process-wide stop-the-world collection mechanism.
Its unbounded article queries also return sets without SQL ordering. Windows
now avoids sorting complete HTML payloads in SQLite unless a query limit
requires selecting the newest subset.

The fresh Windows baseline allocated approximately 470-552 MiB per forced
50-feed refresh. Browser animation frames stayed below 18 ms, but input
responses reached about 100 ms. EventPipe captured 60-87 ms induced full
collections requested from the native WinUI application loop. Upstream
`CCoreServices::TrackMemory` checks working-set growth on rendering frames and
can request a blocking collection; moving parsing off the UI thread alone
cannot protect input from that collection. See
[microsoft/microsoft-ui-xaml#12113](https://github.com/microsoft/microsoft-ui-xaml/issues/12113)
and the linked WinUI/runtime source. The managed trace does not distinguish
every native reference-tracker trigger.

The retained improvements are:

- Value-type XML scanner events, allocation-free empty namespace scopes,
  byte-based RSS element matching, and decoding only text actually used by the
  parser. Large character writes reserve capacity before copying.
- Reuse of already fetched article statuses instead of redundant insert/query
  pairs, without changing article identities, schema, or read/star state.
- Four admitted response bodies through completion of their parsing/storage
  callbacks. Requests can still reach their response headers concurrently;
  cancellation, callback failures, disposal, and body-read timeouts release
  permits. HTTP scratch buffers use a cross-thread pool with less than 32 MiB
  of retained capacity rather than accumulating buffers in per-thread caches.
- Demand-driven title/summary presentation and in-place updates of existing
  timeline rows. The selected document remains in place when its underlying
  article is updated, matching the Mac's identity-based selection behavior.
- Download bursts use a 0.5-second debounce with a two-second maximum before
  becoming eligible for presentation. Passive sidebar/timeline reflow waits for
  500 ms without user input; network, parsing, storage, notifications and direct
  navigation/read/star actions do not wait. Refresh completion no longer forces
  a second immediate collection rebuild.
- Metadata received from the worker is persisted atomically in one row update,
  preserving preferences edited concurrently. Result-set filtering runs off the
  dispatcher. Memory-pressure cleanup retains cache entries already owned by
  reading windows rather than reloading duplicate article bodies.

The worker shares only the unchanged article database with the reader. Its
subscription/settings workspace is private and temporary; the UI remains the
owner of the real subscription structure, edited names, preferences and
reading state. Replies carry committed article IDs and feed metadata, not
downloaded feed bodies. Targeted cache reconciliation preserves live status
objects and invalidates related author/attachment metadata. Cancellation and
shutdown drain work, parent-pipe closure stops the child, and a crashed child
is reported explicitly and restarted on the next refresh. There is no silent
in-process fallback.

The helper is deployed under `RefreshWorker\` with its own dependencies.
Keeping only its executable beside WinUI is insufficient: WinUI's package
overrides can otherwise make the helper load an incompatible JSON dependency.
Debug output and self-contained MSIX publishing both include the private
payload. The unsigned package retains `JCQTechnology.WinNewsWire` and version
`1.0.1.0`; its helper was exercised with global .NET lookup disabled.

The final complete 32-case run initially formatted 19 of over 12,000 rows.
All 150 coasting reversals, native touch-to-wheel transitions and browser
routing checks passed. During heavy refresh, native direction response stayed
below 38 ms and browser document response below 61 ms. Each refresh parsed and
committed 48 real feeds while input was still active, with about 331-398 MiB of
allocations confined to the worker. Warm UI-side refresh allocation was about
7-11 MiB. New timeline rows appeared after input settled, with no row
replacement or collection reset. Two-contact refresh presentation stayed below
36 ms. These are native injected-input measurements, not certification of every
physical touchpad, mouse driver, publisher page or unrelated GPU workload.

Earlier in-process reductions and limited parallel-GC experiments were not
sufficient; their failing reports remain retained. No GC-policy change or
global timer change is part of the final implementation.

### Rapid wheel reversals and stopping

The 2026-11-07 follow-up reproduced 190-283 ms of native article-list movement
after wheel input stopped, including delayed reversals. The list's actual
template `ScrollViewer` initially disabled wheel inertia while retaining
touch/pen momentum; this was superseded by interruptible inertia above.
Input-mode changes happen before the inner scroller handles wheel
packets, and the handlers and cached controls are released during shutdown.
Setting the attached property on `ListView` alone did not affect its scroller.

Plain article titles use the native text path instead of creating managed
formatted runs. XML character storage removes ordinary byte whitespace before
decoding, avoiding a second large string for padded feed bodies while retaining
Unicode trimming semantics. GC-mode experiments were not adopted; normal
workstation GC, rendering preferences, and the image-cache purge policy remain.
An explicit recycled-ToolTip experiment was discarded after a native shutdown
fault; the original tooltip binding remains.

The previous scrolling driver reversed only every four seconds and shared the
reader's managed runtime, so app GC could also pause input generation. The new
`VerifyReader.ps1 -RefreshScrolling -RapidScrolling` gate uses a separate,
per-monitor-DPI-aware input process, verifies zero driver GC during
each workload, and retains HWND/PID, foreground, cursor, and coverage guards.
It injects 2-8-ms wheel packets, reverses every 120 ms, and observes 1.5 seconds
after input stops. Native measurements track visible row positions, not just
virtualized extent corrections; browser presentation is aligned to the
independent injector's stop time.

Two consecutive runs passed, including a final 22-workload run covering the
native article list, real rendered feed articles, controlled embedded webpages,
standard/column layouts, unfocused scrolling, startup/full-refresh pressure,
and touch-to-wheel transitions. Input reached about 458 events/second. Across
the two runs, visible native motion stopped within 33 ms, browser presentation
within 35 ms, and direction responses within 64 ms. This is measured injected
wheel coverage, not certification of physical precision-touchpad hardware,
mouse-driver smoothing, or every publisher's scripts.

Use an already refreshed isolated profile for warm-start comparisons. The gate
reports `rapid-document-changes.json` rather than silently accepting a document
replacement when an old profile's selected article genuinely changes upstream.

### Supplied OPML and large-profile responsiveness

The 2026-11-06 cold-start/catch-up investigation reproduced 145-207-ms
dispatcher gaps during refresh. Networking and parsing were already asynchronous,
but cold image-service initialization still opened metadata databases on the UI
thread, and timeline presentation work also resumed there. The image caches'
three-day purge had run at the reported launch, making those cold paths relevant.
Allocation profiling additionally identified growing response buffers and a
whole-feed copy for salted hashing as major sources of collection pressure.

Image initialization is now awaited off-thread and drained during shutdown.
Timeline formatting uses background preparation with feed-metadata snapshots;
bound-row changes remain dispatcher-owned, and newer selections/searches win.
Only two parsed-feed/database-update pipelines are admitted at once while HTTP
downloads remain concurrent. Refresh finalization no longer resumes on the UI
thread. HTTP scratch buffers are pooled, and incremental hashing preserves the
existing content hashes without copying the complete body. Response size limits,
conditional requests, cancellation, article identity, and read/star state remain.

The expanded catch-up gate uses the retained 50-feed, 12,525-article profile and
verifies initial refresh as well as forced reparsing. It records five browser
presentation traces and additionally scrolls the native sidebar and timeline
during refresh. `-ControlledScrollPages` supplies stable loopback documents
without changing the real RSS workload; the original live-site mode remains
available. Controlled runs passed with high-resolution wheel, notched wheel,
and two-contact touchscreen input. Startup dispatcher gaps were 48-52 ms;
browser presentation p99 stayed at or below 34 ms, with a worst measured update
under 80 ms and none over 100 ms. Native sidebar/timeline gaps stayed below
74 ms. These measurements do not certify physical precision-touchpad hardware
or guarantee arbitrary publisher-page rendering latency.

A supplementary live-site run also kept startup and refresh presentation under
86 ms and native pane gaps under 77 ms. It still recorded five 100-plus-ms updates
on the first YouTube scroll while no feed refresh was running (maximum 179 ms).
That independent cold-page rendering limitation remains; the full live-site gate
is not claimed to pass.

The 2026-10-31 recurrence was reproduced before feed refresh started. The browser
GPU thread spent 300-430 ms flushing raster work and waiting for ANGLE programs;
browser-reported scroll presentation reached 637 ms. Evergreen WebView2 had
updated from 156.0.4314.50 to .58, but an isolated .50 comparison also stalled.
This is a recurring cold GPU-rasterization problem, not proof that the newer
engine alone regressed. Compatibility rendering avoids that path without
pinning an obsolete runtime or disabling GPU video/compositing. It can increase
CPU/battery cost and remains an explicit preference, not a universal new default.

A separate managed profile found about 306 ms in notification registration on
the UI thread during startup. Registration and unregistration now run off that
thread. Initial refresh awaits registration, and shutdown drains a registration
already in progress before unregistering; notification delivery and failure
reporting remain intact. In the fixed 50-feed/12,525-article workload, the maximum
startup UI gap fell from 362 ms to 81 ms. Browser-reported wheel presentation
p99 fell from 289 ms to 33 ms initially and remained 34-45 ms during publisher
and video refreshes, including first-scroll events.

Native notched-wheel, high-resolution-wheel, and two-contact touchscreen
injection are distinct diagnostic modes. Two-contact checks require two trusted
contacts in the browser and exercise startup, forced refresh and video browsing.
They do **not** emulate a physical Windows Precision Touchpad: the documented
Windows injection API supports touch/pen, not touchpad devices. The automated
results therefore do not certify physical touchpad hardware.

The repeated touch-trace timeout is resolved. Routine recording now enables
only input-scrolling/latency events instead of all renderer tasks; the detailed
renderer/GPU categories remain opt-in. Both consecutive captures and the full
eight-phase touch workload pass with all four presentation traces, including
first-scroll and inertial updates. The final touch gate measured presentation
p99 of 33.6-33.8 ms and maxima of 34.8-49.9 ms, with no updates over 100 ms.
`VerifyReader.ps1 -RefreshScrolling -ScrollInput TwoContactTouch` exposes this
repeatable gate; `NotchedWheel` and the default `HighResolutionWheel` select the
separate native wheel drivers.

The 2026-10-25 acceptance run imported the supplied 50-feed OPML through the real
Windows import command into an isolated profile. It produced 12,525 stored
articles, with 951 initially unread. All 50 subscriptions, imported titles, the
on-disk OPML mirror and export survived; re-import preserved feed identities and
did not duplicate subscriptions. The largest feed displayed all 2,846 stored
articles and was scrolled with native mouse-wheel input in both layouts.
Some endpoints remain unavailable (one HTTP 403, one HTTP 404 and one without
an HTTP response); those are per-feed failures, not lost subscriptions.

The import comparison used NetNewsWire's `Account.importOPML`,
`LocalAccountDelegate.importOPML`, `OPMLNormalizer`, `OPMLFile`,
`FeedSettingsDatabase` and timeline download-notification handling. The fixes are:

- Unsupported downloaded content now reports a failed feed activity instead of
  aborting the whole import refresh. `InvalidDataException` is not an
  `IOException`, so the previous filter missed this path.
- New OPML feed-settings rows are initialized completely in one database
  transaction. The 50-feed regression previously performed 150 row
  inserts/replacements; it now performs 50. Existing settings, folder membership,
  imported-title rules and synchronous error reporting are unchanged.
- Successful import resets the article-fetch start date before refresh, matching
  the native history-fetch contract.
- Download notifications refresh a window only when its active feed/folder/account
  scope is affected, or when it represents a smart feed/global search. Unrelated
  feed updates no longer rebuild the current timeline/sidebar while reading.

The sampled UI-thread stack located approximately 726 ms inside OPML
`NewFeed`, including about 521 ms of synchronous settings inserts. The largest
UI heartbeat gap during a fresh import fell from approximately 1,047 ms to
113-153 ms. Total network/import time stayed around 16-17 seconds; this is a
reduction in blocking UI work, not a claim of a tenfold network speedup.

Actual embedded YouTube, Substack and Long Lead pages were opened and scrolled.
The final run measured approximately 17-18 ms p95 browser frame intervals; warm
passes had no frames longer than 33 ms. Cold passes still had individual
67-83 ms frames (earlier passes reached 217 ms), with publisher-side script/task
work. Toolbar/layout counters did not churn during those scrolls. No scripts,
video features or page content were disabled to manufacture these results;
only the private browser's audio was muted.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -ToolbarControls -UseInstalledRuntime `
  -OpmlFixture 'C:\path\subscriptions.opml'
```

The command uses a private profile and a 15-minute bounded timeout for live-feed
coverage. Results are in `opml-performance.json`, alongside screenshots and the
usual reader results. Eleven UI groups, 36 Images cases, 70 Account/OPML/settings
cases and 14 OPML parser cases pass. Ordinary Debug compiles separately without
replacing the running user instance. These are measured workload results, not
universal guarantees for every publisher page, GPU or long-running session.

The read/unread toolbar uses same-size Fluent textbox/textbox-checkmark icons. The
window root also suppresses automatic accelerator hover hints; native F11
fullscreen entry and return still work. This fixes the window-wide `F11` popup
without disabling the shortcut or useful control-specific tooltips.

Folder disclosure updates only the changed contiguous range of sidebar rows
instead of clearing and repopulating the whole list. On My PC is now a static
heading aligned with Smart Feeds. Newly revealed visible folder children fade
from transparent while moving downward by 12 DIP over 220 ms; the surrounding
rows and sidebar do not fade. Default list insertion transitions are disabled
so they do not compete with this row-only effect. Rapid collapse/re-expansion,
container recycling, and window shutdown cancel and clean up the owned
storyboards. Windows' reduced-motion setting suppresses the animation. Hiding a
selected folder child still selects its parent, and legacy folder/filter state
continues to restore.

### Long-session retention investigation

A passive sample of the user's 4.5-hour-old process showed an approximately
8.5-MB managed heap, no GC pauses, and no thread-pool backlog. This did not prove
that the reported late-session scrolling was a managed GC problem. Heap
inspection and forced collections were performed only in private, synthetic
profiles; the live user process was not restarted or heap-dumped.

Repeated navigation and window lifecycle checks did expose genuine retention:

- The summary cache kept complete article bodies alive to validate its
  300-character previews. It now weakly references the body, preserving content
  invalidation without retaining discarded HTML. The new collection regression
  failed before the change and passes afterward.
- Native Window event callbacks retained closed windows and their entire reader
  graphs. Activated/Closed/Closing handlers and close-await callbacks now detach
  explicitly, including the Closed callback itself. The same ownership rule
  applies to preferences, diagnostics, statistics and cleanup windows.
- Window-root `x:Bind` for statistics/cleanup resize handles generated another
  retaining `Window.Activated` callback. Named-element bindings remove that
  generated subscription; actual column references are verified.
- The application now releases its owned taskbar/small-icon handles on close.

```powershell
& .\Windows\Diagnostics\VerifyReader.ps1 -LongSession -Navigation -UseInstalledRuntime
```

The final workload covers 300 article visits, 12 webpage round trips, eight main
window closes and eight secondary-window closes. All closed window/view-model
references clear after collection (the initial run retained all eight closed
main windows). Reader browser state stays at one document, 412 fixture nodes,
nine event listeners and approximately 0.75 MB of script heap; pending browser
tasks drain. Steady article scroll samples remain near 17-ms 95th-percentile
frame intervals, including after the final batch. Native reader history reaches
its existing 50-entry bound; application Back history is intentionally retained,
not silently capped or cleared.

There is still native GDI-resource growth during repeated window creation, not
during the article/web navigation stages. It is recorded in `long-session.json`
and is not claimed fixed by the managed-lifetime repairs. These are bounded
retention/regression results, not proof that every hours-long scrolling scenario
is resolved. The combined run passes 14 UI groups and 36 Images cases; all 23
formatter cases pass. Temporary synthetic-process heap dumps were removed.

### Earlier crash repairs

The reported customization crash at 12:02 on 2026-10-19 was a native access
violation (`0xc0000005`) in WinUI `3.1.8.2608`, not the earlier
`0x800711C7` assembly-policy denial. Matching Microsoft symbols resolve RVA
`0x37b7ba` to separated code in
`DirectUI::ModernCollectionBasePanel::DoScrollItemIntoView`.
The dialog's insert/move handlers previously requested that virtualized scroll
immediately after changing the list. Article Theme had not been saved, and this
failure occurred before applying a theme. The bounded non-virtualized list and
realized-container scrolling remove that path.
The repaired standard Debug executable also started with a responsive main window
using installed WinUI `3.1.8.2608` and closed normally; this repair did not change
signing or policy settings. This does not guarantee that future unsigned builds will never
receive another application-control denial.

The reader repair was reproduced before changing production code: the WebView
cancelled host-authored `data:text/html;charset=utf-8;base64,...` navigations,
five favicon-fallback cases failed, and real-control tests detected replacement
of both timeline collections and selected rows on read/star changes. All 25
Atlantic article bodies were still present in the user's database, inspected
read-only; no profile repair or destructive migration was needed.

After the fixes, the full Images suite executes 36 passing cases. The focused
reader gate passes nine deterministic UI groups, or ten with `-LiveAtlantic`.
It verifies rendered content, publisher-script policy, rejected
unrequested navigation, actual WinUI favicon pixels, cross-window status
propagation without row replacement, retained unread-scope rows, and delayed
favicons without a second document navigation or scroll reset. An explicit live
run rendered all 25 Atlantic articles and displayed its actual timeline/header
icons. The runner also requires successful process exit and persisted window
state matching the pre-close capture. A queued background read/unread case
ensures UI notifications do not replay stale flags into committed statuses.
The gate's rejection of an exit-zero, zero-test TRX was checked separately.
WebView PNGs retain transparency; view them against the reader's background.

Normal Debug and Release builds succeeded. Debug output was redirected to a
separate build directory because the user's running instance held its normal
binaries. No user debugging session was stopped, and no existing subscription
or article data was rewritten. Rebuild and restart the normal Debug destination
to load all final changes.

An older self-contained probe run exposed a separate native snapshot failure in
WinUI `RenderTargetBitmapImplUsingSpriteVisuals::PreCommit` (public symbols,
offset `0x945d1d`, WinUI `3.1.8.2602`). The icon check now samples a distinctive
fixture pixel from the actual timeline window using `PrintWindow`, instead of
capturing and immediately tearing down a temporary `RenderTargetBitmap` dialog.
This strengthens the display assertion and removes that test-only crash path;
it is not a claim that the underlying WinUI version was repaired.

A prior broader probe attempt failed the toolbar search-focus assertion and did
not complete the browser-preference check. The header redesign now passes the
updated real customization/search-focus checks; the browser-preference check and
other broader parity areas remain outside this signoff.

### Other build and test commands

From the repository root:

```powershell
dotnet test WinNewsWire.Tests.slnf --nologo
dotnet build Windows\WinNewsWire.csproj --nologo -p:Platform=x64
```

The last broad allowed-project run executed **878 cases across twelve projects**,
all passing with no skips. A later account-only run passed 289 cases; four new
cache-cleanup and three new statistics/vacuum database cases passed separately.
Earlier Windows Application Control restrictions blocked
`Parsers.Tests.dll`, `Account.Tests.dll`, `WinNewsWire.Tests.dll` and the then-current
`WinNewsWire.dll` application (`0x800711C7`).
VSTest can exit zero after skipping discovery entirely. Check actual execution
counters, and do not bypass the policy. The last complete thirteen-project run
passed 1148 cases before subsequent work. These counts do not measure
upstream-function parity.

The latest affected permitted full runs passed **295 Core, 37 ActivityLog, 10 Database,
83 ArticlesDatabase, 7 SyncDatabase, 9 ErrorLog, 29 HTMLMetadata, 31 Images
and 47 RemoteAccounts cases**, with execution/pass/skip counters checked.
Prior relevant Web and NewsBlur runs passed 99 and 9 cases respectively.
An additional targeted Web run passed **16 HTTP-fixture cases**, retaining all ten
native fixture functions plus six Windows regressions; this is not a new full
Web-suite count. `Web\WebServices\TestingHttpMessageHandler` uses explicit
per-handler response/history ownership instead of global task-local test IDs.
Two Account-owned scope equivalents compile but remain execution-blocked.
These include twenty-eight real-image codec/scale cases, twenty-eight binary
cache cases and fifteen remote-client lifetime cases.
The earlier two account metadata-notification cases and seven new account-level
ownership cases and an additional feed-settings initialization case compile but
have not executed in the blocked Account assembly. Shared cache cleanup/theme
integration cases likewise remain compile-only.
Restart fixtures use fresh delegates with isolated transports, not disposed APIs.
The solution filter now contains sixteen test projects; its previously blocked
assemblies retain their original identities and must not be run through substitute loaders.

The original Shared reader/favicon subset (23 cases), original Atom suite (30
cases), and current reader UI probe have now executed under their existing
identities without policy changes. This supersedes the earlier execution block
for these specific checks, not the unexecuted Account cases or every parity area.

The conditional Windows probe exercises real WinUI and WebView2 in a fresh private
profile, including the original measurement matrices, themes, reader mode,
script policy, add-feed dialog, multiple windows, memory cleanup, statistics,
sidebar selection, native column layout, full screen and isolated reader input.
Probe 51 passed all **nineteen groups**, exited zero and left a matching captured
one-window session. Earlier two-window Quit comparisons also passed. Probe 52,
which adds browser-preference/navigation UI coverage, was blocked before `App`
construction. The earlier thirty-four-group probe added cached-image/bitmap/visible
icon-pixels coverage, native sidebar drop-policy/mixed-transfer/default-destination
coverage and multi-selection URL menu/clipboard-error coverage.
Those groups were initially compiled only. Current reader-specific evidence is
recorded above; the full probe is not green and does not simulate
pointer-driven dragging. These checks do not verify the blocked adaptive-sync tests. Build it
separately:

```powershell
dotnet build Windows\WinNewsWire.csproj -c Probe -p:ParityProbe=true `
  -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishReadyToRun=false
```

Run the resulting `Windows\bin\x64\Probe\net8.0-windows10.0.19041.0\win-x64\WinNewsWire.exe`
with a new absolute output directory as its argument. Add `--quit` to exercise
two-window Quit instead of last-window system close. Inspect every group's
`Passed` value in `results.json` and the process exit code. After exit, compare
`expected-windows.json` with `profile\AppDefaults.json`'s `mainWindowStates`.
Probe code is excluded from normal builds; a build alone is not runtime verification.
Execution requires an approved execution environment/signing path;
do not use another configuration, renamed assembly or loader to evade the block.

### Microsoft Store upload package

The Store release uses the existing `JCQTechnology.WinNewsWire` identity and
publisher from `Packaging\Package.StoreAssociation.xml`. Before a new release,
choose an unused `major.minor.build.0` version above every version already
published or submitted in Partner Center, and update
`Packaging\Package.appxmanifest`. Do not reuse `1.2.0.0`: the Store-signed package
installed on the review host on November 17, 2026 already has that version and
identifies the older `106603d` baseline. Rebuilding current sources with the same
package version does not provide those users an update.

The guard below rejects the known distributed version and older versions; it
does not check Partner Center for higher versions that may already be reserved.
After confirming the new version, build all three configured architectures
from the repository root:

```powershell
[xml]$manifest = Get-Content -LiteralPath 'Packaging\Package.appxmanifest'
$packageVersion = [version]$manifest.Package.Identity.Version
if ($packageVersion -le [version]'1.2.0.0') {
  throw 'Choose an unused Store version newer than 1.2.0.0 and all versions already submitted in Partner Center.'
}
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$output = Join-Path $PWD.Path ("Packaging\AppPackages\Store-{0}\" -f $packageVersion)
$previousRuntimeIdentifiers = $env:RuntimeIdentifiers
try {
  $env:RuntimeIdentifiers = 'win-x86;win-x64;win-arm64'
  & $msbuild 'Packaging\WinNewsWire (Package).wapproj' /restore /t:Rebuild /m:2 /nr:false /nologo `
    /p:Configuration=Release /p:Platform=x64 /p:SelfContained=true /p:ParityProbe=false `
    /p:AppxBundle=Always '/p:AppxBundlePlatforms=x86|x64|arm64' `
    /p:UapAppxPackageBuildMode=StoreUpload "/p:AppxPackageDir=$output" `
    /p:AppxPackageSigningEnabled=false /p:AppxAutoIncrementPackageRevision=false `
    /p:GenerateTemporaryStoreCertificate=false /p:GenerateAppInstallerFile=false `
    /p:AppxSymbolPackageEnabled=true /p:PublishReadyToRun=true /p:PublishTrimmed=false
  if ($LASTEXITCODE -ne 0) { throw 'Store package build failed.' }
}
finally {
  $env:RuntimeIdentifiers = $previousRuntimeIdentifiers
}
```

The restore must include all runtime identifiers for the backend libraries and
refresh worker, not only the WinUI project. Rebuild packaging intermediates when
changing versions: an incremental build can retain an older inner MSIX manifest
even when the bundle and upload filenames have the new version. Verify the
identity, version and native architecture inside each application package.

Upload the newly generated `.msixupload` from
`Packaging\AppPackages\Store-<version>\` to the existing app's Partner Center
submission, not an older upload left in another version's directory. It contains
the architecture bundle and three symbol packages. Both the reader and its private refresh worker
include their .NET runtime; Windows App Runtime and VC runtime framework
dependencies remain declared for Store deployment. No test certificate is needed
for this upload: Microsoft signs the distributed packages. Store certification
still happens in Partner Center; a local build is not certification.

### Unsigned single-architecture package

Unsigned x64 Release packaging uses Visual Studio MSBuild:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
& $msbuild 'Packaging\WinNewsWire (Package).wapproj' /restore /m /nologo `
  /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
  /p:AppxBundle=Never /p:AppxBundlePlatforms=x64 `
  /p:AppxPackageSigningEnabled=false /p:AppxAutoIncrementPackageRevision=false `
  /p:GenerateTemporaryStoreCertificate=false /p:GenerateAppInstallerFile=false `
  /p:PublishReadyToRun=false /p:PublishTrimmed=false
```

The explicit runtime identifier is required for the packaging restore to cover
referenced projects. A standalone test-project restore can replace a library's
assets file without its `win-x64` target; restore the missing target before
retrying a `NETSDK1047` packaging failure. Trimming is disabled because account settings and service
DTOs use reflection-based JSON serialization; enabling it requires complete
serialization metadata and separate runtime coverage. The resulting MSIX contains the app,
manifest, and upstream MIT notice. It was not installed over a user's application.

Output:
`Packaging\AppPackages\WinNewsWire (Package)_1.2.0.0_x64_Test\WinNewsWire (Package)_1.2.0.0_x64.msix`
The archive is inspected for the executable, main assembly, manifest and MIT
notice, and for exclusion of test assemblies and the probe type. A successful package build is
not a substitute for the blocked tests or an installed-app runtime check.

The latest compile-only package is inspected for its app/manifest/attribution
entries, metadata modules and exclusion of tests/probe code. Notification COM
registration must point to `WinNewsWire\WinNewsWire.exe`: packaging does not
replace `$targetnametoken$` inside the COM `ExeServer` element. The package is
not installed and was not used as an alternate execution path during the earlier
application-execution restrictions.

Latest inspected artifact (2026-10-19, before the sidebar/Back/web-view/taskbar
changes): 47,911,588 bytes, 342 entries, SHA-256
`33E176C943FEEB74CECE64CC9A33D069A66644ED2648CCCAA4A2A8C6B315B198`.
The reader repair's pending-document guard, header-icon-only update and
in-place status handlers are present. Probe types, test assemblies and the
diagnostic runner are excluded from the archive.
The pane-header update also includes the title-bar input-region/DPI methods and
header-control routing. Normal Debug and Release builds pass; the package was
inspected but not installed over either running user instance.
The newest archive contains realized-container customization scrolling and dynamic
acrylic-header insets; its probe type and test-only runner remain excluded.
Both metadata assemblies, the seven protocols, two file types and matching
notification COM executable/CLSID are present. Reading PE metadata without
loading the assemblies verifies the current native Find, unread navigation,
smart-scope/Help, preference migration, native retention, toolbar and Dinosaurs
types/methods. Both packaged
attribution copies match the source bytes. Test assemblies and conditional probe
hooks are absent; the explicitly injected Web fixture helper is a module API,
as in the native reference, not a packaged test assembly.
The latest archive also includes the Current Activity/share-receiver types and
eight browser-extension content files matching source hashes. Share Target
formats are exactly `Uri` and `WebLink`, alongside the existing seven protocols
and two file associations. Node tests and test-only .NET/probe assemblies are
not packaged. This is archive/metadata evidence, not installed Share Target or
browser-extension verification.
The automation client/README match source bytes in the package. Metadata
inspection confirms the scripting host, object service, native pipe factory
and remote-client rejection flag without loading any application assembly.
The newest archive also contains the speech service and menu methods while
excluding the playback substitution and probe type. No audio was played and no
application assembly was executed during this inspection.

The direct-debug startup repair adds the official Windows App SDK bootstrap
initializer and its managed/native dependencies. Compiled Debug IL confirms the
module constructor invokes bootstrap before `Main`. The MSIX build retains its
Windows App Runtime dependency and uses the package-identity no-op option;
self-contained probe initialization is unchanged.

An SDK-only activation check reproduced `0x80040154` for
`Microsoft.UI.Xaml.Application` before bootstrap and returned `S_OK` afterward,
using the same SDK release, minimum runtime version and initialization options.
It loaded only the signed SDK library, not application assemblies. Read-only
inspection of an existing user-launched normal Debug x64 instance subsequently
confirmed a responsive WinNewsWire window, both bootstrap libraries and WinUI
loaded from the compatible installed Windows App Runtime. This verifies the
reported startup failure on that debug path, not the blocked parity probe or
deployed activation. A full follow-up build encountered files locked by that
active debug session; the solution's `WinNewsWire:Compile` target succeeded
without replacing its live binaries. The session was not stopped or modified.

Both Windows and Packaging reference WebView2 SDK 1.0.3405.78 explicitly.
Without the packaging reference, MSBuild selected the older primary projection
and reported MSB3277 while still creating an MSIX. That conflict is fixed.
The rebuilt archive contains root and app-local projection assemblies, both
verified as 1.0.3405.78 with the required Find methods. Neutral strings are also
present in the shared assembly. No application or packaged DLL was loaded during
this metadata inspection.

On the verification machine, Debug packaging lacks
`Microsoft.UniversalCRT.Debug, Version=10.0.26100.0`. A prior Release restore
reported NU1701 for `plist-cil` under the packaging project's legacy UAP restore;
the application/backend consumers target .NET 8. The previous HtmlAgilityPack
NU1605 warning is absent. Timer tests await their work and use isolated scheduler
instances instead of opening the shared account manager.

## Compatibility boundaries

The 2026-10-31 Windows upgrade check used the actual storage assemblies from
released baseline `75e2c32`, not a hand-written approximation. Three profiles
(complete OPML/JSON, newer JSON than OPML, and JSON-only) each contained two local
accounts, 51 feeds, three folders including an empty folder and shared feed
memberships, and 204 cached articles. The current code preserved account/feed/
folder IDs, memberships, edited titles, preferences, cached content and read/star
state both on upgrade and on a subsequent reopen; SQLite integrity checks passed.
The existing migration/account/OPML subset also passed 51 cases.

Source folders have been reorganized, but the data-directory code and article
schema are unchanged. `AccountPreferences.json` and `FeedSettings.db` add metadata
storage while the legacy subscription files remain readable. Package name,
publisher and application ID are unchanged. This verifies the storage upgrade,
not a Store/MSIX deployment or remote-service login; ensure the generated package
version exceeds the published version when releasing.

Existing account IDs, article IDs, database locations, and DPAPI storage locations
are preserved. Existing URL-based remote feed IDs remain readable. Feedly repairs them to
canonical service IDs with a replayable article-reference migration.

This is not full binary compatibility with a Mac NetNewsWire profile. Feed settings
match its SQLite schema and plist import contracts, while Windows account preferences,
article databases and DPAPI credentials still use explicit platform adapters. Thirty-day
upstream feed-retention deletion is implemented as an explicit update option, but
was not switched on for existing local accounts. Old remote profiles that never
saved a credential username can use the explicit reconnect path; no username is
guessed from the account ID.
Image caches use native MD5 names with legacy Windows compatibility reads.
SQLite journaling now matches the pinned native DELETE mode; this does not by
itself establish full schema or Mac-profile binary compatibility.

The XML scanner is liberal like upstream, but OPML keeps Windows' strict
whole-document validation before building the tree. This intentionally prevents
partial subscription imports from malformed files. Foundation code-page decoding
uses .NET encodings, dates outside .NET's years 1-9999 cannot be represented, and
Markdig supplies the platform Markdown renderer. Parsed Markdown now overrides
HTML at the same model boundary as upstream. Known legacy parser-ID compatibility
and the corresponding local content-hash version bump are implemented.

Theme extraction additionally limits total expanded contents to 10,000,000 bytes
on Windows, matching the upstream download-size budget while preventing compressed
archives from exhausting disk space. This is an intentional Windows safety bound,
covered at the exact limit and one byte above it. Inoreader app registration keys
can be supplied as `INOREADER_APP_ID` and `INOREADER_APP_KEY`; private upstream
application keys are not copied or invented.

Live account authorization, macOS XCTest execution, visual parity, and native
file-picker/cloud-storage UI behavior have not been verified by the backend suite.
Timeline multi-selection and command semantics have backend coverage. The isolated
probe verifies selected actual controls, statistics, sidebar selection and window
restoration, not the full pointer, accessibility or OS-integration matrix. The
native column layout, full-screen and isolated reader interaction have separate
runtime checks. Remaining gaps include broader pointer/accessibility coverage,
browser UI/OS launch verification, deployed activation, image display and the broader
platform codec/display matrix. The
application execution block prevents current runtime verification from continuing.
