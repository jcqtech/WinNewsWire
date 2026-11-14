# Test parity audit

## Current verification

The 2026-11-14 follow-up review is in [PARITY_REVIEW.md](PARITY_REVIEW.md).
The complete backend solution executed 2,460 cases: **2,459 passed, one
pre-existing AccountManager fixture failed, none skipped**. Current regular and
Probe x64 builds pass, as do focused native regressions for the recent ports and
audit corrections. A pre-sync production build was also measured for comparison.
The foreground input/screen-presentation gate could not be certified on the
available desktop; long-tail UI pauses remain an explicit limitation.

The suite mapping and dated results below describe the older baseline and
subsequent historical runs, not a current claim of exact test parity or complete
runtime verification. Counts of source test functions and expanded xUnit cases
must not be used as a product-parity percentage.

Reference: NetNewsWire `fb086eb7684e9fdb90855c4e033a6beba4325c5e`
(2026-10-16 checkout at `D:\NetNewsWire`).

**The Windows suite is not yet an exact copy of the upstream suite.** This audit
replaces the previous document's stale claim of a complete line-by-line comparison.
The current upstream inventory contains 81 test files declaring 713 non-performance
test functions under module tests and `Tests\NetNewsWireTests`. Parameterized tests
expand into additional cases; these numbers are not comparable to an xUnit result
count and must not be used to calculate a parity percentage.

The inventory covers all those files. Detailed input/assertion comparison was
performed for the suites marked **Ported** below, not for every upstream assertion.
Benchmark files, iOS tests, and commented-out tests are outside this inventory.
Their exclusion is not a claim that equivalent Windows coverage exists.

## Running the Windows tests

```powershell
dotnet test WinNewsWire.Tests.slnf --nologo
```

The solution filter excludes the WinUI and MSIX projects, but includes every backend
test project and its project references. Account credential tests use Windows DPAPI
with a temporary data-directory override; they do not read real account credentials.
HTTP tests use injected handlers or a bounded loopback server; they do not contact
live account services.

### Repeated application bugbash

The 2026-10-29 bugbash adds `-Bugbash` to `Windows\Diagnostics\VerifyReader.ps1`.
It requires the supplied 50-feed OPML and a populated, isolated local profile,
copies that profile, and uses the installed WinUI runtime in an actual Debug
process. The final workload passes 11 user-journey/shutdown groups, followed by
three fresh-process restart groups. It exercises 236 article reads across all
50 feeds, 188 status actions, 12 publisher pages, 200 rapid selections, actual
subscription/folder dialogs, rename/delete/undo/redo, three real Save/Open OPML
round trips, malformed input, cancellation, and 20 window closures. Restart checks
53 subscriptions, folder memberships, edited names, and 20 reading statuses.
All 322 Core cases and 36 Images cases pass; the new typed-URL cases share the
existing activation rules instead of changing the native string-normalization
port. A separate complete parity-probe run passes all 47 registered UI groups,
including Find, speech routing, preferences, statistics, stale-feed management,
multi-window restoration, clipboard failure handling, and browser history.

The close stress includes a deliberately unfinished native Find operation.
Closing its window must complete without that operation finishing. It also
requires collection of closed windows and a bounded GDI-object delta; the final
run measured 111 before and 111 after the 20 closes. The separate
`--window-resources` probe contrasts unreset native title bars, reset title bars,
and application windows. Plain controls and WebView2 did not reproduce the
per-window growth. A private-process heap inspection and a selected-article
regression additionally identified the missing article-event unsubscription.

Some older probe failures were measurement/setup defects, not application bugs:
collapsed or unarranged controls, ambiguous template descendants, asynchronous
Find result notifications, inherited reader mode, and synthetic filename changes
that did not update the shell picker's internal name. The picker driver now uses
owned-window mouse/keyboard input; no overwrite confirmation is accepted.
Animation sampling captures only the visible private title-button region instead
of allocating/redrawing a full-window bitmap for every sample. Browser link
actions wait for hit-testable layout in an isolated world, which continues to run
when publisher JavaScript is disabled.

Browser timings in `bugbash.json` distinguish opened pages from pages that
actually scrolled. Zero-event samples are not evidence of smooth scrolling.
The input is injected mouse-wheel input, not a precision touchpad, and
`requestAnimationFrame` gaps are not actual presentation measurements. Heavy cold
pages can still exceed 200 ms on the ordinary GPU path; the existing presentation
trace gate remains the appropriate separate performance check.

The mandatory reader regression gate is
`Windows\Diagnostics\VerifyReader.ps1` (PowerShell 7, interactive Windows).
It checks actual TRX execution, named real-control outcomes, process exit and
persisted window state; compilation or zero discovered tests cannot satisfy it.
The deterministic cases include denied homepage metadata with a working root
favicon, visible icon pixels in the real timeline, enabled/disabled publisher
scripts, the actual `NavigateToString` navigation URI, row identity and retained
read articles, cross-window status changes, and icon-only updates that retain
the current document and scroll position. `-LiveAtlantic` explicitly enables
the network-backed reproduction; ordinary test runs do not require the site.

The regression repair executed all 36 Images cases, including five cases that
failed before the fallback fix. It also ran the original 23-case Shared
renderer/favicon subset and 30-case Atom suite, with no skips or assembly
renaming. The live UI reproduction stored and displayed all 25 Atlantic entries
and both actual icons in a private profile. Full UI parity is still incomplete:
a wider run found an existing toolbar search-focus failure and did not finish
the browser-preference check. These results do not turn earlier compile-only
Account/Shared work into executed evidence.
Older "blocked" labels below describe prior attempts, not a fresh policy
determination. Rerun original identities and record their actual execution
rather than assuming those restrictions still apply.

The pane-header redesign adds `-HeaderLayout` to the reader gate. The run with
`-LiveAtlantic` executes 13 UI groups, all passing, including unchanged reader
checks and three header/column/customization groups. Forty Shared toolbar cases
execute, with seven additional cases for pane ownership, overflow and preserving
legacy/fixed preference entries. The UI checks cover actual native title-bar
input rectangles, compact/larger-text search, unread subtitles, command placement,
combined column headers, non-banded row styling, flat sorting buttons, line
chevrons, left-aligned narrow tables, merged footers and persistence/rollback.
OS-wide DPI settings are not modified; four scale-factor rounding cases supplement
the current-scale and real-resize checks. A scoped customization run now passes
the earlier search-focus/typing assertion.

The following theme/acrylic group verifies insertion at the end of the bounded
customization list, realized-container scrolling, repeated add/move/remove cycles,
saving/opening Article Theme, and actual acrylic color contribution from content
behind both headers. `-HeaderLayout -LiveAtlantic -UseInstalledRuntime` passes 14
UI groups and 36 Images cases on the same installed WinUI version as normal Debug.
Its runtime metadata distinguishes that execution from older self-contained runs.

The 2026-10-20 run adds `-SidebarLayout -Navigation`. The combined installed-runtime
run executes 20 UI groups, with 17 passing and three failing, so the mandatory
reader gate remains failed. The original batch-delete/undo assertion fails both
before and after these changes. The two favicon groups are blocked by the
existing `WinNewsWire.ActivityLog.dll` application-control denial (`0x800711C7`);
the standalone 36-case Images suite still executes successfully.

Passing new checks cover the original sidebar geometry and actual light/dark
accent pixels, retained account disclosure, title-bar Back/menu/title ordering
and input regions, native original-large/flattened-small icon pixels, and Back
across article/feed/search/multiple selections and embedded websites. They verify
native form and SPA history, interleaved reader/web sessions, default and
new-window links, isolated reader bindings, synthetic-click rejection, page Find,
current-page share data, redirects, failed-page Back and independent windows.
The restored article retains its DOM and scroll position; same-scope Back does
not replace the timeline or sidebar collections. Forty-seven toolbar unit cases
pass, including version-1 migration and preserving version-2 custom removal.
The loopback server handles concurrent connections so browser preconnects cannot
block a later request. The ordinary Debug configuration compiles separately
without overwriting either running user application; no package was installed.

