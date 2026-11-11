# NetNewsWire parity

## Reference and scope

The reference is Ranchero-Software/NetNewsWire at
`fb086eb7684e9fdb90855c4e033a6beba4325c5e`, checked out at `D:\NetNewsWire`.
The clone uses sparse checkout to exclude `AppStore`, whose screenshot names
contain characters Windows cannot create. All application code and tests are
available. The reference is MIT licensed; see `THIRD-PARTY-NOTICES.txt`.

The target is the Mac application's behavior, with WinUI replacing AppKit and
Windows equivalents for platform services. Similar-looking screens and passing
smoke tests are not evidence of backend or test parity. `PORT_STATUS.md` records
the current layout and commands; `TEST_AUDIT.md` maps all current upstream test
suites. The older `PORT_SPEC.md` is explicitly historical.

Baseline: all 295 existing tests pass, but several current upstream suites and
individual assertions are absent. In particular, the existing OPML tests do not
exercise the actual menu command, account selection, post-import refresh,
multi-folder subscriptions, or restart behavior.

## Reader regression gate

Preserving the existing reader is a prerequisite for further parity changes.
Run `Windows\Diagnostics\VerifyReader.ps1` before and after changes to feed
loading, images, timeline events, article rendering or WebView navigation.
It executes the image suite and real WinUI/WebView2 checks in a new private
profile. A build, zero discovered tests, missing UI results, a native crash,
a timeout or a failed shutdown/persistence check is not a pass. Add
`-LiveAtlantic` for an explicit network-backed subscription/read-through check.
Do not continue unrelated parity implementation while this gate is failing or
cannot execute.

The Atlantic regression exposed three errors in the parity work: a navigation
guard rejected the `data:` URI emitted by `NavigateToString`; favicon lookup
lost the previously working root-icon fallback when homepage metadata was
unavailable; and read/status notifications were treated as subscription changes,
rebuilding the sidebar and timeline. The feed download and stored article bodies
were intact. Late icon arrival also reloaded the article document unnecessarily.

The fixes admit only the pending app-authored document, preserve root-icon
fallback without caching a failed homepage as "no favicon", update status
indicators on existing rows, and replace only the header image when its icon
arrives. UI notifications read committed status objects rather than replaying
possibly superseded flags back into them. Native
`TimelineViewController.statusesDidChange` likewise updates
visible cells rather than refetching the timeline. Keeping the Windows fallback
when a publisher blocks homepage metadata is an intentional compatibility
improvement over copying that native transport assumption.

Earlier renderer tests asserted generated HTML, favicon fixtures supplied
explicit icon URLs, and status checks asserted values without checking row
identity or collection resets. The existing UI probe would have detected the
blank reader, but implementation continued after its execution became blocked.
Recording "compiled only" was not an adequate regression gate. The new checks
exercise subscription/download/storage/display together, denied homepage access,
actual timeline icon pixels, read/unread row retention, cross-window status
updates, a single document navigation, and retained reading position.

## Discrepancies, easiest to hardest

The requested Windows presentation and browsing choices are intentional
exceptions to literal Mac UX parity. Preserve static, aligned account headings,
inset folder chevrons and icons, smart-feed glyphs, folder-only fade-down
disclosure, local manual feed ordering, pane-based
command headers, title-bar Back/sidebar/title/
search order, and original taskbar icon. Refresh, Add and feed filtering remain
in the sidebar in every layout; only timeline/article commands combine above
the column labels, without a divider at that junction. Ordinary article links now open in a
separate embedded web browser; explicit Open in Browser remains available.
Window-local Back spans article/feed/search selections and native webpage
history without exposing reader host bindings to websites. Version-1 toolbar
preferences migrate compatibly to add Open in Web View; later customization is
saved as version 2.

The earlier scoped run was limited by ActivityLog application-control denial.
The later performance investigation found Smart App Control already Off and
passed all 20 selected live-reader/header/navigation/performance groups, including
favicon integration, without changing security settings. The subsequent
title-button/account-header polish passes 24 header/sidebar/navigation/performance
groups, including the previously failing batch-delete/undo fixture. These runs
do not certify the broader parity backlog. See `PORT_STATUS.md` for measurements
and execution boundaries.

Effort order reflects implementation dependencies as well as code size.

