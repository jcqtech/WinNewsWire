# NetNewsWire parity and regression review

Review date: 2026-11-14; theme-parity follow-up: 2026-11-15.

Reference: NetNewsWire `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a`.
Windows comparison: pre-sync `106603d128a34c17907ba10625e754b88cb8ff0a`
through the current upstream ports and the audit corrections below.

## Bottom line

**The core reader is substantially implemented, but full product parity has not
been achieved.** Local feed reading, subscription management, OPML, article
storage/search, read/star state, modern media rendering, timeline preferences,
and the main desktop tools have real production paths and substantial executed
coverage. This is considerably more than a UI remake or a collection of stubs.

There are nevertheless material gaps: turnkey
configuration/verification for some remote providers, author-avatar display,
some keyboard navigation, and activation of the native
database-retention policy. Platform delivery and complete UX/accessibility
equivalence also remain unverified.

**iCloud/CloudKit is now an explicitly accepted Windows exclusion**, following
the user's clarification on 2026-11-14. It is not a required parity backlog item.
The account-test failure and one startup UI block are fixed. **The recurring
long graphics-stack pauses are not fully fixed**; the native investigation and
the limits of the tested mitigations are recorded below.

The 505-entry upstream ledger is complete as a **commit-processing record**.
It does not mean every pre-existing NetNewsWire feature exists in Windows.
Neither a commit count nor an xUnit case count is a defensible parity percentage.
The next update can start from the recorded source tip, but it is not yet a
low-risk mechanical translation exercise. Source commit `8060219b...` was
previously skipped because Windows lacked the affected themes; the theme
follow-up now ports those bundles and closes that obligation. Other carry-forward
parity gaps must remain visible beside the source checkpoint.

## Scope and evidence

The review used the pinned upstream source, the Windows commit history, the
existing parity/test documents, the completed upstream ledger, and actual
Windows builds/tests/probes. High-risk integration review covered image
selection/caching, timeline layout, preference propagation, sidebar deletion,
reader identity/lifecycle, refresh availability, and network policy.

The earlier commits explain some remaining divergence. `37f784e` expanded the
initial reader's utilities, OPML and rendering; `106603d` consolidated the
broader parity/responsiveness work. Several gaps below were already present at
that baseline and were not introduced by this synchronization. Older
`PORT_SPEC.md` exclusions are historical scoping decisions, not evidence that
the user accepted those features as permanently out of scope.

This is a source-backed, risk-focused comparison, not a line-by-line proof of
every algorithm or a paired macOS/Windows usability study. A macOS runtime and
authorized live-provider accounts were not available. No real credentials or
live reading profiles were used or modified.

## Functional assessment

| Area | Assessment | Important boundary |
|---|---|---|
| Feed parsing and discovery | Strong implemented coverage | RSS/Atom/JSON Feed, Media RSS, encodings, identifiers and discovery are exercised; exact equivalence for every possible feed is not claimed. |
| OPML and subscriptions | Substantially implemented | Import/export, folder membership, names, mutations, undo and persistence have production paths. Current installed picker/cloud-provider behavior is a separate verification requirement. |
| Articles and state | Substantially implemented | Storage, search, canonical status identity, read/star actions and window-local selection are covered. Retention activation is missing. |
| Reader and media | Substantially implemented | Templates, primary media, enclosures, scripts-on/off handling, themes and scroll preservation have focused coverage. Extraction and Markdown use different engines. |
| Timeline and sidebar | Broad coverage, with identified gaps | New appearance controls work. Author-avatar selection and some keyboard routing are incomplete. All canonical article themes are now available. |
| Remote synchronization | Implementations, not full service certification | Feedbin, Feedly, NewsBlur and Reader API variants exist; mock HTTP tests are not live-provider proof. |
| Desktop tools | Implemented | Find, activity/error logs, statistics, stale-feed management, speech routing and multiple windows exist. Some OS-level outcomes still need interactive verification. |
| Sharing, extension and scripting | Windows adapters exist | Share-target declarations, an Edge/Chrome extension and an opt-in named-pipe scripting API are not merely placeholders. Installed handoff and release delivery are not certified here. |
| iCloud/CloudKit | Accepted Windows exclusion | No existing-account synchronization bridge is claimed. Unsupported profiles remain protected. |
| Localization/accessibility/release | Partial or unverified | Neutral English resources and selected accessibility behavior exist, not a complete language/Narrator/IME/RTL or installed-upgrade matrix. |

## Confirmed gaps and recommended order

