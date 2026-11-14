# NetNewsWire parity and regression review

Review date: 2026-11-14.

Reference: NetNewsWire `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a`.
Windows comparison: pre-sync `106603d128a34c17907ba10625e754b88cb8ff0a`
through the current upstream ports and the audit corrections below.

## Bottom line

**The core reader is substantially implemented, but full product parity has not
been achieved.** Local feed reading, subscription management, OPML, article
storage/search, read/star state, modern media rendering, timeline preferences,
and the main desktop tools have real production paths and substantial executed
coverage. This is considerably more than a UI remake or a collection of stubs.

There are nevertheless material gaps: iCloud synchronization, turnkey
configuration/verification for some remote providers, author-avatar display,
eight bundled themes, some keyboard navigation, and activation of the native
database-retention policy. Platform delivery and complete UX/accessibility
equivalence also remain unverified.

The 505-entry upstream ledger is complete as a **commit-processing record**.
It does not mean every pre-existing NetNewsWire feature exists in Windows.
Neither a commit count nor an xUnit case count is a defensible parity percentage.
The next update can start from the recorded source tip, but it is not yet a
low-risk mechanical translation exercise. Source commit `8060219b...`, skipped
because Windows lacked the affected theme bundles, is a concrete example of why
the carry-forward parity gaps must remain visible beside that checkpoint.

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
| Timeline and sidebar | Broad coverage, with identified gaps | New appearance controls work. Author-avatar selection, bundled-theme breadth and some keyboard routing are incomplete. |
| Remote synchronization | Implementations, not full service certification | Feedbin, Feedly, NewsBlur and Reader API variants exist; mock HTTP tests are not live-provider proof. |
| Desktop tools | Implemented | Find, activity/error logs, statistics, stale-feed management, speech routing and multiple windows exist. Some OS-level outcomes still need interactive verification. |
| Sharing, extension and scripting | Windows adapters exist | Share-target declarations, an Edge/Chrome extension and an opt-in named-pipe scripting API are not merely placeholders. Installed handoff and release delivery are not certified here. |
| iCloud/CloudKit | Missing | Explicitly rejected by the Windows account factory; no equivalent synchronization bridge exists. |
| Localization/accessibility/release | Partial or unverified | Neutral English resources and selected accessibility behavior exist, not a complete language/Narrator/IME/RTL or installed-upgrade matrix. |

## Confirmed gaps and recommended order

| ID | Priority / effort | Gap and evidence | Recommended work |
|---|---|---|---|
| PR-01 | High / XL | **No iCloud account support.** `Modules\Account\Sources\Account\AccountDelegateFactory.cs:15-16` rejects CloudKit; `Windows\Dialogs\AccountSheets\AccountTypePickerDialog.cs:30-41` does not offer it. Upstream owns a CloudKitSync module and CloudKit account delegate. | Decide on a supported Windows integration or explicitly accept the exclusion. Another RSS provider does not synchronize the user's existing NetNewsWire iCloud account. |
| PR-02 | High / M-L | **Some offered accounts are not proven turnkey in a distributed app.** `FeedlyClientConfig.cs:24-46` reads external OAuth configuration; `AccountSetupSheets.cs:187-203` stops setup when it is absent. `ReaderAPIAccountDelegate.cs:30-31` reads Inoreader application identifiers from environment. | Establish a release configuration/client-registration contract and exercise sign-in, refresh, reconnect and state synchronization against authorized live accounts. Do not expose secrets in source or logs. |
| PR-03 | High / M-L | **Production does not activate startup retention.** `ArticlesDatabaseStore.Retention.cs:12` has an explicit policy API, but its callers are tests only. Upstream `Account.swift:348` calls startup cleanup; `ArticlesDatabase.swift:423-434` explains the database-growth/performance requirement. | Design a safe migration/opt-in policy, then wire it into startup with large-profile checks. Do not silently delete existing Windows history to close the gap. |
| PR-04 | Medium / M | **Author avatars are not actually selected/downloaded for display.** `Windows\ViewModels\MainViewModel.cs:1373` populates icons through `PreferredIconAsync(feed)`; it does not consult authors. Upstream `Shared/IconImageCache.swift::imageForArticle` prefers a single author's avatar, using `Modules/Images/.../AuthorAvatarDownloader.swift`. | Put author-versus-feed image selection in a shared policy/service, including fallback, refresh, grayscale and lifetime ownership. Automatic icon visibility currently recognizes avatar metadata without completing the avatar display path. |
| PR-05 | Medium / S-M | **Eight canonical bundled themes are absent.** Windows ships Default, Gray and Plum. Upstream additionally bundles Appanoose, Biblioteca, Hyperlegible, NewsFax, Promenade, Sepia, Tiqoe Dark and Verdana Revival in its application resource phases. Windows content inclusion is `Shared\AppShared.csproj:30-32`. | Port the canonical bundles, their notices/assets and current media templates, then exercise them through the actual reader. The deletion of unused duplicate `Resources/Themes` copies must not be confused with removal of the canonical upstream themes. |
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