| Rank | Discrepancy | Upstream evidence | Result |
|---|---|---|---|
| 1 | Stale test inventory; no backend-only solution entry point; missing upstream license notice | Module test targets and `LICENSE` | Done: revision-pinned audit, `WinNewsWire.Tests.slnf`, MIT notice included in source and packaged output. |
| 2 | Missing portable utility/parser assertions; invalid XML control characters; incorrect NBSP handling | `RSCore` and `RSParser` tests | Portable utility fixes and all 330 current parser functions are ported, with original parser fixture blobs and production scanner/delegate integration. |
| 3 | OPML nesting, shared membership, imported names, and placeholder export names differ | `Account\OPMLNormalizer.swift`, `Account.swift`, `Feed.swift`, `Folder.swift`, `AccountOPMLLoadTests.swift` | Done for the covered contracts: one-level organization, additive membership, persistent edited names, deterministic valid XML, private-attribute handling, restart and failed-write recovery. |
| 4 | Import/export commands bypass account choice, delegates, errors, and refresh | `Mac\MainWindow\OPML`, `Account.importOPML`, service delegates | Implemented: remembered account choice, visible errors, cancellation, delegate import, refresh, and sidebar updates. Native service HTTP contracts and import round trips are tested; native picker/cloud-provider UI is not automated. |
| 5 | Timeline ignores sort/group settings, search bypasses storage, and Ctrl+A is destructive | `Shared\Timeline\ArticleSorter.swift`, `Mac\MainWindow\Timeline` | Done for these defects: five window-local sort keys, legacy grouping migration, real full-text indexing/search, preserved selection/folder expansion, and corrected accelerator. Selection survives an in-flight refresh without being replaced by its earlier snapshot. Broader timeline parity remains below. |
| 6 | Storage regression suites are absent; acknowledgements can lose failed statuses; Today uses midnight | Current `SyncStatusTable`, `TodayQueries`, `ArticleUpdate`, and `StatusDivergence` tests | Ported and passing. Added canonical status repair, update change sets, rolling-day queries, fractional-date persistence, keyed acknowledgements, and pending-edit protection. Full database/schema parity is not claimed. |
| 7 | Shared/backend code is nested under the UI; duplicate shell backends obscure ownership | Root `Modules`, `Shared`, `Mac`, `Tests` | Done: top-level modules with `Sources`/`Tests`, separate `Shared`/`Windows`/`Packaging`, services consolidated into Account, backend-owned delegate factory, updated references, and removed unused duplicate implementations. |
| 8 | Remote imports, account reload, capabilities, and subscription management differ | `AccountDelegate.swift` and service delegates | Native mutations, history/status reconciliation, conditional Feedbin snapshots, Feedly discovery/initial articles, async undo, reconnect and client/delegate resource ownership are implemented. Account-level lifecycle reruns and live-service verification remain open. |
| 9 | Settings migrations, SAX parsing, activity log, operation queues, and rendering differ | Module sources/tests and `Tests\NetNewsWireTests` | Settings/importers, scanners, ActivityLog, operation scheduling, parser-ID compatibility and the shared renderer are integrated. Original attributed-text and measurement cases have Windows counterparts. |
| 10 | Native Mac integrations cannot be copied literally to Windows | `Mac`, Apple frameworks, platform tests | Still open: requires explicit Windows equivalents and platform/service access. |

## Full parity contract

Parity means the same observable result, state transitions, errors and persisted
data for the same inputs, not identical Swift/C# syntax or an identical operating
system shell. Every supported feature needs a production path, equivalent
assertions and execution evidence. A disabled placeholder or an isolated helper
test does not complete the feature.

This inventory is based on the pinned module/test trees, Mac main menu,
preferences, sidebar/timeline/detail controllers and platform entry points.
It expands the previous eight broad backlog items into independently checkable
work. It is the required scope, **not a claim that every implementation has
already received a line-by-line audit**. Newly discovered discrepancies must be
added rather than hidden inside a passing case count.

| Required area | Contracts that must match | Current boundary |
|---|---|---|
| Source ownership | All 17 native modules; shared application logic outside WinUI; matching model/delegate/store ownership and dependency direction. | Sixteen native modules have Windows counterparts; `CloudKitSync` does not. `Core`/`Parsers`/`Database`/`Tree`/`Web` are the explicitly renamed RS modules. Windows `Markdown` is a Tidemark adapter, not an extra native module. Full API/algorithm comparison remains P23. |
| Exact tests | Every one of the 81 inventoried files and 713 declared non-performance functions: parameters, fixture bytes, assertions, async/error behavior and platform substitutions. | Suite mapping is in `TEST_AUDIT.md`. Declared functions are not xUnit case counts. HTTP harness, scripting and CloudKit differences need explicit dispositions, not blanket "ported" labels. |
| Feed input | RSS, RDF, Atom, JSON Feed, RSS-in-JSON, encodings, dates, entities, HTML discovery, redirects and calculated identifiers. | All 330 native parser functions translated; the original 30-case Atom subset has since executed, but a current full parser rerun remains unverified. Legacy Windows IDs must remain stable. |
| OPML | Parser nesting versus one-level account normalization; names, empty folders, shared/root membership, private attributes, escaping, ordering, atomic export, additive import, restart and errors. | Backend contracts implemented; real picker/provider/cancellation and complete deployed entry-point verification remain P10/P26. |
| Accounts and subscriptions | Permanent default local account; ordering; add/rename/enable/reconnect/remove; capabilities; folders; feeds; credentials; service mutations; failed writes and ownership. | Provider implementations exist. Default identity/removal and external-drop fallback are implemented in P02/P03 but still need original Account/UI execution. |
| Sync | Local refresh; Feedbin, Feedly, NewsBlur, FreshRSS, Inoreader, BazQux and The Old Reader; paging, ID mapping, status queues, retries, conditional requests, polling, cancellation and shutdown. | Fake-service coverage is not live-provider proof. Latest account-level polling/lifetime execution and live sessions remain P21/P32. |
| Durable storage | Articles, authors, attachments, statuses, FTS, feed/account preferences, separate sync store, migrations, date epochs, retention, maintenance and crash recovery. | Current fixture-tested improvements do not authorize deleting existing history. Retention and full-profile verification remain P22. |
| Sidebar | Static non-selectable headings, smart/folder icons, independent feed selection, folder disclosure, unread filters/counts, manual feed order, context actions, rename, delete/undo, internal/external drops, cross-account copies, service constraints and keyboard navigation. | Native same-account reorder/folder drags, persistence, undo/redo, and provider drop-policy checks execute. Broader service/modifier/virtualization coverage remains P03/P09/P11. |
| Timeline | All five sorts/directions; column and list layouts; grouping migration; multi-selection; read filters; search; status actions; next unread across scopes; scroll/selection retention and context menus. | In-scope Next Unread exists, but is not the native cross-sidebar traversal contract. P04/P06/P12/P13 remain. |
| Article display | Native templates/macros/themes, reader extraction, title/byline/date, attributed text, five text sizes, scripts disabled/enabled, trusted host input, links, media and scroll restoration. | Shared renderer and native raster processing implemented; extraction/Markdown and full media/display equivalence remain P19/P23. |
| Clipboard and sharing | Preferred article versus external URL, multiple articles, title/HTML/text, share subjects, selected content, native sharing and incoming share subscriptions. | Single-article contextual and main-menu multi-selection URL commands exist. Runtime/OS delivery and share-target integration remain P04/P27. |
| Menus and preferences | Every applicable native command and enablement rule; shortcut modifiers/focus; refresh options/defaults; theme/browser; unread count modes; account controls; toolbar visibility/customization. | Main menu and settings exist, but native commands/defaults must be checked individually; P06-P09/P13-P18. |
| Utility windows | Inspector, Current Activity, Activity Log, Error Log, Account Stats, stale-feed Dinosaurs, Help/About and application-support access. | Activity/errors/stats exist. Dinosaurs is a real stale-feed management feature, not a decorative window. P07/P15/P17. |
| Windows and lifecycle | Independent windows, correct global versus window-local preferences, close/Quit coordination, restoration, normal/maximized/full-screen geometry, focus, foreground/background browser opening and displays. | Earlier runtime coverage is not current-build verification. P20/P24/P31/P32. |
| Platform integration | Feed/theme/OPML activation, primary-instance routing, notifications, browser extension, incoming/outgoing sharing, automation and cloud sync. | Activation declarations/parsing exist; extensions, automation and CloudKit are not completed Windows equivalents. P26-P30. |
| Accessibility and language | Names/states/levels, focus order, keyboard-only operation, Narrator, high contrast, scale, RTL, culture-sensitive formats and native localized resources. | Selected peers and text measurements exist; full runtime and localization matrix remains P18/P24. |
| Distribution and support | Signed deployment, upgrades, update channels, opt-in crash reporting, privacy/support links and license attribution. | An unsigned inspected MSIX is not installed/update/crash-reporting parity. P31/P32. |