| ID | Priority / effort | Gap and evidence | Recommended work |
|---|---|---|---|
| PR-01 | Accepted exclusion | **No iCloud account support.** `Modules\Account\Sources\Account\AccountDelegateFactory.cs:15-16` rejects CloudKit; `Windows\Dialogs\AccountSheets\AccountTypePickerDialog.cs:30-41` does not offer it. | The user explicitly does not expect iCloud provider setup on Windows. Do not offer it or reinterpret an existing CloudKit profile as local. Another RSS provider is an alternative, not access to NetNewsWire's existing iCloud account. |
| PR-02 | High / M-L | **Some offered accounts are not proven turnkey in a distributed app.** `FeedlyClientConfig.cs:24-46` reads external OAuth configuration; `AccountSetupSheets.cs:187-203` stops setup when it is absent. `ReaderAPIAccountDelegate.cs:30-31` reads Inoreader application identifiers from environment. | Establish a release configuration/client-registration contract and exercise sign-in, refresh, reconnect and state synchronization against authorized live accounts. Do not expose secrets in source or logs. |
| PR-03 | High / M-L | **Production does not activate startup retention.** `ArticlesDatabaseStore.Retention.cs:12` has an explicit policy API, but its callers are tests only. Upstream `Account.swift:348` calls startup cleanup; `ArticlesDatabase.swift:423-434` explains the database-growth/performance requirement. | Design a safe migration/opt-in policy, then wire it into startup with large-profile checks. Do not silently delete existing Windows history to close the gap. |
| PR-04 | Medium / M | **Author avatars are not actually selected/downloaded for display.** `Windows\ViewModels\MainViewModel.cs:1373` populates icons through `PreferredIconAsync(feed)`; it does not consult authors. Upstream `Shared/IconImageCache.swift::imageForArticle` prefers a single author's avatar, using `Modules/Images/.../AuthorAvatarDownloader.swift`. | Put author-versus-feed image selection in a shared policy/service, including fallback, refresh, grayscale and lifetime ownership. Automatic icon visibility currently recognizes avatar metadata without completing the avatar display path. |
| PR-05 | Completed 2026-11-15 | **All eleven canonical theme choices are implemented.** `a7dd3a2` adds the eight missing bundles, both template generations and font notices, and closes live-control, processed-CSS, local-font/resource and import-confirmation gaps. | Native tests cover all themes, five text sizes, dark appearance, media slots, actual embedded/local fonts and multi-window updates. A fresh unsigned x64 MSIX contains the complete collection. See the theme follow-up below; the installed pre-sync app was not replaced. |
| PR-06 | Medium / M | **Copied keyboard definitions are not runtime parity.** `Windows\Assets\Keyboard\TimelineKeyboardShortcuts.plist` declares left/right navigation, but `KeyboardShortcutsLoader.LoadAll` has no runtime consumer and those actions have no Windows handlers. Upstream `Mac/MainWindow/MainWindowController.swift:555-565` implements the focus transfers. | Introduce one command/routing registry with scope-aware tests, preserving explicitly chosen Windows aliases. Verify actual focused controls, not only shortcut assets or the help catalog. |
| PR-07 | Medium / M-L | **Dependency substitutions need compatibility coverage.** Windows uses SmartReader and Markdig (`Shared\ArticleExtractor\ArticleExtractor.cs`, `Shared\AppShared.csproj`, `Modules\Markdown\Markdown.csproj`); upstream uses its extraction service and Tidemark. | Maintain explicit adapter contracts and a shared corpus. Different engines are not automatically wrong, but they are not a 1:1 implementation or proof of identical results. |
| PR-08 | Medium / L | **Language and platform-verification gaps remain.** Windows has only neutral `Strings.resx` plus hard-coded captions; upstream catalogs include Spanish entries. Full Narrator, high contrast, IME, RTL, mixed-DPI, installed sharing and browser-extension verification was not performed. | Build a reproducible platform/language matrix. Keep unverified outcomes distinct from missing implementations. |

The extension is source-installable rather than published
(`Windows\BrowserExtension\README.md`). Windows automation is an explicit
current-user pipe/JSON equivalent, not AppleEvents
(`Windows\Automation\README.md`). These should be completed/verified as adapters,
not listed as wholly missing features.

Microsoft Store distribution was explicitly requested in the earlier work.
Store-managed updates can therefore be the Windows update mechanism; copying
Sparkle is not required. Signed installation, upgrade/profile preservation and
Store delivery were not certified in this audit. The application records local
errors, but it does not reproduce the Mac consented crash-report submission
workflow.

## What can and cannot be mitigated

There are three different categories: missing portable behavior, service or
release work requiring external access, and literal Apple-only APIs. They must
not all be labelled "impossible on Windows."