The column-header follow-up adds a fifth `-HeaderLayout` group. It compares the
actual positions of Refresh, Add and the sidebar feed filter before/after layout
switches in all three display modes at normal and larger title text. It requires
the title/table-header junction to be within 0.05 physical pixels and have no
intervening divider, and checks hidden-sidebar overflow with a focused feed
command. Actual toggle-pattern invocations verify hide/show results, retained
current articles and failed-write rollback.

Simple filtering passed before the change, but the rapid-reading regression
exposed a real ordering bug: the filter query could finish before automatic read writes.
The filter now awaits those writes and ignores superseded scope/filter requests.
All eight cycles pass after the fix across both layouts. All five header groups pass;
the focused UI run at that point was 12/14 because of the same two ActivityLog-related
favicon failures. The 56-case toolbar/timeline unit subset and all 36 Images
cases execute successfully; these counts do not certify the blocked integration.

The subsequent UX performance run found Smart App Control already Off and
successfully executed the original favicon cases without a policy workaround.
`-Performance` adds a native icon-contrast/cache group and a 2,000-article UI
workload covering both layouts, selection, scrolling, resizing and a dispatcher
heartbeat. It records CPU, allocation, virtualization and UI-work counters in
`performance.json`, and asserts that unchanged article selections do not
remeasure/rebuild header columns, raster scrolling does not use compositor
readbacks, handled cancellations do not write stack traces, and hidden websites
suspend without losing form/history state or freezing on rapid return.

The combined `-Performance -HeaderLayout -Navigation -LiveAtlantic
-UseInstalledRuntime` run passes all 20 UI groups and 36 Images cases; the
performance-only repeat passes 11 groups. All 25 live Atlantic articles render.
The 45 native-image/luminance cases cover decoded color/alpha, malformed data,
background-thread decoding and existing scaling/orientation behavior.
An initial WinRT pixel-analysis experiment failed native UI/lifetime checks and
was discarded; the final file-backed path reuses the existing WIC adapter.
The temporary debugger helper was removed. CPU/wall-time numbers remain
machine/workload observations, not general latency guarantees.

`-ArticleScrolling` adds direct article-WebView coverage rather than relying on
the timeline-scrolling benchmark. It uses a long image/link fixture and an
optional explicitly supplied Reader View fixture; private-window native wheel
input is guarded by foreground/pointer ownership checks. Browser-generated
gestures provide controlled width, host-message, background and shadow
comparisons. Raw frame/input/CPU/callback measurements are retained separately
from pass/fail assertions.

Both widths showed about 17-ms steady-scroll 95th-percentile browser frame
intervals; the reported sustained visual lag was not reproduced. The actual
overhead regression was per-frame host telemetry and unrelated count retallies.
The new assertions require bounded/coalesced updates, zero hover retallies,
the exact final cached position, rejection of stale-page scroll updates, and
Back immediately after scrolling. Existing disabled-publisher-script interaction
coverage is included. Browser frame callbacks are not claimed as proof of all
OS screen-presentation paths.

The focused direct-scrolling/navigation run passes all 14 UI groups and 36
Images cases, including disabled-script controls and scroll/Back restoration.
Broader header runs also reproduced an existing column-acrylic visual assertion;
that appearance issue was not changed or counted as passing by this scrolling
fix. The copied article fixture and its private test profiles were removed,
leaving timing/counter reports without copied article content.

Webpage presentation coverage holds a loopback response while the browser still
contains a distinctly colored previous page. Actual window pixels verify that
the loading surface hides that page, and frame samples verify that only the
requested page is revealed. The test also covers pending Share/Open in Browser
metadata, cancellation by Back, superseding requests, navigation failures and
native-history form-state restoration without inserted blank entries.
The navigation/performance gate passes 15 UI groups and all 36 Images cases.
Ordinary Debug compiles separately without replacing the running user instance.

`-LongSession` runs a 300-article soak with repeated native-web Back, main-window
and secondary-window lifetimes, UI-idle collections, browser-process memory,
DOM/listener counts, native/app history lengths, and scroll-frame samples.
Results are `long-session.json` and `long-session-scrolling.json`. The timeout is
ten minutes only for this explicitly selected workload. User profiles are not
loaded and user processes are not collected or stopped by the runner.

The initial soak retained all eight closed main windows. After explicit native
event detachment and removal of window-root compiled-binding activation hooks,
all eight main and eight secondary windows are collectible. The new formatter
test separately demonstrates that a cached summary no longer keeps a
1,000,000-character source body alive. All 23 formatter cases pass.
The combined long-session/navigation gate executes 14 UI groups and 36 Images
cases successfully. Late-session browser-frame samples stay near 17 ms p95;
remaining GDI growth is isolated to window creation and is retained as an open
native-resource finding rather than hidden by the managed-GC results.

Title-bar motion coverage uses the real stock WinUI animated Back/menu sources
and native button visual states. Captured glyph bounds show horizontal menu
compression and rightward Back shrink; frame samples verify release rebound,
settling and rapid/interrupted-state recovery without resizing the 32-DIP input
targets or rebuilding header columns. The foreground override used for pixel
sampling restores its original local-value state so later dark-mode checks are
not contaminated. Native animation settings are respected, not changed.

`-ToolbarControls -SidebarLayout -UseInstalledRuntime` verifies the intentionally removed
upstream-news Help action, the consistently sized textbox/textbox-checkmark icons and their
read/unread toggle and overflow behavior. It also constrains the customization
viewport with larger text and checks real command-label bounds against both
scrollbars. Scrollbar arrow glyphs are not treated as command labels. The
combined run passes 17 UI groups and all 36 Images cases; customization stays
non-virtualized and cancelling leaves its configuration unchanged. Icon checks
cover state/action mapping, Mark All Read, independent geometry ownership and
overflow copies. The disclosure checks require zero collection resets, zero
unchanged-selection notifications, no account-header unload and retained
surrounding row containers during collapse/expand, alongside existing filter,
selection and restoration tests.

The later static-heading update keeps account children permanently available
and ignores legacy account-collapse state. The sidebar gate checks equal
account/Smart Feeds label positions, a 10-DIP content gutter, actual folder
glyphs versus feed favicons, folder selection/filter restoration, and both old
saved-state formats. Folder expansion samples verify increasing opacity and a
negative vertical offset settling to zero, with no fade on persistent rows.
Repeated toggles must leave no owned animation, transform, collection reset,
selection churn, or header unload. Only visible revealed children are animated;
the Windows animation preference is respected.

The subsequent ordering update verifies non-selectable account labels without
highlighting and sun/reading-list/star smart-feed icons. System glyphs are
14 DIP in fixed 16-DIP slots, with a measured six-DIP icon/label gap for smart
feeds, folders, and root/nested feeds. Feed favicons keep their original size.
It adds five
actual mouse drags in the launched application: root reordering, dropping into
a collapsed folder, insertion before a folder child, reordering within a folder,
and moving back out to the root. The driver uses an independent input thread,
guards foreground/pointer ownership, releases its own mouse button, and waits
for `DragItemsCompleted`, not merely a model change. Checks cover persisted
ordering, live updates in another window, undo/redo, insertion/folder feedback,
stable heading containers, and unchanged provider copy restrictions.
The combined sidebar/toolbar gate passes 19 UI groups and 36 Images cases.
The ordering/subscription command subset passes 22 cases, including preference
write failures, membership rollback, retriable undo, independent account/folder
orders, stable folder IDs across renames, and malformed saved preference data.

Sidebar Delete-key coverage uses native keyboard input on single and multiple
selections, confirmation/cancellation and grouped undo. Timeline and reader
focus must not delete subscriptions; Delete still edits text in search, inline
rename and the embedded browser. Smart feeds and mixed ineligible selections
are protected, and repeated Delete cannot stack confirmations.