Native iOS-only screens, widgets and iOS tests are not silently counted as Mac
features. AppKit Services, speech/spelling, AppleScript, Safari extensions and
CloudKit cannot be declared complete merely because Windows uses a different
framework. Their applicable user outcomes need explicit Windows equivalents.
Developer-only destructive/debug commands need not appear in a release menu.

## Remaining discrepancies, easiest to hardest

**S** is a bounded helper/command change, **M** spans a component, **L** spans
subsystems or durable state, and **XL** needs platform/service integration.
Order includes dependencies; an execution gate can block a small item. **Open**
means missing or incomplete, **Audit** means equivalence is not established, and
**Verify** means an implementation exists but required evidence is missing.
Completed implementation will be recorded without erasing its runtime gate.

| ID | Effort | State | Work and acceptance criterion | Pinned source / Windows boundary |
|---|---|---|---|---|
| P01 | S | Done | Reproduced both never-created and externally removed cache-directory failures. Missing-entry removal is now idempotent; a locked-file error still propagates and preserves bytes. All 28 cache cases execute. | `RSCore\BinaryDiskCache.swift`; `Core\BinaryDiskCache.cs`, `BinaryDiskCacheTests`. This is Windows hardening, not an invented native test: native throwing deletion and Windows idempotent deletion are distinct APIs. |
| P02 | S-M | Verify | Implemented permanent `OnMyMac` identity, default-first ordering, UI/API removal protection and dependent picker ordering. Invalid default metadata fails rather than creating a replacement; failed manager construction closes adopted stores. Ten original Account regressions compile; execution is blocked. | `AccountManager.swift` initialization/sorting; `AccountsPreferencesViewController.swift` delete validation. Existing IDs/data are not renamed or discarded. |
| P03 | S | Verify | Implemented empty-space/smart-scope URL drops to the default local account, preserving explicit destinations and service restrictions. Drag feedback/drop share resolution; async reads revalidate the destination. Extended the existing drop probe; execution is blocked. Depends on P02. | `SidebarOutlineDataSource.swift::ancestorThatCanAcceptNonLocalFeed`; Windows `ExternalFeedDropDestination`. |
| P04 | S | Verify | Implemented distinct multi-selection Copy Article URL / Copy External URL menu commands, enablement, Ctrl+Shift+C / Ctrl+Alt+C and clipboard-error reporting. Ordinary text Copy and the existing Copy Link alias remain. New command/payload probe compiles without modifying the system clipboard; execution is blocked. | `MainWindowController.swift::copyArticleURL/copyExternalURL`; existing single-article contextual commands are retained. Windows clipboard-format/platform verification remains P27. |
| P05 | S-M | Verify/adapter | All ten native Web fixture cases now execute through the module-owned injected handler, plus six Windows ownership/concurrency/error regressions. The two Account-owned scope companions compile in their original assembly. Handler identity replaces task-local IDs and implicit shared tables; this representation difference is explicit. | `RSWeb\Tests\TestingURLProtocol*Tests`; `Account\Tests\IsolatedWebserviceResponsesTraitTests`; Windows `Web\WebServices\TestingHttpMessageHandler.cs`. |
| P06 | S-M | Partial/verify | Go/Today/All Unread/Starred, Ctrl+1/2/3, sidebar focus and current-scope search clearing are implemented. N/Space and the reader bridge share Next Unread; reader Go/URL-copy/Find routing requires trusted key events. The complete native command/focus matrix remains open. | Native `MainMenu.xib`, `GlobalKeyboardShortcuts.plist`; existing Windows J/K/M/L/R aliases are not silently remapped to destructive native commands. UI checks compile only. |
| P07 | S-M | Verify | Implemented application-support-folder opening and a Windows project-support action. Folder/link launch failures are reported. The Windows Help menu intentionally omits the upstream news subscription shortcut; source-port RSS/JSON duplicate-detection APIs remain. Actual OS delivery is unverified. | Native Help menu and `AppDelegate`; upstream subscription UI is an intentional Windows UX difference. |
| P08 | M | Partial/verify | Native refresh values/defaults and explicit Windows compatibility migration are implemented, as are remembered folder destinations and shared local-first account ordering. Twenty-four new Core migration cases execute. The native sidebar/timeline font getter always returns Medium, so unused stored keys do not justify adding size controls. Remaining hidden defaults and the display/preference runtime matrix remain open. | Native `RefreshInterval.swift` uses 1/3/4/5/6/7, not former Windows 0-6. `refreshIntervalVersion` prevents remapping twice. `Mac\AppDefaults.swift::fontSize(for:)` is deliberately hard-coded despite its storing setter. |
| P09 | M | Partial/verify | Count/dot/hidden modes now control sidebar indicators, accessible labels and numeric header/footer counts without changing unread data. Preferences persist and update existing windows, with failed-write rollback. Neutral resource strings and 28 original Shared regressions are added. Taskbar badge equivalence and runtime verification remain open. | `Shared\UnreadCountDisplay.swift`, `AppDefaults+Shared.swift`; eight-DIP dots follow `UnreadCountView`. This does not claim complete localization. |
| P10 | M | Verify | Exercise actual OPML menu/account choice, picker cancellation, read-only/cloud-provider paths, errors, refresh, all eligible accounts, export/re-import and restart. | Native `MainWindow\OPML`; Windows `OpmlCommands` plus existing original Account/Shared regressions. Requires approved runtime. |
| P11 | M | Partial/verify | Native pointer-driven root/folder placement and local manual ordering now execute with persistence, cross-window updates, undo/redo, and stable headings. Complete all service/modifier, multi-selection and filtered/virtualized combinations. | Five actual mouse drags and provider-policy regressions pass. Manual feed ordering is an intentional Windows display preference; native account/OPML ordering is unchanged. |
| P12 | M | Verify | Implemented timeline-first/sidebar-next traversal, first-selected-row semantics, group/expanded-folder skips, collapsed-folder and smart-scope wrapping, queued commands and shutdown cancellation. Navigation waits for pending read changes and the latest timeline query, and does not override a later user selection. | Native `ArticleArray.swift` and sidebar/main-window navigation. Seventeen Shared helper cases and a real-control traversal group compile only. Windows honors the requested wrap flag rather than copying the native controller's missing flag forwarding. |
| P13 | M | Verify | Implemented native WebView2 Find with case/whole-word options, Unicode, counts, next/previous wrapping, selection-based search, jump-to-selection, rerender restart and stale-request guards. Ctrl+F now means body Find; Ctrl+Alt+F retains database Article Search. | Uses stable SDK 1.0.3405.78's `CoreWebView2.Find`, not a JavaScript text-search approximation. An older runtime reports the missing capability explicitly. The new engine/control probe compiles but cannot execute. |
| P14 | M | Verified Windows headers | Per the requested Windows UX, feed commands remain in the sidebar in every layout; only timeline/article commands combine above the column labels. Title-bar Back/search/sidebar controls are fixed; hiding commands retains `...` and search. Add/remove/reorder/defaults/cancel, display modes, overflow, shared persistence and failed-write rollback remain. | The standalone native-style strip is intentionally not reproduced. Version-1 fixed/structural preferences survive migration/customization, but legacy spacers/separators are not drawn. Forty-nine toolbar cases and all six real header/column/customization/motion groups execute. Broader accessibility/platform combinations remain P24. |
| P15 | M-L | Verify | Implemented native latest-date queries, calendar-month/DST cutoff, active-account filtering, all sort keys and URL tiebreakers. The resizable Dinosaurs window supports blank/remembered thresholds, icons, sidebar/browser/copy/help actions, confirmed all-membership deletion and retry-safe undo/redo. Window shutdown drains actions before account shutdown. | Six database query cases execute. Twelve original Shared model/command cases and a new real-control group compile; actual UI execution remains blocked. Native startup-open/threshold keys are retained. |
| P16 | M | Audit | Finish account/inspector controls, service capability enablement, account active/name updates, feed metadata/notification settings and contextual-menu differences. | Native preferences/inspector/sidebar controllers; Windows dialogs/preferences. |
| P17 | M | Verify/audit | Current Activity is now separate from Activity Log, with half-second updates, two-second completion linger, idle timer shutdown, native labels/state accessibility and saved open state. Atomic snapshots prevent concurrent category/state mismatches and stale status icons. Existing Error Log/Account Stats still need the broader runtime matrix. | Three new ActivityLog cases execute; six Shared timing/text/lifetime cases and the Current Activity control group compile only. Windows corrects the reference-identity-only row-update issue rather than retaining a stale pending icon. |
| P18 | M-L | Partial/verify | Added native Speech Start/Stop outcomes using installed Windows voices, selected/visible reader text and one owner-scoped playback session. Replacement, navigation and shutdown cancel/drain resources; errors are visible. Spelling/editing, complete localization, culture/RTL, IME and clipboard matrix remain open. | The no-audio speech probe checks text/ownership/cancellation/error routing but has only compiled. Real synthesis/playback and applicable system editing services require approved platform verification. |
| P19 | M-L | Verify/Audit | Finish declared-image formats/codec fallbacks, mixed-DPI icon sizing, actual image binding, reader extraction, themes, links, embedded media and JavaScript-on/off behavior. | `RSImage`, `Images`, native detail/rendering; WIC tests do not establish every platform format or current WinUI pixels. |
| P20 | M-L | Verify | Run all independent-window/state/close/Quit/focus scenarios after current changes, including the new browser/image/drop paths and exact persisted snapshots. | Native `MainWindowState`; current probe cannot run under Application Control. |
| P21 | L | Verify/Audit | Finish account-level polling/backoff/queued-send, OAuth/transport ownership, reconnect, failed construction/removal and status/membership reconciliation verification for every provider. | Native Account delegates; original Windows Account cases are blocked, although client component cases run. |
| P22 | L | Partial/verify | Native cleanup is now an explicit DAL policy with PreserveHistory, FeedBased and SyncSystem modes; eleven new storage cases execute. It preserves unread/starred sync bodies, separates body/status lifetimes, batches large deletions, honors the native age-cleanup budget and rolls back failures. No startup caller or existing profile has been switched to destructive retention. Profile opt-in/activation and remaining schema migrations are still open. | Native startup cleanup uses 90-day read/unstarred sync bodies; orphan statuses use 180 days/read+unstarred for sync or 30 days/unstarred for feed-based accounts. A nonempty subscribed set also removes unsubscribed content/statuses, even starred. An explicit migration is required before enabling that behavior. |
| P23 | L | Audit | Complete method/algorithm/schema ownership comparison for all modules and shared code, not only tested entry points. Compare parser permissiveness, HTTP contracts, Markdown/extractor dependencies, caches, queues, threading and error propagation; remove only proven redundant implementations. | Native `Modules`/`Shared` against Windows counterparts. Platform adapters and intentional safety differences require individual documentation. |
| P24 | L | Verify | Complete real keyboard/focus/Narrator/accessibility-peer, high-contrast, scale/monitor/rotation, minimum-size and large-text matrix. Do not inject global input into another user's foreground window. | Native accessibility/display behavior; previous text measurement and selected peers are only part of the matrix. |
| P25 | L | Verify/Audit | Measure large OPML/profile/search/refresh/scroll workloads, memory pressure, cancellation and shutdown; compare native performance expectations without claiming functional case counts prove responsiveness. | Native performance cases and cache/queue design; benchmarks are outside the current 713-function inventory. |
| P26 | L-XL | Verify | Install an approved signed package and exercise feed/theme/OPML protocols/files, cold/warm/two-process routing, account-scoped notifications and foreground/background browser launches. Include malformed/stale requests and primary-instance failure. | Native URL/document/notification entry points; package/PE inspection and parser tests are not deployed behavior. |
| P27 | L-XL | Verify | Implemented incoming Uri/WebLink share-target registration, URL-only normalization, primary-instance processing, progress reporting and user-confirmed account/folder/name selection. Cancellation and failed subscription creation do not report completed subscriptions. Outgoing sharing remains available. | Sixteen Core cases execute; the new data/dialog/failure probe compiles. OS ShareOperation completion, cold/warm/two-process routing and existing outgoing-share delivery still need deployed verification. Desktop share activation requires Windows 10 version 2004 or later. |
| P28 | L-XL | Partial/verify | Added an Edge/Chrome Manifest V3 extension with activeTab/storage only, active-page URL handoff, dedicated/default-reader choice and visible errors/rollback. Thirty standalone Node cases execute. Source and bundled files are present; store publication, real browser installation and Windows handoff remain open. | Pinned Safari code forwards the current page URL to app-owned FeedFinder; it does not scan the DOM. Windows uses an explicit popup link for the external-protocol gesture and browser-local preferences rather than an Apple app group. No broad site permissions or page scripts. |
| P29 | XL | Verify/adapter | Implemented an opt-in local scripting service with native object properties, get/count/exists/make/delete/set/openLocation, container-scoped mutations and active-window selection. Article IDs are feed-supplied UniqueIDs. Windows pipe access is current-user-only with explicit remote rejection; a bounded PowerShell client is included. | Six native scripting counterparts and four added service cases compile in the original Shared suite; ten Core transport/opt-in cases execute. This is a documented JSON/pipe equivalent, not an AppleEvent implementation. Installed application, cross-user denial and UI behavior remain unverified. |
| P30 | XL | Open | Resolve iCloud/CloudKit synchronization and storage-statistics parity using an actually supported Windows integration. Another RSS provider is not equivalent cross-device iCloud sync; never present unsupported account types as local accounts. | `CloudKitSync`, CloudKit account delegate/stats/test; absent on Windows. Requires platform/service and product decisions. |
| P31 | XL | Open/Verify | Establish signed installation/upgrade/update-channel equivalents, update checks, crash collection with explicit privacy/consent and support resources. Preserve profiles across update/uninstall choices. | Native Sparkle/Advanced preferences/CrashReporter; current unsigned archive has not been deployed. |
| P32 | Gate | Blocked | Obtain approved execution of original Parser/Account/Shared assemblies and the application; run current suites with nonzero TRX counts, all UI groups, live authorized provider sessions and macOS comparison. Close P01-P31 only with their required evidence. | Application Control error `0x800711C7`; no policy changes, renamed/repackaged assemblies, alternate loaders or execution evasions. |