## Intentional UX versus accidental divergence

Earlier user requests were recovered from the original comparison session,
not inferred solely from current code. Confirmed choices include:

- Pane-based command headers, title-bar search/menu controls, and feed commands
  staying in the sidebar when column layout is enabled.
- Thin gray separators rather than banded column rows, extra bottom padding,
  flat column-header behavior, and a left-aligned narrow table.
- Static account headings aligned with Smart Feeds, inset folder disclosure,
  folder/smart-feed glyphs and the original taskbar icon.
- Responsive menu overflow and the specifically requested wheel-inertia
  interruption behavior.

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
eight canonical themes is a baseline parity gap, not a platform requirement.

## Regressions found and corrected

| Correction | Evidence |
|---|---|
| Restore intentional column separators | `e6c59b6`; the new control fails when the borders are removed and passes with both Windows column styling and standard-row suppression intact. |
| Share grayscale variants across different view histories | `c680e67`; local revision values such as 5 and 1 previously regenerated the same file. The cache now shares one source, checks file versions off UI, and updates bytes/luminance on real changes. Native cache, pixel and reader-response controls pass. |
| Remove duplicate timeline text measurement/retention | `b84b507`; a 500-title control retained 500 extra measurement entries before and zero after. In that controlled run, allocations fell from 2,800,080 to 1,883,904 bytes and elapsed time from 158.1 to 90.6 ms. This is a microbenchmark, not an FPS claim. |
| Stop status-only updates from relaying out command headers | `8d7fab5`; ten hover-link changes now cause zero extra header layouts. Refresh-session enablement remains correct. Sixteen article selections return from 32 header layouts to the pre-sync 16. |

The original/reverted production paths fail the new controls, and restored paths
pass. Related line-budget/full-title/thumbnail, selection, grayscale, reader
identity and shutdown checks were rerun. The older icon-contrast probe was also
corrected to expect the intended translucent system fill rather than the
superseded opaque RGB constants. Its file-invalidation/no-readback checks pass.

## Test and performance results

The complete backend solution executed **2,460 cases: 2,459 passed, one failed,
none skipped**. The remaining failure is the pre-existing
`AccountManagerTests.UnsupportedAccountFilesAreKept`: it places a CloudKit
profile in the permanent `OnMyMac` directory, conflicting with the newer
default-account guard. Its relevant test and guard predate this synchronization.
It was not suppressed or silently changed to make the suite green.

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

The existing reader/performance gate initially had four failing groups:
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

## Making subsequent ports safer

1. Keep `UPSTREAM_SYNC.md` and the source-commit ledger as the source-history
   checkpoint, but carry PR-01 through PR-08 forward independently. "Not currently
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
6. Resolve the release-provider configuration, safe retention migration and
   CloudKit decision explicitly. These are the largest obstacles to a
   low-surprise, nearly mechanical future porting workflow.

The lowest-risk functional follow-ups are the missing theme bundles and command
routing, followed by shared avatar selection. Retention and provider delivery
need more careful integration; CloudKit needs a genuine platform/product plan.
The review intentionally did not enable destructive retention, invent service
credentials, change an operating-system policy, or silently accept the remaining
product gaps.