Sidebar scrollbar coverage measures its four-DIP gutter offset without changing
label alignment, clicks the native scroll arrow in that added space, and drags
the far edge of the separate eight-DIP resize handle.

The pane-header probe measures the rendered right edge of article and webpage
footer text in column layout, keeps counts on the left with a gap, and verifies
trimming at narrow widths and left alignment after switching back. Toolbar
coverage distinguishes the plain Mark All as Read checkmark from the individual
textbox read/unread icons, including separately owned overflow icons.
Toolbar hiding now verifies collapsed title/tool hosts, zero-height header rows,
recovered reader space, and no leftover timeline inset in standard, column, and
reading-only layouts. Native clicks reach the floating options over both article
and embedded-browser content; customization and restoration remain available,
including at minimum width and after opening another window. Find controls and
column headings stay unobstructed. The floating button keeps at least eight DIP
clear of real WinUI/browser scrollbar rails and shares the hidden header's
background through layout and theme changes. Shared visibility, keyboard focus, fixed
search, failed-write rollback, and the original browser document are preserved.
The minimum-width check queries the native `WM_GETMINMAXINFO` resize limit,
measures a 300-DIP article pane in standard, column and reading-only layouts,
and reopens an undersized saved window at that same minimum without recreating
the current article browser. Four scale cases cover per-edge pixel rounding;
compact menu overflow must expose the rightmost menu. The installed-runtime
header gate passes 21 UI groups and 36 Images cases. At the tested 150% scale,
the native minimum is 488 physical pixels and every layout retains exactly
300 DIP of article width.

The menu-overflow group measures progressive rightmost collapse and restoration
and the centered 20-DIP ellipsis icon. It uses a native mouse click on the icon,
invokes ordinary and checked commands through submenus, and verifies a hidden menu's native Ctrl+M
shortcut and displayed shortcut labels. It covers nested dynamic window entries,
long headings, resizing while a popup is open, and rapid resize recovery.
Canonical command controls remain in their original menu; overflow entries bind
their state and invoke the corresponding native Invoke/Toggle action. Reparenting
opened WinUI menu controls was rejected after reproducing native ownership errors.

`-OpmlFixture <absolute-path>` adds a real-file import/refresh workload in the
isolated profile, including feed identity/title checks, on-disk mirror/export
comparison, repeat-import idempotence, native sidebar/timeline scrolling and
actual embedded publisher-page scrolling. The supplied 50-feed fixture yielded
12,525 stored articles; the largest 2,846-article timeline is exercised with read
articles visible, in both regular and column layouts. The live-file timeout is
15 minutes, and unavailable endpoints/page attempts are recorded explicitly.
The file is read in place, never edited or imported into the user's profile.

The import settings-write regression measures row writes with an SQLite audit
trigger: 150 before, 50 afterward. The fetch-date reset and unsupported-response
tests also failed before their fixes. Seventy targeted Account/OPML/settings and
14 original OPML parser cases pass. The final live/toolbar workload passes
eleven UI groups and 36 Images cases, with native F11 input preserving fullscreen
behavior and the window-wide shortcut hint suppressed.

`-RefreshScrolling -ProfileDirectory <isolated-profile> -OpmlFixture <file>
-UseInstalledRuntime` now tests the missing concurrent case. It uses an actual
Debug build, starts the complete application runtime, keeps all 50 subscriptions
and over 12,000 stored articles, and drives native wheel input from a separate
thread while the real Reload button refreshes feeds. The private conditional
fetch/content caches are reset to ensure real download/reparse work.
Publisher and YouTube samples must satisfy the runner's explicit interaction
budgets and preserve timeline collection identity. `-BrowserTrace` additionally
captures Chromium `EventLatency`, `PipelineReporter` and frame-presentation
events on the first and refreshed scroll of both sites.
`MeasureBrowserPresentation.ps1` writes the four actual-presentation summaries
to `browser-presentation.json`; each needs at least 100 completed scroll updates,
p99 at most 75 ms and maximum at most 100 ms. Scoped async-ID pairing, ID reuse,
non-scroll events, negative durations and empty traces were checked with
synthetic inputs, and real-trace results match an independent parser.
JavaScript `requestAnimationFrame` timing alone cannot pass this presentation
gate.

The final default-hardware run recorded scroll-presentation p99 of 33.7 ms for
the publisher page and 50.1 ms for YouTube during active refresh, with no full
sidebar/timeline replacements. A separate initial-page GPU-rasterization stall
remains. The retained default-GPU first-scroll traces fail the new presentation
budget, despite passing host-side refresh checks. Native HWND hosting, matching
Edge's engine version, fresh/local caches, different GPUs and removing the
hidden reader did not resolve it. Detailed GPU tracing now identifies blocking
ANGLE/Direct3D shader compilation, while the GPU process has AboveNormal priority
and no execution-speed throttling.

The explicit, default-off compatibility-rendering preference uses the measured
software-rasterization workaround. `-CompatibilityRendering` seeds the private
profile's saved preference, not an environment flag. The full runtime passed
both sites before and during refresh (presentation p99 33.7-49.7 ms; maximum
50.5 ms; zero updates over 100 ms). Thirty preference cases and 27 focused UI
groups pass, including visible control save/reopen, failed-save rollback, the
restart boundary and sharing a running browser/profile across new windows.
The compatibility long-session gate passes 300 article visits, 12 webpage
round trips and 16 window closures without retained closed windows/models or
pending browser tasks. The ordinary GPU-mode issue remains reproducible;
compatibility mode is an experimental workaround, not a fixed driver/runtime.
Database coverage is now 90 passing cases. The full Account run is 326 passing /
one pre-existing unsupported-default-profile expectation failure, not a green
full Account suite.

The two-finger-scroll follow-up adds a distinct **motion-response** check:
the earlier native injection is a mouse-wheel stream, not precision-touchpad
hardware. `refresh-scrolling.json` explicitly records that distinction.
The probe measures direction changes and observes a 400-ms settling period;
it no longer treats prompt frame presentation as proof that content follows
input promptly. Its actual JavaScript calculation was checked against delayed,
immediate and missing responses, horizontal events and control-wheel exclusion.

`-DirectBrowserScrolling` seeds only the private saved preference. The full
50-feed run passed the presentation gate and required at least two measured
reversals per stage, each at most 75 ms. Typical maximum responses were
0.3-1.9 ms, with a 16.9-ms worst response across eight stages, versus roughly
119-137 ms in the earlier animated-wheel samples. Native arrow keys and actual
scrollbar drags pass in both modes. Thirty-two preference cases and 16 UI groups
pass for the enabled path, and 16 groups pass for the default path.
The physical precision-touchpad path and subjective gesture feel remain
unverified; synthetic wheel results must not be relabeled as hardware coverage.

The interruptible-inertia follow-up adds `-InertialScrolling -InertiaFocus`.
Its 12-case acceptance matrix uses single notches and bursts separated by
coasting, then verifies that the opposite input wins while inertia is still
active. It includes the native scrollbar and column layout. All 180 focused
reversals passed, with native response below 75 ms and composited browser
response below 39 ms; coasting remains enabled. Nested scrolling, RTL/Shift
input, page-owned wheel handling, reduced motion, keyboard interruption, reading
position capture, and favicon-triggered row rebinding have regressions.

The input helper is now the minimal `Tools\WheelInput` console program; the
PowerShell-hosted injector and its no-GC-region experiment were replaced.
`CaptureScrollFrames.mjs` captures actual composited scroll offsets in a
separate Node.js process so observing frames does not add managed UI callbacks.
The diagnostic DevTools endpoint is loopback-only and absent from normal builds.
The reader's isolated-world hook suppresses per-frame host position reports
without enabling publisher scripts.

That inertia-focused functional run passed 29 UI groups and 36 Images cases.
At that stage the broader 32-case forced-refresh stress gate was not green.
Those reports are retained separately; the later refresh-isolation work below
addresses the remaining host/input delays without weakening the inertia checks.