The hard platform decisions are not grounds to silently narrow the original
request. CloudKit, automation, browser-extension support, update distribution and
legacy-history retention remain explicit open requirements until an agreed
Windows contract or an explicitly accepted exclusion is recorded.

## Execution evidence and limits

The last broad allowed-project run executed **878 passing cases across twelve
projects**, checking TRX counters rather than trusting exit status. The parser's
434 cases previously passed. Later, an account-only run passed 289 cases and the
three new database statistics/vacuum cases also passed separately. The real WinUI
probe reached **nineteen passing groups** before the application itself was
blocked. This covers sidebar disclosure/filtering, resizable statistics, native
column layout, full screen, isolated reader interaction and actual runtime
shutdown. The last successful run was probe 51; its final one-window session
matched the captured snapshot after exit. Earlier two-window Quit runs also
matched. The new browser UI check in probe 52 could not start.

The latest allowed full backend runs executed **295 Core, 37 ActivityLog, 10 Database,
83 ArticlesDatabase, 7 SyncDatabase, 9 ErrorLog, 29 HTMLMetadata, 31 Images
and 47 RemoteAccounts cases**. Prior relevant Web and NewsBlur runs passed 99
and 9 cases respectively. A new targeted Web run executed **16 HTTP-fixture
cases**, including all ten original Web fixture functions; this is not a new
full Web-suite count. All execution counts were checked in TRX files. The new modules and
their tests follow the pinned source's ownership; no blocked test cases were
moved or re-executed through a replacement assembly. Account and Shared tests remain blocked;
the earlier two account metadata-notification cases and seven new resource-ownership
cases, an additional feed-settings failed-initialization case, and the updated
shared cache/theme integration cases compile but have not executed. Remote restart fixtures now supply fresh
delegates and isolated transports instead of reusing disposed clients.
Ten new default-account regressions and two HTTP-scope companions also compile
in the original Account assembly without execution. The current thirty-four-group
UI harness includes cached bitmap binding, actual icon pixels, drop-policy
validation, mixed-account transfers and menu URL-copy payloads. The drop group
now includes default-local external destinations. Its browser, image, drop and
clipboard additions are compiled, not passing UI results; clipboard capture does
not establish OS clipboard delivery.
This checklist pass specifically reran **245 Core, 31 Images and 47 RemoteAccounts
cases**, plus the **16 targeted Web cases**: **339 executions, all passed, none
skipped**. The other full-suite figures above retain their earlier run dates.
The subsequent navigation/preferences pass ran all **269 Core cases**, including
24 new refresh-migration cases. Its focused preference run executed 26 cases.
New Account news detection and Shared display/folder/interval/traversal regressions
compile in their original assemblies only. The additional Go/Help, unread-display,
folder-dialog, Next Unread and native Find UI groups have not run.
The later stale-feed query run executed **72 ArticlesDatabase cases**, including
six new query regressions; RemoteAccounts was rerun with **47 passes**. This
continuation therefore executed **388 cases across those three full suites**.
The original Account/Parser/Shared and current application remain unexecuted.
Another request for an approved runner/machine/signing route could not be answered
because the user was unavailable. Broad further UI layout changes are deferred
until the accumulated control/runtime changes can be exercised safely.
The subsequent explicit-retention run raised ArticlesDatabase to **83 passing
cases**, including eleven new policy/batching/rollback cases. Dinosaurs and
toolbar implementations also passed normal, Probe and original Shared-project
compilation. Their additional control groups remain unexecuted; this is not a
current thirty-group UI pass.
The subsequent Current Activity/share pass executed **285 Core** and
**37 ActivityLog** cases, plus **30 Node extension-component cases**. The new
Current Activity and shared-feed controls bring the compiled harness to
thirty-two normal-path groups. The Node popup tests use an isolated DOM/browser
API fixture; they are not installed-browser or application execution evidence.
The following automation pass executed all **295 Core cases**, including ten
transport/opt-in cases. Its native Windows pipe tests use isolated names and
exercise framing, malformed clients and cancellation/drain behavior. Six native
scripting counterparts plus four additional Shared service cases compile only.
The opt-in application/IPC check is the thirty-third unexecuted UI group.
The last complete thirteen-project run passed 1148 cases before
subsequent work. None of these numbers establishes full parity.