| Gap | Missing outcome and impact | Feasible mitigation and acceptance boundary |
|---|---|---|
| PR-01: iCloud | Windows cannot currently read or synchronize an existing NetNewsWire iCloud account, including its reading state. | **Accepted exclusion, not a direct API port.** A bridge would require a separately authorized service/container integration, not substituting another provider's name. A common supported RSS provider can supply cross-device synchronization; OPML can transfer subscriptions, but does not transfer iCloud read/star history. |
| PR-02: provider delivery | Feedly sign-in needs `FEEDLY_CLIENT_ID` and `FEEDLY_CLIENT_SECRET`, or `feedly-client.json`. Inoreader also expects application identifiers. An offered setup screen is not evidence that a fresh Store install can connect. | **Mitigable, with external prerequisites.** Register and maintain a Windows client/redirect contract with each affected provider, use its approved native-client authentication design, deliver configuration safely, and distinguish missing configuration from invalid configuration. Verify sign-in, token refresh/revocation, reconnect, subscription/folder mutations, and read/star reconciliation with authorized live accounts. Unit HTTP fixtures cannot certify this; upstream credentials must not be copied or invented. |
| PR-03: retention | The cleanup API has no production startup caller, so old bodies/orphaned state can continue accumulating. This is a storage-growth risk, not proof that it caused the measured graphics stalls. | **Mitigable, but not by blindly enabling deletion.** Map each provider to PreserveHistory, FeedBased or SyncSystem; introduce an explicit migration/consent policy, preview deletion counts, and exercise cancellation, rollback, large profiles and restart. The existing sync policy preserves unread/starred bodies when aging content out, but the unsubscribed-feed branch can remove even starred content. That distinction must be disclosed and approved before activation. No existing history was deleted in this follow-up. |
| PR-04: avatars | Automatic timeline icon visibility recognizes author-avatar metadata, but the display path still loads a feed icon. Single-author articles therefore do not show the avatar preferred upstream. | **Directly mitigable in code.** Add a shared author-versus-feed selection/downloading service; single-author avatar, valid feed icon and fallback must have one precedence policy. Reuse the bounded image cache, decoding, content-version invalidation and cancellation/lifetime mechanisms. Verify actual sidebar/timeline/reader results, changed avatar content, unavailable images and grayscale; merely parsing an avatar URL is not completion. |
| PR-05: themes | **Resolved in source and verified package output.** Default plus all ten canonical file-based themes are available. | Imported the missing bundles and licensed embedded fonts, retained legacy/7.3 templates, completed live settings/resource behavior and confirmed installation workflows. Source `8060219b...` is now Ported; `6ba1b38e...` also records the complete template collection. A signed updated release is still needed to replace the user's older installed application. |
| PR-06: keyboard routing | Copied shortcut plists are not consumed at runtime; some timeline left/right focus transfers are missing. Help text and actual handlers can diverge. | **Directly mitigable in code.** Use one scope-aware command registry for routing, menus and help. Preserve accepted Windows aliases and normal editing keys. Verify actual focus transfers among sidebar, timeline and reader, including text inputs and WebView2, using an owned foreground window. Modifier adaptation is legitimate; missing routing is not. |
| PR-07: extraction/Markdown | SmartReader and Markdig replace upstream extraction/Tidemark implementations. Exact output and edge-case equivalence have not been established. | **Mitigable compatibility risk, not a proven failure for every article.** Define adapter contracts and a pinned corpus covering malformed HTML, entities, embedded media, code/lists/tables, Unicode and link resolution. Compare normalized output and rendered behavior; port or adapt mismatches that matter. Different libraries can match user outcomes, but universal byte-for-byte equivalence cannot be promised from their names or current tests. |
| PR-08: language/accessibility | The Windows application lacks upstream language breadth, and a complete Narrator, keyboard-only, high-contrast, IME, RTL and mixed-DPI matrix is unverified. | **Mitigable on Windows.** Move remaining captions into the existing localization mechanism, translate/review resources, and exercise cultures and layouts. Complete UI Automation names/roles/states, focus/announcement behavior and the real platform matrix. VoiceOver/AppKit code is not portable, but equivalent accessibility outcomes are. Unverified cases are not automatically missing implementations. |

Platform-specific delivery has its own acceptance work:

| Native facility | Windows outcome | Boundary |
|---|---|---|
| Safari extension / Apple app groups | The existing Edge/Chrome MV3 extension hands URLs to WinNewsWire. | Safari's extension binary cannot run on Windows. Publishing the Windows extension and testing installed browser/protocol handoff are feasible, but require publisher/release access and real browsers. |
| AppleEvents / AppleScript | An opt-in current-user named-pipe API and PowerShell client implement scripting outcomes. | Existing AppleScript programs are not transparently executable against Windows. Document command/object mappings and complete installed-process/access-denial checks rather than calling automation wholly absent. |
| macOS share/services APIs | Windows share-target, outgoing sharing, protocol and file registrations exist. | Use Windows contracts and confirm cold/warm/two-process delivery in an installed signed package. Declaration inspection alone is insufficient. |
| Sparkle updates | Microsoft Store-managed updates fit the explicitly requested distribution model. | A separate Sparkle clone is unnecessary. Signed installation, upgrade/rollback behavior and preservation of profiles still need release verification. |
| Mac crash submission | Local error reporting exists. | A consented Windows crash-collection/submission workflow is feasible, but needs a privacy policy, redaction/retention design and an authorized endpoint. It has not been supplied or silently enabled. |
| iOS/iPad-only UI and frameworks | Not required for the Windows desktop reader. | Do not invent Windows obligations for mobile-only screens. Shared reading/subscription behavior in an Apple source file remains relevant. |