The 2026-11-09 heavy-refresh follow-up adds `-RefreshFocus` for the four
startup/manual-refresh cases, plus per-case start timestamps and timeline
add/remove/replace/reset counts. `-RuntimeDirectory` can reuse an already built
diagnostic runtime; it reads assembly metadata without executing it and rejects
normal application binaries before launching anything. Timing limits and
independent input/frame-capture requirements are unchanged. `-MemoryPressure`
also exercises cache cleanup during the focused refresh workload.

New regressions bound XML token allocation across 10,000 elements, avoid
decoding ignored/overridden RSS text, reuse committed statuses without redundant
inserts, and reuse response scratch buffers across different worker threads.
Response-admission tests cover concurrent headers, bounded bodies through
callbacks, cancelled waiters, failed callbacks, reuse of a completed session,
and stalled-body timeouts. Launched checks cover lazy row presentation,
property notifications, in-place content updates, and preservation of selection.
Cached set queries are traced to reject unnecessary SQL ordering while retaining
correct newest-subset selection for limited queries. Memory-pressure checks
retain live article objects and evict unreferenced cache entries.

The implementation passes 441 Parser, 95 ArticlesDatabase, 121 Web, 383 Shared,
339 applicable Account cases, and the final 21 reader UI groups plus 36 Images
cases. Eight real-worker integration cases cover process isolation, conditional
requests, concurrent read/star changes, cancellation, restart after a crash,
account shutdown, missing-helper errors, and parent-pipe closure. Metadata tests
check single-write updates, concurrent preferences and failure rollback.
One separately reproduced Account baseline failure is not part of this fix:
`AccountManagerTests.UnsupportedAccountFilesAreKept` also fails against the
pre-change assemblies because its fixture assigns a non-local account type to
`OnMyMac`; that unrelated profile-validation discrepancy is not changed here.

The final full matrix passes all 32 cases and all 150 coasting reversals.
Heavy-refresh maxima were below 38 ms for native direction response and below
61 ms for browser document response. The gate requires a separate worker PID,
substantial worker allocation, and monotonic timestamps proving at least
25 parsed/committed feeds during active input (48 were observed). It also
rejects passive collection changes before wheel input stops. It does not
mistake a slow final network response for deferred feed processing.

Input-driver collections are checked over the actual injection interval;
post-input quiet-period collections are reported separately. Reports stream
as UTF-8 off the dispatcher after observers detach, rather than allocating
large cumulative JSON strings that contaminate the next measurement. Native
two-contact scrolling during refresh passes the presentation gate, below
36 ms in the recorded run. No experimental GC configuration is retained.
The self-contained MSIX helper was verified from the actual package payload
with global .NET lookup disabled, including database initialization and cleanup.

Attach the profiler to the initialized reader process: launching the entire
harness under a suspended diagnostic port also suspends its independent .NET
input helper and does not produce a valid input workload.

Earlier rapid-wheel coverage added a separate input process so host GC could not pause the
injector along with the UI. The 22-workload gate exercises 120-ms reversals with
2-, 4-, and 8-ms packet cadence, a slow-wheel control, and 1.5 seconds without
input. It covers the article list and both browser controls, standard/column
layouts, unfocused panes, startup/full-refresh pressure, and real native
touch-to-wheel transitions. Visible-row tracking distinguishes actual motion
from virtual extent corrections. Chromium presentation timestamps are aligned
to the injector's last packet rather than the last delivered browser event.
The runner rejects missing/duplicate cases, shared-process or GC-paused
injection, delayed reversals, late motion, and late browser presentation.
Replay checks verified that recorded 250-350-ms delays cannot pass the gate.

The final two runs passed with native stopping below 33 ms, browser presentation
stopping below 35 ms, and reversal responses below 64 ms. Functional validation
also passed 25 UI groups and 36 Images cases. Parser coverage now has 439 passing
cases, including Unicode trimming and a one-MiB body allocation regression.
Plain/formatted title recycling is checked by the reader's existing article
group. GC tuning and experimental ToolTip reuse are not part of the production
fix. These are native injected-wheel results, not physical touchpad or
mouse-driver certification.

Cold catch-up coverage now includes startup presentation tracing and native
sidebar/timeline scrolling while all supplied feeds refresh. The
`-ControlledScrollPages` option isolates host refresh pressure from changing
publisher scripts, overlays, and video controls; live-site testing is still the
default. Wheel pacing uses a private high-resolution waitable timer, not a global
timer-resolution or power-policy change. Direction measurements preserve callback
order when wheel and scroll events have identical timestamps; a browser-side
regression exercises that case.

High-resolution wheel, notched wheel, and two-contact browser runs passed all
five presentation captures, with no scroll update over 100 ms. Native pane phases
use independent wheel input, including in the two-contact browser run. Functional
checks hold timeline preparation on a worker while the dispatcher responds,
change selection during that work, and reject results superseded by a new search.
Transport tests preserve the exact 64-MiB boundary and verify pooled scratch-buffer
reuse and owned response bytes. Account tests retain the previous salted hash
while bounding hash allocations and the parsed-feed backlog. All 116 Web cases,
19 targeted Account refresh/lifecycle cases, and 36 Images cases pass.

The 2026-10-31 follow-up adds native two-contact touchscreen injection and
notched-wheel input alongside the high-resolution wheel driver. The touch
workload requires two trusted simultaneous contacts in every phase; the full
eight-phase functional run passed with 50 feeds and 12,525 starting articles.
The repeated touch-trace timeout was resolved by limiting routine recording to
input-scrolling/latency events; detailed renderer/GPU tracing remains opt-in.
Consecutive trace captures and the full eight-phase touch run now pass with all
four presentation traces. The final touch gate includes first-contact and
inertial updates (p99 33.6-33.8 ms, maximum 49.9 ms, no updates over 100 ms).
`-ScrollInput TwoContactTouch` verifies trusted contact counts in every phase;
`NotchedWheel` and `HighResolutionWheel` retain distinct input and event budgets.
The presentation parser now counts both phases and has a synthetic case that
would expose a previously omitted 300-ms first-scroll stall.

Managed profiling isolated a separate 306-ms UI-thread notification registration.
The native API now registers/unregisters in background tasks, with initial
refresh waiting for registration and shutdown waiting for cleanup. A new
launched-UI check holds registration while verifying dispatcher responsiveness,
closes during registration, checks exactly-once teardown, and verifies reported
registration failure. The default reader gate passes 13 UI groups and 36 Images
cases; the fixed full-runtime high-resolution wheel gate also passes.

Account-row checks now select the actual On My PC header in both themes, compare
the title's vertical center with its container, and verify no accent pixels
overlap the inset chevron. They also collapse/expand the account and verify that
normal feed rows retain their accent marker afterward. The combined
header/sidebar/navigation/performance run passes 24 groups and all 36 Images
cases; the original sidebar batch-delete/undo group now executes successfully
as part of that run.

**Historical execution restriction:** Windows Application Control blocked
`Parsers.Tests.dll`, `Account.Tests.dll` and `WinNewsWire.Tests.dll` with
`0x800711C7`. VSTest reports no available tests but exits
zero, so exit status alone is not sufficient. Do not disable the policy or
rename/repackage assemblies to evade it. Their reruns require approved execution.
The block also applied to the then-current `WinNewsWire.dll` application: probe 52
failed before `App` construction. The `.NET Runtime` event log records the same
`0x800711C7` policy error. No alternate binary or execution route was attempted.
Before the policy also blocked Account and Shared, twelve projects were run
separately with their TRX counters checked: **878 executed, 878 passed, none
skipped**. A later account-only run passed 289 cases, and four new cache-cleanup
cases also passed separately. Three new ArticlesDatabase statistics/vacuum cases
subsequently executed and passed; that checkpoint contained 65 cases. The later
complete 66-case run is reported below. The latest adaptive status-polling, provider-count
and background-flush additions compiled but could not execute their tests.
The last complete thirteen-project run passed 1148 cases before the later
rendering, account and lifecycle work.