The application block was confirmed in the Windows `.NET Runtime` event log:
loading `WinNewsWire.dll` failed before `App` construction with Application
Control error `0x800711C7`. No alternate binary or loader was used. A request for
an approved execution path could not be answered while the user was unavailable.

Live Feedbin/Feedly/Reader API/NewsBlur accounts and a macOS/Xcode environment were
not available for cross-platform end-to-end verification. No real credentials
were copied or invented. Passing fake-HTTP contracts does not prove live-service
or complete UX parity.

## What the regression work established

The new parser cases first reproduced skipped OPML outlines. Account cases
reproduced moved/lost folder memberships, names overwritten by refresh, invalid
XML exports, stale OPML after renames, and missing restart restoration. The final
implementations cover these failure paths, including an intentionally locked OPML
file and recovery from its newer settings snapshot.

The previous full-text-search test manually inserted the index rows that
production never created. It now exercises normal article writes and searches,
with update, migration, Unicode/author, empty-scope, and result-limit coverage.
Feedbin's original JSON fixtures verify the 224-feed refresh and failure behavior;
additional fake-service tests cover import through article retrieval.
The local loopback regression also reproduced a valid uppercase-scheme URL being
downloaded but dropped because the callback looked up a different URI spelling.
The refresher now uses the same canonical URL key in both directions.