In short, **the portable gaps can be closed**. Provider certification, release
delivery and full platform testing need external credentials/permissions or an
interactive environment. Literal Apple APIs are replaced, not copied; iCloud is
the explicit accepted exclusion. Neither unavailable credentials nor different
frameworks justify claiming the remaining portable behavior is complete.

## Theme parity follow-up (2026-11-15)

Commit `a7dd3a2` completes the canonical theme collection at the pinned upstream
revision: **Default, Appanoose, Biblioteca, Gray, Hyperlegible, NewsFax, Plum,
Promenade, Sepia, Tiqoe Dark and Verdana Revival**. The eight newly imported
bundles preserve source content with whitespace-only normalization. Both
`template.html` and `template7.3.html` are retained, and embedded font bytes are
unchanged. Hyperlegible includes the Atkinson Hyperlegible OFL notice; NewsFax
retains the ModeSeven distribution notice. Windows system-font fallbacks replace
Apple-only font aliases without replacing the themes' embedded fonts.

Theme names are unique and sorted, with Default separated in native menus and
bundled names taking precedence. Open Preferences, menu checkmarks and readers
stay synchronized across windows. CSS and metadata edits refresh the selected
theme; deleting it restores and persists Default. Reader helpers handle
Biblioteca's different body markup and header icon. Theme-specific size scales
and intentional designs, including NewsFax's lack of a header image and the
fixed palettes of Sepia/Tiqoe Dark, are preserved.

File-backed themes now use their **processed** stylesheet, including text-size
macros, Windows font fallbacks and grayscale metadata. Theme-specific HTTPS
origins prevent two themes with identical CSS from sharing the wrong local
assets. Local resources are bounded, read off UI, confined to the selected
theme folder and served with the font CORS headers they need. Virtual-host folder
mapping is not used for this path because it bypasses `WebResourceRequested`
and would serve the unprocessed stylesheet.

File/URL imports stage and inspect an immutable copy before showing creator,
version and website information. Replacement requires a second confirmation.
Cancellation, invalid replacements and closing the confirmation window preserve
installed files; staging and active work drain before shutdown. Existing Windows
behavior of selecting an accepted import is retained. Built-in bundles cannot be
overwritten through the UI; a customized bundle using a built-in name must be
renamed. HTML/CSS-only themes and XML/binary plist metadata remain supported,
with JSON metadata retained as a Windows authoring option.

The full Shared suite passes **541 cases**. Native checks exercise all eleven
themes through real menus, every text size, dark appearance, media slots,
embedded/local fonts, separate asset origins, live edits, multiple windows,
import confirmation and close-time cancellation. Existing live-grayscale and
all three reader-resize/anchor controls pass. A stale resize-observer test
condition was corrected to wait for the current viewport; production anchoring
and its offset tolerance were not changed.

Genuine controls fail when the actual prior implementation is restored:
unprocessed CSS loses the font-size macro, old control wiring leaves selection
stale, the old importer installs before confirmation, and old body selectors
miss Biblioteca tables. A fresh build without the new bundles fails exactly
eight bundled-theme cases while Default/Gray/Plum pass.

A fresh **unsigned x64 Release MSIX** was built and validated/unpacked with the
Windows SDK. Its 42 theme/license files match source, all ten file-backed theme
directory names are correct, Default's embedded resources exist, and both
production assemblies identify `a7dd3a2` with no diagnostic probe. The existing
UAP `plist-cil` compatibility warning remains; it did not prevent packaging.
No installed application, real reading profile, signing certificate or package
version was changed. At the November 14 inspection, the installed 1.2.0.0
application was the pre-sync `106603d` build; that observation is not a claim
about the version currently running.

Evidence is retained in `theme-parity-complete-tests`,
`theme-parity-complete-native`, `theme-verified-theme-import`,
`theme-verified-reader-resize-*`, `theme-negative-*`, and
`theme-parity-package`. This closes PR-05, not the unrelated graphics-pause or
live-provider/release-certification gaps.

## Intentional UX versus accidental divergence

Earlier user requests were recovered from the original comparison session,
not inferred solely from current code. Confirmed choices include:

- Pane-based command headers, title-bar search/menu controls, and feed commands
  staying in the sidebar when column layout is enabled.
- Thin gray separators rather than banded column rows, extra bottom padding,
  flat column-header behavior, and a left-aligned narrow table.
- Static account headings aligned with Smart Feeds, inset folder disclosure,
  folder/smart-feed glyphs and the original taskbar icon.