The newest affected permitted full runs executed **295 Core, 37 ActivityLog, 10 Database,
83 ArticlesDatabase, 7 SyncDatabase, 9 ErrorLog, 29 HTMLMetadata, 31 Images
and 47 RemoteAccounts cases**,
all passing, none skipped. These include the native column-layout preference,
browser-preference persistence, all nine original browser-path functions and
Windows path/launch/quoting cases; feed/theme/widget/file activation parsing,
native command-line tokenization and notification identities; and the new native
metadata database/retrieval/discovery contracts. Twenty-eight new Core cases
exercise real Windows image decoding, frame extraction, scale decisions and
pixel output. Fifteen new RemoteAccounts cases exercise HTTP ownership and OAuth
shutdown. The earlier two Account metadata-notification cases and seven new
account-level ownership cases, an additional feed-settings initialization case,
and shared cache cleanup/theme integration cases compile but have not executed. Their restart
fixtures use fresh delegates and isolated transports rather than reusing a
delegate whose account already disposed it.

The new targeted Web fixture run executed **16 cases**: the three native isolation,
three method-matching and four request-count functions, plus six Windows
regressions. It does not replace the earlier 99-case full Web run. Ten new
default-account cases and two Account-owned HTTP-scope counterparts compile but
have not executed in the original Account assembly. They have not been copied
to the permitted Web or RemoteAccounts projects.
This checklist pass reran 245 Core, 31 Images and 47 RemoteAccounts cases plus
the 16 targeted Web cases: **339 executed, 339 passed, none skipped**. Other
full-suite totals retain their earlier evidence; this was not a complete
sixteen-project run.
The following navigation/preferences continuation executed all 269 Core cases.
Its 24 new refresh-preference cases cover every old Windows value, stable native
values, deprecated ten-minute polling, fresh versus existing implicit defaults,
unknown values/versions, durable notifications and read/write failures. The
focused preference run executed 26 cases; its first run exposed missing-parent
preference writes, which were fixed before the full run passed.
Six subsequent module-owned latest-update-date cases brought ArticlesDatabase to
72 passing cases. They cover the native date-precedence-before-MAX contract,
missing status/content joins, fractional pre-epoch UTC dates, case-distinct feed
IDs, update/delete visibility, 1001 feeds and cancellation/disposal errors.
RemoteAccounts was also rerun with 47 passing cases. These three full current runs
total **388 executed/passed cases**, not a full sixteen-project or UI pass.

A later full ArticlesDatabase run executed **83 cases**, including eleven new
native-retention regressions. They cover explicit preserve-history mode,
read/unstarred sync-body eligibility, separate 30-/180-day orphan-status rules,
unsubscribed feeds, empty subscription sets, large batches, two-second budgeting,
rollback and preservation of referenced authors/attachments. No new cleanup API
has been wired into startup, and no existing profile has opted into deletion.
The historical 72-/388-case checkpoints above are not the latest storage totals.

New original-assembly additions compile only: three Account news-subscription
cases; 28 Shared unread-display cases; nine Shared refresh-enum cases; three
Shared folder-default cases; one shared add-feed ordering regression; and
17 Shared row-traversal cases. They were not moved into Core or another
permitted assembly. Reading the compiled neutral resource entries established
resource packaging, not execution of those blocked Shared tests.
Dinosaurs adds eight Shared model cases and four all-membership command cases
covering month/DST boundaries, strict age comparison, sorting and null ordering,
active/subscribed feeds, cancellation, removal rollback, provider-wide deletion
and retry-safe restoration. These original Shared cases remain blocked.
Toolbar configuration adds 22 source-expanded Shared cases and toolbar layout
adds eleven, for 33 new cases. Their version handling, persistence, rollback,
overflow/focus protection, tracking separators and spacing are compiled only,
not executed. Normal, Probe and original Shared-project integration builds pass.
The Current Activity/share continuation ran 285 Core and 37 ActivityLog cases.
Sixteen new Core cases reject non-subscription share payloads and preserve native
feed URL wrappers. Three new ActivityLog cases cover immutable snapshots,
concurrent transitions without duplicate/lost entries, and associated URL
inspection. Six Current Activity timer/display/lifetime cases remain in the
original blocked Shared assembly and compile only.

The new Chromium extension has thirty Node component tests covering protocol
handoff, forbidden pages, narrow permissions, popup state, preference persistence
and failed-write rollback. They run against a local DOM/browser-API fixture
without loading WinNewsWire or a blocked test assembly. They do not establish
installed Edge/Chrome, external-protocol or Share Target behavior.

The automation continuation executed all 295 Core cases. Eight new transport
cases cover UTF-8 byte framing, limits, invalid/truncated input, malformed-client
recovery, isolated current-user pipe roundtrips, cancellation/drain and hashed
user/profile names. Two opt-in cases verify the default-disabled setting and
failed-write rollback. Native Windows pipe creation additionally sets
`PIPE_REJECT_REMOTE_CLIENTS` and a current-user access rule.

Ten scripting-service cases compile in the original Shared assembly: the six
native script counterparts (including the no-result Open Location contract)
plus collection scope, native article IDs/status mutation, contextual
create/delete and read-only/sensitive-property rejection. The generic
`Geoducks!` case remains a harness result, not evidence of application execution.
No blocked scripting assertions have been moved into the Core transport suite.

The two new module test assemblies cover newly implemented metadata behavior;
they do not contain relocated Account/Shared/Parser tests or load the blocked
application. The new Database suite covers its own connection/journal behavior
and previously missing native string fixtures, not relocated blocked tests.
The solution filter now includes sixteen test projects. Historical
counts in the table below intentionally remain the earlier thirteen-project
inventory.

| Test project | Cases in the last broad checkpoint |
|---|---:|
| `Modules\Account\Tests\Account.Tests` | 285; execution is now blocked |
| `Modules\ActivityLog\Tests\ActivityLog.Tests` | 34 |
| `Modules\Account\Tests\RemoteAccounts.Tests` | 32 |
| `Modules\Articles\Tests\Articles.Tests` | 5 |
| `Modules\ArticlesDatabase\Tests\ArticlesDatabase.Tests` | 62 |
| `Modules\Core\Tests\Core.Tests` | 128 |
| `Modules\ErrorLog\Tests\ErrorLog.Tests` | 9 |
| `Modules\FeedFinder\Tests\FeedFinder.Tests` | 9 |
| `Modules\NewsBlur\Tests\NewsBlur.Tests` | 9 |
| `Modules\Parsers\Tests\Parsers.Tests` | 434 previously passed; latest full rerun unverified |
| `Modules\SyncDatabase\Tests\SyncDatabase.Tests` | 7 |
| `Modules\Web\Tests\Web.Tests` | 74 |
| `Tests\WinNewsWire.Tests` | 224 at the broad checkpoint; latest full rerun unverified |
| **Checkpoint total, excluding the already-blocked parser project** | **878** |

The baseline had 295 cases. Additional cases include both upstream ports and
Windows-specific regressions; the increase does not count upstream functions
one-for-one.

## Suite mapping

**Ported** means the functional inputs and assertions were translated, including
parameter cases and fixtures where applicable. **Partial** means related Windows
coverage exists but the current upstream suite is incomplete or not fully
re-audited. **Missing** means the original suite has no equivalent test port.
**Platform/harness** requires a different runtime adapter or test harness.

Paths below are relative to each upstream package's test directory. Windows module
tests live at `Modules\<module>\Tests\<module>.Tests`; shared application tests live
at `Tests\WinNewsWire.Tests`. C# spelling/casing follows the existing port.

### Account