Later passes established stable migration of known legacy parser IDs, shared
theme/reader rendering, native attributed-text behavior and real WinUI text
measurement. Cross-account drags copy rather than delete subscriptions, matching
upstream. Reconnect preserves account/feed/article identities and keeps passwords
out of metadata and prefilled forms.

Refresh sessions now survive between local refreshes, retaining 4xx, 429 and
redirect state. Cancellation drains callbacks before storage closes; periodic
status work shares the account operation gate with refresh, mutations and
reauthorization. Account removal first moves its closed profile outside the active
accounts directory, so a failed file cleanup is reported without resurrecting a
partially deleted account on restart.

Feedbin, Feedly, Reader API and NewsBlur callers now close only owned HTTP clients.
Account shutdown drains delegate work before closing its stores. Feedly tracks
OAuth work even after a caller stops waiting, blocks token persistence after
disposal, and drains cleanup even when a cancellation callback throws.
Reauthorization closes the previous delegate after adopting the replacement.
Failed account creation releases factory-owned delegates; an observer exception
after registration cannot dispose an account that the manager already owns.
Cleanup errors are logged without replacing the original creation error.

Feedbin distinguishes unchanged snapshots from empty ones and commits validators
only after successful model/status writes. Folder renames invalidate its tagging
validator, and legacy URL-based feed IDs reacquire their mapping after restart.
Feedly retries a valid empty search using local feed discovery and retrieves the
initial article page before reporting success.