- Responsive menu overflow. Sidebar/browser wheel inertia defaults off, while
  the later November 16 article-list request explicitly restores unmodified
  native WinUI scrolling for wheel, touchpad and touchscreen input.
- One pane-aligned footer, retaining sidebar count/refresh cycling, original
  timeline totals and article/webpage information, and the far-right theme picker.
  The gear uses its original subscription commands but lives at the right end
  of the main menu bar, independently of footer visibility.
- Column-view footer counts and article/webpage information are adjacent and
  left-aligned, separated by a short vertical rule. Long text still truncates
  within its allocation instead of pushing out the theme picker.
- The combined article-list/reader pane has no outer margins and only its
  top-left corner rounded. Its sidebar gutter remains ten DIP; the scrollbar
  extends four DIP into it, with the eight-DIP resize target overlapping the
  pane edge by two DIP rather than covering the scrollbar.
- The article-list scrollbar starts below visible toolbar and column headings.
  Rows still scroll under the acrylic headers, using unmodified native input.
  In side-by-side layout, both panes meet the single divider without blank
  gutters; its eight-DIP resize target overlays the reader side.
- With headers hidden, the floating options button sits four DIP farther right
  and fades/slides with mouse proximity. Keyboard focus, touch/pen input and an
  open options menu keep it available; reduced-motion settings are respected.
- The pane/footer boundary is flat, with no pane bottom border and one
  footer-owned separator. The original subtle left shadow lives in a separate
  non-interactive layer clipped to the pane's left edge and the content row.
  The translucent container itself stays unraised so the shadow cannot shade
  its bottom fill or the footer. Bottom fills stay square in all layouts.

The November 16 footer requests explicitly require "There should only be one
footer" and then "move the settings button from the bottom left hand corner
to the top right in-line with the menu bar." The subsequent pane request retains
only top-left rounding, removes the window-edge margins and slightly reduces the
sidebar gap. These choices supersede the ported bottom-left gear and duplicate
footer rows; future upstream ports must preserve them. Column layout still puts
timeline totals and article information together at the bottom, while hidden
panes do not retain empty footer sections. The top-right gear has a permanent
XAML owner; the menu-overflow host excludes its reserved column.

The column request was explicit: "Get rid of the banded rows - insetad, put a
thin grey line between each row to separate them." The later instruction made
account headings static, superseding the earlier request to keep their
disclosure button. The existing pane/browser design is also documented in
`PARITY.md`.

The post-sync review found that the literal Mac separator rule had incorrectly
removed the intentional Windows column dividers. That regression is repaired.
Standard-layout separator preferences and the permanent column dividers are
now tested separately.

Not every difference has equivalent approval. In particular, the precise
AppKit status-bar gradient/grip palette was excluded in source commit
`c943a1e6...`; Windows retained Fluent styling. That is an identified visual
adaptation, not a recovered explicit user decision. Likewise, omitting the
eight canonical themes was a baseline parity gap, not a platform requirement;
the theme follow-up above has now closed it.

## The four introduced regressions: cause and prevention

These were porting/integration mistakes, not necessary Windows compromises.
The checks originally used did not cover the relevant user decision,
multi-consumer identity, retained production data, or invalidation frequency.

| Regression and cause | Fix and evidence | Specific prevention |
|---|---|---|
| **Column dividers disappeared.** `40e4cfd` applied the Mac standard-layout-only separator rule to Windows columns, overriding the user's explicit request for thin gray dividers. The upstream rule was treated as authority without consulting the Windows exception. | `e6c59b6` restores permanent column borders while retaining standard-row preference/selection rules. Removing the real borders fails the native control. | Maintain an explicit UX exception register with the original request and a native regression. Review layout-specific scope before translating a platform rule; do not infer approval from existing code or an AppKit filename. |
| **Grayscale work was duplicated across views.** Per-view revision counters such as 5 and 1 were mistaken for global file-content versions, so consumers of the same image repeatedly regenerated/displaced variants. One-view tests missed the churn. | `c680e67` shares the source by normalized file identity, checks disk versions off UI, and refreshes bytes/luminance only for actual changes. Native pixels, cross-view sharing and reader response/state controls pass. | Separate notification generation from content identity. Exercise two views with different histories, unchanged re-notifications, genuine file replacement and shutdown; assert decode/conversion counts as well as visible results. |
| **Titles were measured twice and retained.** The line-budget helper independently measured text already measured by the native panel and cached every visited title. Correct line wrapping did not reveal growing retention/allocation. | `b84b507` reuses measured title height and retains only font/line calibration. The 500-title control changes from 500 retained entries to zero; allocations change from 2,800,080 to 1,883,904 bytes and elapsed time from 158.1 to 90.6 ms in that microbenchmark. | Test the production panel, not only the helper. Bound retained entries and allocations over many distinct/recycled rows; verify line/full-title behavior in the same path. These numbers are not an FPS claim. |
| **Status changes relaid out command headers.** A broad status-update callback queued toolbar work for hover links/time/status even when command availability was unchanged. Functional enablement tests did not count needless work. | `8d7fab5` invalidates command headers only when refresh availability changes. Ten hover-link changes cause zero extra layouts; 16 selections return from 32 header layouts to the pre-sync 16, and refresh-session disable/enable still works. | Scope invalidation to the state consumed by the control. Assert zero work for unrelated changes and exactly necessary work for real transitions, including active refresh and multiple selections. |