| Upstream suite | Functions | State and Windows counterpart |
|---|---:|---|
| `AccountCredentialsTest` | 1 | Ported: `AccountCredentialsTests`; DPAPI substitutes for Keychain. |
| `AccountOPMLLoadTests` | 3 | Ported: `AccountOPMLLoadTests`. |
| `AccountSettingsImporterTests` | 10 | Ported; includes the URL dictionary/base cases and an additional binary-plist regression. |
| `FeedSettingsDatabaseTests` | 2 | Ported; feed metadata now uses the matching SQLite schema. |
| `FeedSettingsImporterTests` | 20 | Ported; XML/binary plist migration and Windows JSON migration are integrated. |
| `IsolatedWebserviceResponsesTraitTests` | 2 | Two Windows counterparts now compile in Account: explicitly owned response scope and the original 418-versus-200 isolation assertion. Handler identity replaces Swift task-local test-ID binding; execution is blocked. |
| `Feedbin\AccountFeedbinFolderContentsSyncTest` | 1 | Ported: original initial/add/delete fixture sequence and folder/root counts. |
| `Feedbin\AccountFeedbinFolderSyncTest` | 1 | Ported: original 9/8/10-folder sequence. |
| `Feedbin\AccountFeedbinSyncTest` | 1 | Ported: original 224/225-feed sequence and metadata assertions. |
| `Feedbin\FeedbinFeedNameTests` | 1 | Ported in `Feedbin\FeedbinFeedNameTests.cs`, with original JSON fixtures. |
| `Feedbin\FeedbinStatusSendTests` | 2 | Ported in `Feedbin\FeedbinFeedNameTests.cs`; failed sends still allow other groups and article refresh. |
| `Feedly\FeedlyActivityMessageTests` | 7 | Ported and used by production activity logging. |
| `Feedly\FeedlyCollectionParserTests` | 2 | Ported in `FeedlyModelParserTests`; includes RTL sanitization. |
| `Feedly\FeedlyEntryParserTests` | 8 | Ported; includes publisher/crawl date choice, parsed items, authors, tags and URLs. |
| `Feedly\FeedlyFeedNameTests` | 3 | Ported: `Feedly\FeedlyFeedNameTests.cs`. |
| `Feedly\FeedlyFeedParserTests` | 2 | Ported in `FeedlyModelParserTests`. |
| `Feedly\FeedlyFolderReconciliationTests` | 13 | Ported; plus durable article-ID mapping and interrupted-migration replay tests. |
| `Feedly\FeedlyReauthorizationTests` | 2 | Ported; plus concurrent refresh coalescing and transient-failure retry tests. |
| `Feedly\FeedlyResourceIdTests` | 1 | Ported with all four original resource inputs. |
| `Feedly\FeedlyTextSanitizationTests` | 1 | Ported as 12 theory rows retaining all original inputs. |
| `Feedly\FeedlyUnmarkableArticleIDsTests` | 4 | Ported; sender drops unmarkable read changes and acknowledges successful chunks separately. |
| `NewsBlur\NewsBlurFeedNameTests` | 2 | Ported: `NewsBlur\NewsBlurFeedNameTests.cs`. |
| `ReaderAPI\ReaderAPIEntryTests` | 6 | Ported; includes signed/high-bit hexadecimal IDs and Old Reader IDs. |

### Storage and infrastructure

| Upstream suite | Functions | State and Windows counterpart |
|---|---:|---|
| `ActivityLog\ActivityLogTests` | 31 | Ported, plus owner-renaming, concurrent-ID and cache regressions; integrated with refresh/mutations and a Windows Activity window. |
| `Articles\AuthorCacheTests` | 5 | Ported: `Articles.Tests\AuthorCacheTests.cs`. |
| `ArticlesDatabase\ArticleUpdateTests` | 4 | Ported: `ArticleUpdateTests.cs`. |
| `ArticlesDatabase\StatusDivergenceTests` | 7 | Ported: `StatusDivergenceTests.cs`; C# awaits repair explicitly. |
| `ArticlesDatabase\TodayQueriesTests` | 8 | Ported: `TodayQueriesTests.cs`, plus a rolling-day boundary regression. |
| `CloudKitSync\CloudKitSyncTests` | 1 | Platform: CloudKit is unavailable in this Windows implementation. |
| `ErrorLog\ErrorLogDatabaseTests` | 3 | Ported: full field assertions, insertion order and 200-entry pruning on reopen; existing Windows cases retained. |
| `FeedFinder\FeedFinderTests` | 4 | Ported in the module-owned FeedFinder test project. |
| `NewsBlur\NewsBlurTests` | 1 | Upstream's empty placeholder is retained explicitly; eight substantive Windows model/API cases accompany it. |
| `RSDatabase\DatabaseTests` | 2 | Functional inputs/assertions ported in `DatabaseStringTests`, including all five original strings, the generated long mixed body, SQL NULL and empty text. Actual SQLite UTF-8 bytes are checked; Swift's `isContiguousUTF8` storage predicate has no CLR-string equivalent. |
| `SyncDatabase\SyncStatusTableTests` | 4 | Ported: `SyncStatusTableTests.cs`, plus acknowledgement/retry/limit regressions. |

### Core

| Upstream suite | Functions | State and Windows counterpart |
|---|---:|---|
| `CGImageLuminanceTests` | 5 | Ported through the RGBA/BGRA adapter, with alpha/format/boundary regressions; used by the adaptive WinUI icon control. |
| `CollapsingWhitespaceTests` | 13 | Ported, including all ASCII-whitespace parameter cases and NBSP preservation. |
| `HMACTests` | 6 | Ported, including pinned UTF-8 vectors. |
| `MacroProcessorTests` | 3 | Ported; existing C# inputs/assertions match all three original functions, with an additional unterminated-macro case. |
| `MainThreadOperationTests` | 9 | Ported on an owned synchronization context, plus background completion/cancellation/cycle tests. The Windows view-model uses the queue. |
| `MD5Tests` | 3 | Ported; known-vector parameter cases expand to four xUnit cases. |
| `MultilineTextFieldSizerTests` | 1 | WinUI counterpart verified by the isolated probe: all 17,280 original text/size/width combinations compared against real control `DesiredSize`. |
| `SingleLineTextFieldSizerTests` | 1 | WinUI counterpart verified by the isolated probe: all 56 original text/size combinations. |
| `String+RSCoreTests` | 10 | Ported in `StringExtensionTests`, excluding the separate performance function. |
| `StripHTMLTests` | 16 | Ported, including the original ten HTML/expected-text resource files. |
| `URL+RSCoreTests` | 5 | Ported in `UrlExtensionTests`. |

### Parsers

All 330 declared functions in the current parser inventory now have their
functional inputs and assertions ported. The 434 expanded Windows cases passed
before Application Control blocked the assembly's later execution; a current
parser-suite pass is not claimed. RSS, Atom, HTML and OPML use the
byte-oriented scanner/delegate architecture, including namespace scopes and
raw XHTML capture. Windows OPML additionally retains strict whole-document
validation before the SAX pass, so malformed imports cannot silently succeed.

The 55 parser fixtures are verified against Git blob IDs at the pinned revision,
not merely against the Windows reference worktree: `core.autocrlf=true` changes
checkout bytes and initially broke the pinned article-length assertions. Fixture
exports use `git -c core.protectNTFS=false -c core.autocrlf=false archive`;
`.gitattributes` disables conversion for the copied resources.