Independent main windows now preserve separate selection/search/undo state while
sharing stored article statuses. Sorting is also window-local; legacy grouping
migrates to feed-name sorting. Typed sidebar/timeline/detail snapshots restore
multiple selected scopes, per-scope read filters, folder expansion, a selected
article, reader mode and scroll without marking a restored unread article read.
Missing IDs are ignored, bounds are clamped to current displays, and minimized
coordinates do not overwrite normal placement. Search text is not persisted.

System close, menu close and Quit share an asynchronous coordinator. This matters
because `Window.Close()` destroys the WinUI window without raising
`AppWindow.Closing`. A failed session write leaves the window open and reports
the error. Closing one does not close shared databases; the last window and Quit
drain runtime work before exit. Quit saves the complete open-window set before
closing any window; last-window close retains its final snapshot.

The sidebar is an extended-selection outline ListView: TreeView's cascading
multi-selection does not match native independent rows. Folder/feed/account/smart
scopes are unioned and deduplicated, with per-source read filters and scoped
search. Mixed deletion undo restores folders before their feed memberships.
Account Stats includes inactive accounts, totals, numeric sorting and explicit
vacuum; counts distinguish feed-scoped articles from all status rows. Vacuum
preserves articles, search data, status and queued service changes.

The real WinUI probe exercises these paths. Memory cleanup uses desktop Win32 low/high notifications,
because the WinRT app-memory event is unavailable in the unpackaged runtime.

Account headings now expand independently, including remembering expansion of
folders hidden inside a collapsed account. Hide Read Feeds retains selected-feed
and parent-folder exceptions and always keeps smart feeds visible. Account name
and active-state changes notify the manager after the primary preference write,
so open sidebars update, reorder and reveal newly active accounts.

The native `useColumnLayout` setting applies to all windows, while sorting and
split heights remain window-local. Column layout reuses the existing panes and
browser, with fixed status columns, bold unread rows, title fallbacks, feed icons,
native date formatting, sortable/resizable/reorderable headers and saved widths
and order. Full screen preserves normal and maximized placement. Geometry is
restored before loading an article, preventing a late layout change from moving
its saved scroll position.

Reader shortcuts, editable controls, hover, Space and F11 now work with publisher
JavaScript disabled. Host listeners run in a named CDP isolated world with a
world-scoped binding, after enabling the Runtime domain. Context and navigation
checks reject stale messages; arbitrary page `WebMessage` payloads no longer
invoke reader actions. Browser operations drain before the view is closed.
Timeline refresh retains favicon paths, avoiding status-only document reloads,
and extraction completion matches article identity rather than a replaced row
object.

Windows activation now recognizes native `feed`, `feeds`, `x-netnewswire-feed`,
`netnewswire://theme/` and `nnw://showunread|showtoday|showstarred` routes, plus
Windows-specific aliases. File activation reuses the OPML account chooser,
progress/cancellation and theme installer. Secondary launches redirect before
opening shared databases. Notification links carry both account and article IDs;
their tags use a bounded stable hash rather than process-dependent `GetHashCode`.
The package declares its protocols, `.opml`/`.nnwtheme` file types and a matching
notification COM executable/CLSID. Generic XML/ZIP associations are not claimed.

`Modules\HTMLMetadata` now owns the native JSON record/table/database and metadata
retrieval; the unused Web stub was removed. `Modules\Images` owns image metadata
tables and feed-image discovery. Success entries last 149 hours; HTML 4xx and
network retries use eleven days and five hours, with a three-hour attempt gate.
Image/no-favicon retries use five days, with thirty-day HTML and thirty-three-day
image-failure cleanup. Discovery preserves redirect-relative URLs, candidate
fallback and declared-icon precedence. The app's vacuum and shutdown paths own
both databases. Windows-specific reliability corrections preserve successful
metadata after memory eviction and expire in-memory no-favicon verdicts.

The Windows Imaging Component adapter now implements the native image-scaling
decision: choose the first frame with the largest maximum dimension, keep a valid
small single-frame source unchanged, otherwise extract/resample to one PNG at
`ceil(48 * maximum screen scale)` without enlarging it. Pixel tests cover indexed
ICO colors, alpha, animated GIF frame extraction and all eight EXIF orientations.
The active image pipeline validates decoded pixels before publishing bytes or
paths, records malformed successful responses as persistent failures, and repairs
invalid/oversized disk-cache entries. The full WinUI display path and the broader
platform codec/display matrix remain unverified; this is not every RSImage API.
Unsupported declared image formats fail explicitly and follow existing fallback
paths; SVG discovery remains excluded as upstream, and a raster response at an
SVG URL no longer receives an SVG display path.

Both metadata stores run native startup `vacuumIfNeeded`, sharing
`RSDatabaseInfo(key, value)` and its Unix `lastVacuumDate`. They vacuum on first use
and after thirteen days; a failed vacuum does not advance that marker. This is
cache maintenance, not an implicit migration that deletes article history.

Active image caches now use the source's `Caches\Images` and `Caches\Favicons`
folders with extensionless, lowercase URL-MD5 filenames. `BinaryDiskCache` stores
the supplied key directly instead of hashing it again. Existing Windows
SHA1/extension files remain readable and are copied to the new cache on a hit,
without deleting the legacy copy or downloading the image again. Native entries
win over legacy ones, and an image URL used for both an icon and favicon retains
separate cache paths. Missing disk files are restored from cached bytes.
Writes remain atomic and report failures; cache keys must be safe Windows
filename components, using the same validation helper as theme extraction.