The original/reverted production paths fail the new controls, and restored paths
pass. Related line-budget/full-title/thumbnail, selection, grayscale, reader
identity and shutdown checks were rerun. The older icon-contrast probe was also
corrected to expect the intended translucent system fill rather than the
superseded opaque RGB constants. Its file-invalidation/no-readback checks pass.

## Test and performance results

The complete backend solution now executes **2,462 cases: all passed, none
skipped**, across 16 assemblies. The Account suite passes all **363** cases.

### The pre-existing failure, now fixed

`AccountManagerTests.UnsupportedAccountFilesAreKept` put a CloudKit profile in
the reserved `OnMyMac` directory and expected it to be ignored/replaced with a
local account. Production intentionally throws instead: the default On My PC
account must be local, and its existing profile must not be overwritten. The
fixture conflated an unsupported ordinary profile with a corrupt reserved
default profile. It was a contradictory test expectation, not justification to
weaken data protection.

Commit `13f29f3` separates the two contracts. Unsupported CloudKit and unknown
type `999` profiles use a non-reserved ID; tests check unchanged metadata and
payload bytes, omission from loaded accounts, and creation of the canonical
local default. Reserved-path tests cover both Feedbin and CloudKit and still
require an exception, unchanged metadata and no replacement feed database.
No real profile was renamed, deleted or reinterpreted.

Both normal and Probe x64 builds pass. Focused native controls cover the audit
fixes and the recent layout/image/network behavior. These results do not
substitute for installed application or live-provider tests.

A pre-sync `106603d` worktree was rebuilt with unchanged production code.
Only a diagnostic selector and identical GC-timing instrumentation were added.
It and current Windows code ran the same isolated 2,000-article workload:

| Metric | Pre-sync | Current after corrections |
|---|---:|---:|
| Maximum realized rows | 38 | 36 |
| Selection median in paired instrumented runs | 182.75 ms | 183.68 ms |
| Header layouts for 16 selections | 16 | 16 |
| Header collection rebuilds during selections | 0 | 0 |
| Icon screen readbacks while scrolling | 0 | 0 |
| Regular scrolling allocations in the paired run | 1.62 MiB | 3.29 MiB |
| Column scrolling allocations in the paired run | 1.75 MiB | 2.33 MiB |

**This is not a clean bill of health for latency.** Both baseline and current
code exhibited intermittent roughly 400-500 ms dispatcher gaps during article
selection. The final current run also recorded 314-440 ms scrolling gaps and
a 524 ms selection gap. GC-pause counters do not explain all of that wall-clock
delay. Median selection and structural virtualization/header behavior remain
similar, but richer rows allocate more, and the long tail needs further
foreground/presentation tracing under a representative populated profile.

The earlier reader/performance gate initially had four failing groups:
one obsolete palette assertion, now corrected, and three native
foreground/screen-pixel checks. A direct query returned no foreground window,
so the ownership guard correctly refused keyboard/scrollbar injection.
The full native-input/screen-presentation gate is therefore **not certified**
in this environment. No ownership guard or security setting was bypassed.
The timing report explicitly records `ForegroundOwned`, `PhysicalInputMeasured`,
GC pauses, and long UI gaps so a structural pass is not mistaken for verified
physical scrolling responsiveness.

Evidence is retained in the session's `files` directory: `post-sync-backend-audit`,
`post-sync-reader-performance-original`, `pre-sync-performance-isolated`,
`audit-performance-gc-baseline`, `audit-performance-gc-current`,
`audit-performance-gc-comparison.json`, `post-audit-performance-final`, and the
source-prefixed negative/restored probe folders. Temporary baseline production
copies are not part of the repository.

### Follow-up pause investigation: one fix, one unresolved native boundary

The follow-up used per-process EventPipe and passive native sampling of isolated
probe processes. It did not capture unrelated applications, modify graphics
drivers, bypass foreground ownership, or change global graphics settings.

**Startup block fixed in `4976093`.** An EventPipe trace showed the UI waiting about
1,049 ms in `NetworkMonitor.IsAvailable` while a worker initialized that type
by enumerating network adapters. A cheap-looking property still waits for a
type initializer on another thread. `ArticleThumbnailService` now uses its
existing Windows connection-profile/cost/status API directly and no longer
touches/subscribes to the redundant portable monitor on UI.