| Upstream suite | Functions | State |
|---|---:|---|
| `Feeds\FeedParserTypeTests` | 6 | Ported with original URLs, plus native sniffing fallbacks and async parsing/cancellation. |
| `Feeds\JSON\JSONFeedParserTests` | 7 | Ported, including complete author records; numeric IDs, malformed arrays and 64-bit attachment regressions added. |
| `Feeds\JSON\RSSInJSONParserTests` | 1 | Ported; native calculated-ID and enclosure-length regressions added. |
| `Feeds\XML\AtomParserTests` | 30 | Ported: nested xml:base, raw XHTML, source/author handling, summary promotion, pinned items and namespace suppression. |
| `Feeds\XML\OPMLTests` | 6 | Ported in `OpmlTests`, plus malformed XML/cursor regressions. |
| `Feeds\XML\RSSParserTests` | 29 | Ported: RDF identifiers, pinned MD5s/bodies, GBK, CDATA, Markdown, text-input and namespaced subtrees. |
| `HTML\HTMLLinkTests` | 7 | Ported, plus nested-text-reset and detached-delegate regressions. |
| `HTML\HTMLMetadataTests` | 13 | Ported, plus complete Open Graph images, favicon deduplication and icon-size regressions. |
| `HTML\HTMLRelativeURLResolverTests` | 14 | Ported; shared rendering and Atom xml:base use the resolver. |
| `Utilities\DateParserTests` | 30 | Ported; every assertion runs through both byte and string entry points. |
| `XML\EntityDecodingTests` | 21 | Ported; native entity table, Windows-1252 remapping and malformed-input rules replace WebUtility decoding. |
| `XML\XMLASCIITests` | 30 | Ported, including the exhaustive byte-range assertion. |
| `XML\XMLAttributesTests` | 22 | Ported: qualified names, case folding, dictionaries, iteration and Unicode. |
| `XML\XMLEncodingTests` | 25 | Ported: BOMs, aliases, CJK/Cyrillic/European code pages; invalid UTF-16 regression added. |
| `XML\XMLEntitiesTests` | 32 | Ported with all parameter inputs and byte-offset assertions. |
| `XML\XMLNamespaceContextTests` | 17 | Ported: built-ins, default scopes, shadowing and balanced pops. |
| `XML\XMLSAXParserTests` | 40 | Ported, plus raw-comment preservation, callback failure/reuse and retained-character-snapshot regressions. |

### Web

| Upstream suite | Functions | State and Windows counterpart |
|---|---:|---|
| `DictionaryTests` | 4 | Ported: `WebExtensionsTests` has the same inputs and assertions, including accents and emoji. |
| `DownloadSession429Tests` | 2 | Ported, plus exact-host cancellation, empty/throttled completion, one-shot-cache bypass, drained callbacks and cross-refresh throttling regressions. |
| `HTTPLinkPagingInfoTests` | 4 | Ported: `HttpLinkPagingInfoTests`. |
| `MacWebBrowserTests` | 9 | Ported pure path-formatting inputs/assertions in `BrowserPathDisplayTests`, including off-root volumes. Windows drive/UNC cases and launch-request/argument-quoting cases are additional. OS discovery/launch remains a platform adapter. |
| `SpecialCasesTests` | 6 | Ported: domain/subdomain and Relay FM path checks. |
| `StringTests` | 1 | Ported: `WebExtensionsTests.HtmlEscaping` has the original input and expected entities. |
| `TestingURLProtocolIsolationTests` | 3 | Functional cases ported and executed in `TestingHttpMessageHandlerIsolationTests`: concurrent 418/503 responses, reset isolation, and 404 shared responses. Sharing is explicit handler injection rather than a global fallback test ID. |
| `TestingURLProtocolMethodMatchingTests` | 3 | Original URL/method/status inputs and assertions ported and executed in `TestingHttpMessageHandlerMethodMatchingTests`; specific methods precede wildcard responses. |
| `TestingURLProtocolRequestCountTests` | 4 | Original 0/1/2 counts, method narrowing, unstubbed counting and cross-scope isolation ported and executed in `TestingHttpMessageHandlerRequestCountTests`. |
| `URLPreparedForOpeningTests` | 4 | Ported; shared scheme filtering is wired into browser-opening commands. |

### Shared application

| Upstream suite | Functions | State and Windows counterpart |
|---|---:|---|
| `ArticleRenderingSpecialCasesTests` | 14 | Ported; redirect removal, Slashdot, YouTube and paragraph behavior are wired into rendering. |
| `ArticleSorterTests` | 16 | Ported, including the current date/feed/title/unread/starred parameter cases. |
| `ArticleStringFormatterTests` | 20 | All ported, including the three attributed-string identity/content cases and cached object identity. |
| `ExtractBodyFragmentTests` | 8 | Ported. |
| `NSAttributedStringHTMLTests` | 13 | Ported to immutable attributed-text runs; actual WinUI font/decorations and grapheme-safe truncation also verified by the isolated probe. |
| `SanitizedTitleTests` | 25 | Ported; theory rows retain upstream case names and parameter variants. |
| `SharingTests` | 2 | Portable subject-line assertions ported; Windows native share-sheet behavior still needs platform verification. |
| `ThemeUnzipTests` | 2 | Ported with the original base64 ZIP fixtures; Windows-specific extraction/installation regressions added. |
| `ScriptingTests\ScriptingTests` | 6 | Windows object/command counterparts added in `ScriptingServiceTests`, retaining original inputs/assertions with an explicit JSON/pipe adapter. Compiled only in the original Shared assembly. AppleEvent binary behavior and actual application execution are not claimed. |

## Additional Windows regressions

The Windows-only tests cover OPML restart recovery and failed writes, nested/empty
folders, URL case sensitivity, shared feed memberships, public/private export
attributes, import cancellation and concurrent-import rejection, remote HTTP
contracts, credential lookup metadata, full-text index migration/update/scoping,
large sync queues, and preserving newer/pending status changes. A real HTTP
loopback case also exercises local OPML import through download, parsing, and
article storage, including canonicalized URL keys and custom-title preservation.

Additional cases cover service mutation methods/paths/form bodies, failure-safe
membership, subscription deletion/move undo, timeline multi-selection identities,
asynchronous status-command failures, account preference persistence, and active
SQLite feed settings. Existing article IDs and statuses survive Feedly feed-ID
repair; interrupted repairs are replayed on reopening.

These augment upstream coverage rather than replace it. No live service credentials,
macOS test execution, visual comparison, or file-picker/OneDrive UI automation was
used to claim parity. See [PARITY.md](PARITY.md) for the ordered remaining work.

Further regressions now cover parser-identity migration without losing article
IDs/statuses, three-minute one-shot caching and independent caller cancellation,
exact 64 MiB response bounds, redirected feed discovery, persisted one-hour OpenRSS
throttling, native Feedbin conditional snapshots and status methods, Feedly local
discovery/initial articles, protected credential bundles, same-identity reconnect,
account shutdown/removal, and remembered add-feed destinations. The large-result
storage case lowers SQLite's actual bind limit to 500 and round-trips 1001 articles
with distinct authors and attachments; it first reproduced `too many SQL variables`.
The new database maintenance cases verify feed-scoped article/read/starred counts,
orphaned status counts, empty/large scopes, explicit vacuum preservation and
failure reporting. The large-scope assertion lowers SQLite's actual parameter
limit to 500 and includes duplicate subscriptions across a 1002-feed scope.
The three targeted maintenance cases were rerun with three executions and three
passes verified in the TRX counters.

Remote lifetime regressions verify owned versus borrowed HTTP clients for all four
providers, rejection of calls after disposal, asynchronous delegate cleanup,
draining cancelled Feedly authentication, coalesced shutdown, callback failures
and rejection of credential persistence after disposal. Four older status-send
fixtures were corrected to create known rows with the appropriate initial flags;
unknown/unchanged IDs still do not queue status changes in production.

The original Account test assembly contains the corresponding account/storage
ordering and failed-construction/adoption regressions. It remains blocked:
compilation is not an executed account-level verification result, and these tests
were not moved into RemoteAccounts.

## Windows interaction harness