The serialized database adapter now requests native DELETE journaling and NORMAL
synchronization. SQLite, not manual sidecar deletion, recovers existing WAL data.
The returned mode is checked, with the necessary in-memory database exception.
The new `Modules\Database\Tests\Database.Tests` suite verifies recovery, locking,
retry, rollback, serialized asynchronous work and disposal, plus the native string
fixtures. CLR strings remain UTF-16; this is not a claim to reproduce Swift's
contiguous-UTF8 storage representation. Failed article/feed-settings/sync store
initialization now releases its owned queue.

Sidebar drop validation now rejects existing destination feed URLs, duplicate
folder names and service-specific unsupported copy operations before offering a
drop. A mixed-account gesture moves same-account feeds and copies cross-account
feeds unless the user explicitly holds the copy modifier; a Copy cursor alone no
longer forces every item to be copied. The earlier backlog's grouped-transfer-undo
requirement was incorrect: the pinned `SidebarOutlineDataSource` and
`Account.moveFeed` register no drag/drop undo. Existing Windows single-feed move
undo is retained as an extra, not expanded into an invented native requirement.

The default account is now the stable local `OnMyMac` profile rather than the
first directory encountered. It sorts ahead of other accounts even if renamed;
inactive defaults remain identifiable but are not offered as active destinations.
Removal is disabled in Preferences and rejected by the Account API. A damaged
default profile is not skipped in favor of a replacement or a remote account.
Construction failure releases accounts already adopted by the manager.
Existing additional local accounts remain removable.

External feed URL drops on empty sidebar space or smart scopes now target that
default account, while a feed row targets its parent folder/account. Explicit
remote destinations are not silently redirected to local storage. Activity,
ownership, folder membership and root restrictions are checked before accepting
the drop and again after reading its data.

Edit now exposes separate article/external URL-copy commands for multiple
selected articles. They retain timeline order, skip missing values and use the
external field even when it equals the preferred link, as the native main menu
does. Ctrl+Shift+C / Ctrl+Alt+C leave ordinary text Copy unchanged. The existing
single-article contextual commands and Article > Copy Link remain. Clipboard
failures set an explicit status message and are logged.

`TestingHttpMessageHandler` ports the native canned-response matching and
request-history rules without installing a process-wide network interceptor.
Each test injects its own handler; sharing requires passing the same handler
explicitly. Specific methods precede wildcard methods, resets clear both maps
and history, and unstubbed calls deliberately receive the native fixture's empty
HTTP 200 while still being counted. This is a test-fixture contract, never a
fallback in production networking. Ten original Web cases and six Windows
regressions execute; Account-owned equivalents remain in their blocked assembly.

## Compatibility rules

- Keep existing account directories, account IDs, article IDs, credentials, and
  read/starred state readable.
- A manual OPML import is additive. It must not unsubscribe existing feeds or
  move a feed out of another folder.
- Parsing preserves nested outlines; account normalization follows NetNewsWire's
  single folder level.
- User-chosen imported feed titles survive refresh. Restoring the application's
  own OPML is not a manual rename.
- Import clears the article-fetch start date before refreshing. Unsupported
  downloaded content is logged against its feed rather than aborting the batch.
  Complete new OPML settings use one transaction to avoid synchronous per-property
  disk commits on the UI thread; existing settings and membership rules remain.
- OPML export/re-import does not carry read/starred article state; it is a
  subscription format, as in NetNewsWire.
- Failed writes must be reported. A newer durable settings snapshot takes
  precedence over an older OPML file left behind by a failed replacement.
- Preserve existing integer Unix-date rows while allowing fractional seconds
  in new article/status writes; SQLite's existing column affinity accepts both.
- Keep platform substitutes explicit: .NET encoding/SQLite/HTTP APIs and Markdig
  are adapters for Foundation/FMDB/URLSession/Tidemark. OPML deliberately validates
  complete XML before using the source-ported SAX parser.
- Preserve source-defined HTTP behavior where possible. One-shot and batch
  downloads have an explicit Windows 64 MiB safety bound; they do not share the
  short-lived one-shot response cache. Native OpenRSS throttling is one hour and
  persisted; Windows intentionally does not consume its allowance on unrelated
  batches containing no OpenRSS URL.
- Known older Windows parser ID formulas are supported explicitly, including
  restart/collision checks. This is not heuristic compatibility with every
  historical malformed input or parser discrepancy.
- Status edits queue only changed IDs. A failed queue write rolls back the local
  boolean change so the same action can retry. The article and sync databases
  remain separate, as upstream; crash-atomicity across them is not claimed.
- A downloaded-article notification refreshes only affected window scopes, plus
  smart feeds/global search, matching the native timeline's interest check.
- As in native `ArticlesTable`, reuse cached article instances instead of
  decoding body/relationship rows repeatedly. Invalidate only after committed
  writes and on deletion/remapping; preserve shared status identity when
  releasing article bodies under memory pressure.
- Coalesce downloaded-article UI updates and merge unchanged rows without
  resetting the timeline/sidebar controls. Actual subscription/metadata changes
  and unread-filter exception resets still update the visible tree.
- Measure embedded-browser responsiveness with native input and actual Chromium
  presentation events, including first scrolls and concurrent feed refresh.
  Callback timing alone is insufficient. The ANGLE shader-compilation stall and
  experimental, default-off compatibility-rendering preference are documented
  in `PORT_STATUS.md`. This Windows-specific workaround does not change backend
  parity or system graphics settings; its rendering choice takes effect after
  restart and preserves the existing browser profile.
- Distinguish mouse-wheel injection from physical precision-touchpad input.
  Check direction-change/settling behavior as well as frame presentation. The
  separate experimental direct-scrolling option removes measured browser wheel
  animation delay, but is not a claim of verified hardware touchpad parity.
- Do not claim a whole suite is ported when only selected cases are covered.