The new `--network-startup` control holds the adapter-query dependency for
500 ms. With the exact production fix reversed, UI construction takes
606.0 ms and the 150 ms control fails; restored code takes 22.3 ms and passes.
An initial uncontrolled timing check passed on warm adapter state even with
the old code, so it was replaced with this controlled contention test rather
than presented as a useful negative control. The dependency hook exists only
in Probe builds. Offline cached images, constrained-network downloads and
restricted background revalidation remain covered.

**The recurring graphics pauses remain unresolved.** Background timer work
now queues independent dispatcher callbacks, alongside the WinUI pulse timer.
Both are delayed during the stalls; this is not merely pulse-timer coalescing.
Synchronous article-selection setters take less than 7 ms in the captured run.
Native copied-context unwinding, correlated to 431-483 ms queue delays, gives:

```text
WinUI dispatcher/render tick
  CCoreServices::NWDrawTree
    CCoreServices::SubmitPrimitiveCompositionCommands
      DCompTreeHost::PreCommitMainDevice
        DirectComposition / Direct2D flush
          NDXGI::CDevice::ReclaimResources
            Intel user-mode graphics driver
              NDXGI::CDevice::ReclaimAllocationsCBImpl
                D3DKMTReclaimAllocations2
```

The WinUI and D3D11 names were resolved from Microsoft's public PDBs matching
the loaded binaries. This identifies the blocking call chain, not a complete
proof of which driver, operating-system memory policy or framework interaction
causes its delay. It is not evidence that the entire pause is CLR GC, HTML
generation or a synchronous article database query.

| Experiment | Observed boundary |
|---|---|
| Default standalone runtime, Intel graphics | Repeated selection/scroll queue stalls around 400-500 ms. |
| Existing software-page-rasterization option | Did not eliminate stalls; WinUI itself still uses native graphics. |
| Disable GPU in the private WebView environment | Did not eliminate stalls; this is not a fix for XAML's compositor. |
| Temporarily keep the private window topmost | Did not eliminate stalls. No input-ownership checks were bypassed. |
| Private executable's Windows high-performance GPU preference | The NVIDIA module was actually loaded. Selection queue delays fell to about 6-13 ms, but separate runs still had a 496 ms scroll delay or a 381 ms native window-resize delay. Partial mitigation, not a full fix. |
| Installed production runtime rather than the older self-contained probe | WinUI `3.1.8.2608` loaded from the installed package, versus probe `3.1.8.2602`. The Intel path still recorded selection delays up to 465 ms and a scroll queue delay of 536 ms. The problem is not confined to the older diagnostic runtime. |

All temporary per-executable GPU preferences were removed. The normal app's GPU
choice was not changed. No always-on-top, disabled-GPU or private WinUI hook is
being shipped as a supposed fix. The documented WinUI ForceWarp mechanism found
in framework source is a global runtime/debug setting, not a validated
application-only release contract; it was not enabled.

The remaining safe next boundary is an authorized graphics/Windows runtime
repair or driver comparison and remeasurement on an owned interactive desktop.
Live-provider credentials, real reading profiles and unrelated applications
are not needed for the existing isolated reproducer. A broad driver/policy
change on this shared machine was not made without authorization. **This
follow-up does not claim the user's no-long-pauses requirement is satisfied.**

To prevent another false green, `performance.json` now records scenario and
selection timestamps, synchronous setter time, individual dispatcher delays,
the native runtime/driver identity, `ResponsivenessPassed` and a **100 ms
queued-dispatch budget**. `VerifyReader.ps1 -Performance` supplies
`--require-responsiveness`, which fails when that budget is exceeded.
`--performance-only` can still collect a failing workload for diagnosis, but its
explicit responsiveness result must not be confused with structural success.
Neither path claims physical-input verification when `ForegroundOwned` or
`PhysicalInputMeasured` is false.

The full installed-runtime reader gate executed 16 groups: 12 passed, while
native keyboard ownership, two visible-screen pixel checks and the strict
responsiveness check failed. The latter correctly rejected a **424.0 ms**
queued-dispatch delay; `ResponsivenessPassed` was false. Seven separately
launched final native checks passed: cold network startup, constrained/offline
policy, network-driven thumbnail heights, grayscale revision sharing,
measurement retention, status-only invalidation and timeline separators. Each
also passed state capture and drained shutdown. These positive checks do not
override the remaining failing latency/input gate.

The final standalone strict check also rejected a 515.2 ms delay. Every scenario
had real queued-dispatch samples and zero enqueue failures; the normal Debug
assemblies were inspected to confirm that neither `ParityProbe` nor the
slow-network dependency hook was present.