`Windows\Diagnostics\ParityProbe.cs` is compiled only with `ParityProbe=true`.
It uses a new private profile, a private WebView2 data directory and a loopback
fixture server. The normal application startup refresh is not run. It drives
the production article controls, not an HTML-only substitute. The latest complete
WinUI probe 51 passed all nineteen groups, including actual add-feed destination
selection, URL normalization, cancellation, independent main windows, shared
status propagation, desktop memory-pressure registration, cache cleanup and
runtime shutdown. Statistics, inactive-account totals/sorting/vacuum, independent
sidebar selection and mixed deletion undo are exercised through real controls.
Account disclosure/unread-feed filtering, legacy expansion migration,
resizable statistics, native column rows/header actions and remembered split
geometry are included. Full screen is exercised through its accessible menu and
the isolated reader's F11 handling. Window-targeted key messages exercise an
exact eight-DIP divider step; global keyboard injection is not used.
Window restoration verifies independent sorting, read filters and selected
scopes, folder expansion, unread-selection preservation, reader/scroll state,
normal/maximized/minimized geometry, off-screen clamping, stale IDs, failed saves
and closing an initializing window. Fresh last-close and two-window Quit runs
exited with code zero, and their persisted sessions matched the captured snapshots.
These UI assertions do not cover the new adaptive-sync/provider
tests blocked by Application Control.

The browser-selection/navigation UI check initially could not start after the
application DLL became policy-blocked. It now passes in the 2026-10-20
installed-runtime run: duplicate-name labels, preference reopening, background
inversion, embedded default links and explicit external opening are exercised.
External launch requests are captured rather than starting a real browser process.
The current probe also contains an image-pipeline group: a loopback
PNG is downloaded through the production feed adapter, bound using the real
converter and IconView, and checked through decoded dimensions and rendered image
pixels. The fixture was changed from SVG to PNG for the raster-decoder path.
This group passed in the 2026-10-19 reader gate; its current execution is blocked
by the ActivityLog dependency as recorded above. The broader harness
also validates native duplicate/service-specific drop restrictions and the real
mixed-account transfer command path, plus external-drop default destinations.
Its newest group invokes the article/external URL menu commands, checks
multi-selection payload order, enablement and shortcuts, and injects a clipboard
failure. It captures data packages instead of reading or replacing the user's
system clipboard. These are pending checks, not evidence of
pointer-driven dragging. The backlog no longer claims that native drag/drop has
grouped undo: the pinned implementation does not register drag/drop undo.

Five further groups cover Go/Help routing, unread display modes across windows,
folder destination/cancellation behavior, cross-scope Next Unread and native
WebView2 Find. They verify intended native engine/control behavior rather than
relabeling helper tests as UI evidence, but remain compiled and unexecuted.
The reader group now also rejects synthetic publisher key events. Find requires
the stable API introduced in SDK 1.0.3405.78 and captures real match counts,
case/word options, Unicode, wrapping, selected text, document replacement and
close races when run on an approved runtime. No blocked application was run to
test the dependency upgrade.
The Dinosaurs group adds actual threshold, selection, confirmation, all-membership
undo/redo and retained-history checks without touching the system clipboard.
The toolbar group covers production customization, state preservation, modes,
overflow and search restoration. There are thirty normal-path groups: 28
functional groups and two shutdown groups. At that checkpoint, none of the newer
groups had executed; later reader-specific evidence is recorded above.
Current Activity and incoming shared-feed groups add live state/linger,
close/reopen, data-format precedence, cancellation and failure-propagation checks.
They do not simulate the Windows share broker or prove cross-process
ShareOperation lifetime. The current total is thirty-two normal-path groups.
The automation group adds opt-in/current-user IPC, feed-supplied article IDs,
status mutation, sensitive-property rejection and disabling the endpoint.
It compiles but has not executed; the latest total is thirty-three groups.
The following speech group raises the current total to thirty-four. It captures
selected/body text through the production reader path, substitutes playback to
avoid user audio, and checks replacement, owner-specific stop, cancellation and
reported failures. It compiles only and does not establish installed-voice,
speaker or actual synthesis behavior.
Release packaging initially selected an older WebView2 projection despite the
newer application dependency. An explicit matching packaging reference fixed
MSB3277; metadata inspection verifies both packaged projections expose Find at
version 1.0.3405.78, without loading either assembly. The known legacy UAP
`plist-cil` restore warning is separate from that resolved conflict.

Successful runs established the measurement matrices above, native page scripts,
all five article font sizes (14/16/18/20/22 px), custom theme CSS/relative assets,
trusted processing while untrusted article JavaScript is disabled, and reader
mode retaining its theme. They exposed and now cover selection changed while a
database fetch was in flight, explicit scroll capture and isolated callbacks
when publisher scripts are disabled, and the difference between system close and programmatic
`Window.Close()`. Both use the production close coordinator, and shutdown is
asserted after the actual final window closes rather than stopping the runtime
in advance. Inspect each `results.json` group's `Passed` value; neither
building the probe nor a zero process exit proves its assertions passed.
Results are written after each group so a partial file from a crashed process
cannot be mistaken for a full successful run.
Compare `expected-windows.json` with the private profile's saved `mainWindowStates`
after process exit; `--quit` selects the two-window exit case.

Browser DOM checks and control assertions are not a complete visual comparison,
accessibility audit, or pointer-driven drag/drop test.

Deployed activation is also unverified. Compile-only package checks inspect all
seven URI schemes, both file types, the notification CLSID match and the resolved
COM executable path; they caught an unresolved `$targetnametoken$` that the package
build itself accepted. The corrected path is checked against the archive entry.
Core activation tests retain `+`, decode percent escapes once, reject malformed
and duplicate query parameters and do not resolve redirected relative files
against another process's working directory.

The metadata suites verify native JSON/property names and Unix dates, the exact
149-hour/eleven-day/five-hour/five-day retry boundaries, thirty-/thirty-three-day
cleanup, cache eviction/restart, stale-success preservation and failed-write
atomicity. Fake HTTP exercises final-URL parsing, request coalescing, caller and
shutdown cancellation, image candidate fallback and declared-icon precedence.
Startup maintenance now verifies the native thirteen-day `RSDatabaseInfo`
schedule, exact due boundary, Unix/fractional dates, other-key preservation and
failure without advancing the date.

Image tests now use the real Windows Imaging Component decoder, not a substituted
signature check. Core cases independently decompress the generated PNGs to compare
actual colors and alpha, check aspect ratios/bounds, choose the first
largest-dimension ICO frame, preserve indexed palettes, extract animated GIFs,
apply all eight EXIF orientations, retain small single-frame bytes and reject
undecodable/truncated bodies. The initial run exposed incorrect COM array
marshaling and mirrored EXIF mappings; both were corrected before the complete
Core run passed. Image-pipeline cases exercise those bytes through
memory/disk caches, activity failure reporting, five-day retries, declared-icon
fallback, local resampling of legacy cache entries and corrupt-file repair.
These component results do not substitute for the blocked WinUI image check or
the full platform format/monitor matrix.

Twenty-eight binary-cache cases verify exact caller-supplied filenames, unsafe-key
rejection, Unicode names, atomic replacement, cancellation, concurrent requests,
recreating a purged directory, deleting from a missing directory and preserving
locked-file deletion errors. The two missing-directory cases first reproduced
`DirectoryNotFoundException`; the complete 245-case Core run then passed.
Image cases verify native MD5/directory layout,
separate icon/favicon caches, native-file precedence, legacy SHA1 reads without
network access or deletion, migration-write failure, restart and restoring missing
disk files from memory.

The new Database suite's ten cases include a consistent copy of a private legacy
database and its committed WAL, without a copied shared-memory file. Opening it
through the production queue recovers text, blobs, FTS and schema metadata before
switching to DELETE journaling. A live reader deliberately blocks another
transition; the failure retains the WAL and data, and retry succeeds after the
reader closes. Other cases cover NORMAL synchronization, the in-memory exception,
serialized work across awaits, rollback, disposal and setup-failure cleanup.
The lock fixture uses a bounded one-second provider timeout: zero means unlimited,
not an immediate failure. The first unbounded fixture run was stopped and is not
a passing result. The full ArticlesDatabase suite now executes 66 cases, including
failed schema setup releasing its file handle without removing existing data.

The memory-pressure probe registers real Win32 notification handles and invokes
the cleanup path; it does not exhaust system memory to force an OS notification.
The three-day disk-cache cleanup tests use private directories and retain
unrelated browser/account data.