Additional retained artifacts include `pause-followup-backend-final`,
`pause-network-startup-controlled-reverted`,
`pause-network-startup-controlled-restored`, `pause-original.nettrace`,
`pause-native-unwound-gaps.json`, `pause-native-symbolized.txt`,
`pause-private-native-gpu`, `pause-native-nvidia-gaps.json`, and
`pause-installed-performance`, `pause-final-reader-gate`, and
`pause-final-strict-dispatch`. REA native evidence:
`ev_37d3e846a58545c9d643634c14e3298143cb66c3c773af1952fe1d66a28c1d7a`
and `ev_42ecdb35ced581b99edb675a3ed028ca50f905a7aaa2852d3466f2044c98c7f7`.
Microsoft reference:
[D3DKMTReclaimAllocations2](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/nf-d3dkmthk-d3dkmtreclaimallocations2).

### Reader input follow-up, November 16

The user reported another content-panel pause exceeding half a second and then
explicitly requested: "turn off scrolling inertia when using scroll wheel or
touchpad (touchscreen should still have inertia)".

The new `useWheelInertia` preference defaults to false for both fresh and
existing profiles. It replaces the old browser-only workaround control rather
than treating an old saved `useDirectBrowserScrolling=false` as permission to
retain the superseded inertia default. Other preferences are preserved. At this
checkpoint, native sidebar, timeline and horizontal-column scroll viewers
applied wheel policy to their actual template controls and restored inertia
for touch/pen input. The article-list exception below supersedes that part.
Browser panes share the direct-scroll environment and omit the extra easing
wheel listener when inertia is off. The browser path still uses the tested
WebView2 `--disable-smooth-scrolling` flag rather than an OS-wide setting;
browser changes require restart.

Controlled comparisons found application-added browser coasting of roughly
230-250 ms in original-source controls. The final restored run had zero added
animation interval, with actual browser wheel-event-to-frame times of 15.2 ms
in the reader and 32.9 ms in the embedded page. Browser touchscreen motion
continued 242.7 pixels after release. These are isolated-profile/browser-input
measurements, not a physical touchpad or touchscreen certification.

An important diagnostic correction separates automation request queuing from
input latency. DevTools can delay creating a synthetic input event for hundreds
of milliseconds; timing from the command request misattributes that wait to
scrolling. The independent driver now records trusted wheel-event timestamps,
dispatch delay and composited frame timestamps in the browser clock domain.
It installs its event listener in an isolated world, so disabling publisher
JavaScript does not disable the instrumentation. Request-to-input delay remains
visible separately in the report.

**The reported physical-input pause is not certified fixed.** Native injection
could not acquire foreground ownership on this desktop. The current isolated
host check also still exceeds its 100 ms budget during load/resize, although
its scrolling samples were below 60 ms. The earlier WinUI/DXGI stalls remain a
separate unresolved boundary; an intentionally blocked renderer can also delay
scrolling independently of inertia. No global input settings, graphics-driver
settings or real reading profile were changed. Removing app-added coasting
must not be presented as proof that every long input/presentation stall is gone.

### Native article-list scrolling follow-up

The later request is explicit: "Remove whatever fancy custom algorithm you're
using to customize its scrolling behavior, and just make a regular scrolling
box." The article list and its horizontal column wrapper now retain WinUI's
native scrolling defaults. They no longer attach wheel interception,
per-device inertia switching, manipulation cancellation or custom middle-button
autoscrolling. The remaining input customization is explicitly sidebar-only.

Normal selection reveal, keyboard navigation, virtualization and thumbnail
viewport tracking remain. The sidebar/browser inertia preference does not
change article-list scrolling, and its description now states that scope.
Future ports must not reattach the custom input behavior to the article list.

## Making subsequent ports safer

1. Keep `UPSTREAM_SYNC.md` and the source-commit ledger as the source-history
   checkpoint, but carry the still-open PR-02/03/04/06/07/08 gaps forward independently and retain
   PR-01 as an accepted exclusion. "Not currently
   implemented on Windows" is not a reason to treat shared behavior as irrelevant.
2. Record each intentional UX exception with its user request and a native
   regression. Do not infer product decisions from AppKit/UIKit file locations.
3. Move cross-platform policy out of Windows-only view models where practical:
   image selection, command availability and preferences should have shared
   contracts; WinUI should own rendering and focus rather than duplicate policy.
4. Maintain actual call-site/behavior mappings, not merely matching filenames,
   copied plist files or passing helper tests. Update test inventories when
   upstream adds or changes assertions.
5. Keep the renderer/OPML/state regression gates mandatory and add a controlled
   foreground performance baseline with presentation/input evidence. Report
   distributions, allocations and retained state, not only averages or a
   structural "Passed" flag.
6. Resolve the release-provider configuration and safe retention migration
   explicitly, and require the latency gate as well as functional tests. These
   remain major obstacles to a
   low-surprise, nearly mechanical future porting workflow.

The lowest-risk functional follow-ups are command routing and shared avatar
selection; the theme work is complete. Retention and provider delivery
need more careful integration; CloudKit is intentionally excluded.
The review intentionally did not enable destructive retention, invent service
credentials, change an operating-system policy, or silently accept the remaining
product gaps.
