# NetNewsWire synchronization

Source repository: `https://github.com/Ranchero-Software/NetNewsWire`

Original port baseline: `fb086eb7684e9fdb90855c4e033a6beba4325c5e`.

Target of this synchronization: `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a` (2026-11-12). **This is a target,
not a claim that every change through it has been incorporated.**

The commit inventory in [UPSTREAM_COMMITS.csv](UPSTREAM_COMMITS.csv) records
individual source commits, changed paths, disposition, and corresponding Windows
commits (semicolon-separated when a Windows adaptation needs a follow-up).
`Pending` means not fully incorporated; it must not be interpreted as a
platform-specific skip. A pending row with Windows commits is a partial port:
its notes identify the missing integration. Source commit IDs are also recorded in the
`NetNewsWire-Commit` trailer of each Windows port commit.

## Incorporation checkpoint

The **fully incorporated watermark is now
`a99fd2e5df5c7b3974a55c22dfaece790056ecaf`**. This synchronization is in progress.
Every inventoried ancestor between the original baseline and this watermark is
ported, covered, or explicitly skipped. The next unresolved first-parent change
is preloading saved URLs before large-row layout, `b17bb24a671a14512cad1178139bf95c9cabd5ec`;
media, image-cache, and appearance work also remains beyond the watermark.

The latest individually ported change by source-history order is
`35c27e3b5fe1044f49fa6c653c01980d9d5b5801` (open the double-clicked timeline row,
not the previously selected article). Its merge,
`98e71194a1b10dc2383e9a5a053cb2aa301b8711`, is recorded as covered.
It is **not** a whole-repository checkpoint. Other completed ports include magnet
links, MacRumors/FreshRSS/Rust reader fixes, raw-text scanning and entity decoding,
footnote positioning, lazy iframes, Plum/Gray themes, and theme-specific reader-icon
grayscale. The default theme now has text-size-aware line lengths and upstream
color, caption, icon, monospace-font, and link-styling refinements, adapted to Windows.

Media RSS is parsed in RSS/Atom, including dimensions, media types, durations,
descriptions, artwork, author Gravatars, rendition selection, and feed-image
filtering. Ordered attachments are stored as article-scoped JSON, with a migration
from the old Windows normalized tables. Reader rendering includes audio/video/image
enclosures, privacy-enhanced YouTube embeds, primary video/audio/image placement,
and legacy-theme enclosure placement. It suppresses resized-image duplicates,
upgrades Flickr body images, normalizes Engadget size prefixes, and avoids stills
of already embedded YouTube videos. Modern templates are selected through
`template7.3.html`, including the shipped Plum and Gray templates.
Separate thumbnail URLs now survive parsing, article equality,
database migration, and cold-cache reads. Feedbin, Feedly, NewsBlur, and Reader API
media metadata is incorporated, including NewsBlur's ordered secure-image mapping.
RSS and Atom now route Media RSS thumbnails to that separate field while keeping
iTunes full artwork independent.

Other ports include feed-title OPML autosave, feed/theme protocol links, and
consistent successful-refresh completion dates. Windows-specific shared adapters
preserve attachment order, dimensions/media types, and thumbnail metadata.
Attachment-clearing updates have regression coverage. Plain text now renders
with paragraphs, line breaks, escaping, and explicit web/mail links. The Windows
detector also recognizes bare domains, `www` links, and email addresses, including
Unicode and punycode hosts, while preserving their displayed text and rejecting
partial malformed addresses and unsupported schemes.

HTML image discovery now has the final upstream lazy-source priority, significant
image filtering, ordered unique candidates, CSS/hidden-image handling, and
bandwidth-aware responsive-image selection. It rejects tracking pixels, sharing
buttons, avatars, and decorative strips. The new finder selects provided images
by thumbnail mode, discovers HTML images off the caller thread, and persists up
to three ordered candidates plus negative results. Queries are bounded, writes
transactional, and results expire after 33 days. Failed URLs fall through to the
next candidate after warm or cold reads. Combined positive/negative in-memory
results reset after exceeding 50,000 articles and reload from persistent records.
Relative images share the reader's article-link, feed-homepage, then feed-URL
base rules, stripping fragments and ignoring unrelated external links. Windows
captures feed metadata on the UI thread before worker discovery; extracted
content and rendering-only URL rewrites retain their separate behavior.
Contentless articles are excluded before database discovery, avoiding stale
HTML thumbnails while permitting discovery if content arrives later.
Chosen URLs, including negative selections, are now cached per immutable article
instance and thumbnail mode, bounded to 50,000 entries per mode. Replacing an
article instance recomputes its selection. Failure notifications invalidate both
modes first and carry only affected article IDs, allowing native rows to refresh
without relying on stale per-view URL snapshots. New HTML records invalidate
dependent choices too; clearing the HTML cache also clears its dependent choices,
so cached selections cannot accidentally prevent a persistent-record reload.
The finder can now preload saved positive/negative URLs without parsing HTML or
publishing availability events. Windows exposes an awaited database operation
rather than blocking WinUI. Missing records remain eligible for later discovery.
Integration before initial large-row layout is the next pending source change;
the new preload API alone is not claimed to eliminate cold-layout movement.
Standard timeline rows now display
small thumbnails with local HTML discovery, persisted-failure notifications,
automatic candidate fallback, and a one-screen download lookahead. Recycled
views reject stale/canceled results and release their image references; models
do not retain a decoded bitmap for every offscreen article. Visible downloads
wait until native layout has settled. Column layout remains thumbnail-free.
Large mode now has a persisted app-wide preference and localized checked View
command, with UI-dispatched updates in other windows and no loss of selection.
Its initial responsive layout was ported separately, then refined to a fixed
180-logical-pixel height aligned with the feed icon (or text without an icon).
Both timeline modes fit the whole image on a theme-aware fill. The reusable view
now always fits and centers images, removing the unused crop/fit option; native
layout updates scaling on resize and clips rounded corners. Permanent failures refresh
only matching rows; download lookahead waits for native layout to settle.
Shared **Small / Large / None** settings now replace the initial large-mode
boolean. The localized View submenu and native Preferences picker stay in sync
across windows. Hidden mode stops new discovery/download admission and retains
compact rows; switching modes restores the selected row to view. Windows retains
an existing large preference until the user explicitly picks a new mode.
Preference-save failures restore the picker and display the error.
Shared **Automatic / Always / Never** feed-icon settings now have a native
Preferences picker and localized Automatic explanation. Automatic shows icons
for mixed containers, or single feeds whose articles include both a byline and
an author avatar (not necessarily on the same article). Standard rows follow
the choice; columns retain feed icons. Coalesced realized-row updates and
thumbnail geometry respect hidden icons without changing selection.
Shared thumbnail availability now fills idle matching visible placeholders
after another consumer downloads their images. Active/unrelated requests and
already displayed images are left alone. UIKit-only preview collections,
their assets/headings, and Swift access-control corrections are assessed
separately rather than reproduced in Windows Preferences.
Reader images matching the selected timeline source now use the same original
bytes through the trusted Windows HTTPS resource origin. Attribute insertion
preserves publisher markup. Metadata discovery runs off the UI thread, and
stale-navigation/shutdown checks protect asynchronous responses. Unavailable
local images fall back once to their original URL in the isolated reader world,
without enabling publisher scripts. None mode leaves reader images remote.
Routed URLs now carry an HMAC-SHA256 signature under a random, process-only
256-bit key. Exact parameter checks and constant-time authentication reject
unsigned or altered URLs before the shared source downloader is called, so
publisher markup cannot use this resource to request arbitrary cache downloads.
Only generated routes are authorized; original remote fallback remains separate.
Windows has no UIKit full-screen image viewer or its timeline-image click flag.
Routing is confined to WebView navigation; shared render/export HTML stays
portable. Selected HTML restores original image URLs in an inert copy document,
retaining absolute links without starting restored-image downloads.

Thumbnail downloading now shares the existing icon resource pipeline without
duplicating its migration, coalescing, backoff, activity, and shutdown behavior.
The new shared `DownloadedImages` cache coalesces reads/downloads across feed
icons, reader images and both thumbnail sizes. It stores original full-size encoded
bytes, including animation, per URL without a second raw HTTP-body cache. Native thumbnails decode
those bytes directly at 256/1200 pixels, respect EXIF orientation, and retain no
per-size encoded thumbnail copies. Scaled icon/favicon representations now also
persist beside the original, keyed by size and encoding, avoiding repeated
resizing after memory eviction or service recreation. Corrupt copies are rebuilt
without poisoning the original URL; changed originals invalidate derived copies.
The source path still identifies the full-size original for reader/native callers.
Derived copies follow source eviction/orphan cleanup and do not count toward
the upstream original-source byte budget.
A shared four-slot cancellation-safe LIFO
scheduler limits thumbnail network requests. Cached images remain available
offline; new downloads honor connectivity and Windows connection cost.
At most 60 requests wait, newest-first; overflow drops the oldest request as
transient without poisoning its URL or imposing a retry backoff. Background
icon checks bypass thumbnail slots so full thumbnail queues do not block them.
Visible thumbnails now take precedence over newer next-screen prefetches.
Queued sources promote by URL when their rows become visible, including
coalesced downloads. Overflow drops prefetches before visible work; both tiers
remain newest-first. Native timeline lookahead uses the low-priority path.
Visible rows reuse their already resolved URL during loading; failed candidates
still trigger fresh lookup and fallback rather than freezing a bad choice.
Full-size readers also start an already queued source immediately without
duplicating it. Normal thumbnail work waits until temporary oversubscription
falls below the usual limit. Connectivity and Windows connection-cost recovery
notifications dispatch to UI and retry only settled empty visible placeholders;
active requests and displayed pixels are retained. Native observers detach at
shutdown.
New limited timeline downloads above 25 MiB are rejected before decoding or
saving. The exact boundary is allowed, and already-cached large sources remain
usable. Rejection marks thumbnail-only lifetime unsuitability rather than
blocking reader/icon access for five days. Shared work becomes unlimited when
a reader joins; a caller arriving just after rejection retries safely. The
independent pre-existing 64-MiB transport safety limit remains.
New prefetch admission also respects Windows Energy Saver. Visible work remains
independent of that prefetch restriction. Windows retains its existing stricter
connection-cost policy for visible downloads rather than silently permitting
metered traffic to match Apple's visible-image policy.
Stored source byte counts, pixel dimensions, last access, ETag and Last-Modified
are persisted. Known unsuitable thumbnails are skipped using those dimensions,
independently of transient download failures or their expiry. Native decoding
still checks dimensions; unreadable thumbnails are excluded for that finder's
lifetime without suppressing otherwise usable feed icons.
Favicons share the same disk source and conditional-update policy, retaining
only their small display representation in memory. Windows repairs missing
display paths through the shared downloader without replacing full-size source
bytes with a small icon. Activity details identify the homepage. Retired favicon
folders, including the former SHA1 cache, are removed once on upgrade; they are
no longer imported or periodically purged. Windows retains native decoder
failure reporting instead of the Apple-specific unreadable-data fallback.
Retired Images/Thumbnails/LargeThumbnails folders are removed once, retaining
atomic preference markers. Shared sources now use the upstream desktop disk
budget instead of periodic folder purges: 300 MiB soft, 1 GiB hard, and
80-percent eviction targets. Three-day recent-use and one-minute save protection
preserve active images. Startup/download-triggered passes reconcile orphan
files/records, and disk hits update access dates at most daily. Source disk
reads/saves and pruning are serialized without serializing network downloads.
Warm icon/favicon and decoded thumbnail hits now update the same daily access
accounting, without decoding or downloading again. Warm icons also schedule due
validation; decoded thumbnails do not. Tracked, canceled/drained metadata touches
share the disk gate without recursively acquiring it. The session throttle is
bounded to 10,000 URLs; clearing it never removes persisted usage or image data.
Retired-folder deletion runs on a worker through tracked startup, retains its
completion markers/error reporting, and drains before shutdown completes.
Soft and hard eviction also protect actual pending reads/downloads and active
conditional revalidations, including work that outlives individual canceled
waiters. Protection ends on completion so old sources can be removed later.
Due icon reads now return cached bytes immediately and revalidate their source
in tracked background work, using ETag/Last-Modified independently of the limited
thumbnail scheduler. Thumbnail reads do not initiate checks. Validator-backed checks use
an eight-day default and clamp server max-age to 3-30 days; sources without
validators wait 30 days. The final selected interval receives plus-or-minus
20-percent jitter to spread server checks. Windows reuses its connectivity/connection-cost policy.
304 responses retain the source and validators; invalid/failed replacements do
not poison a valid cached copy. Background checks cancel and drain on shutdown.
Existing metadata databases gain the validation-date column through a migration.
ETag and Last-Modified requests now bypass both the response cache and URL-only
request coalescing. Their responses never replace the ordinary cached body,
preventing bodyless 304 responses from reaching unrelated image consumers.
The existing Windows binary cache already performs atomic replacement.

Validated replacements now notify consumers after the new source is saved.
Feed-icon and favicon representations are rebuilt, including icons already
evicted from memory, after older pending decodes finish. Refresh work is tracked,
reported on failure, canceled and drained with the service. Each window dispatches
updates to matching timeline/sidebar rows; revisioned references bypass native
image caching only after a replacement. Ready reader-header icons also receive
new source revisions without changing article HTML or navigating the document.
Raw filesystem paths remain unchanged. Notifications preceding initial binding
are remembered so a late-bound row cannot revive cached old pixels. Windows has
no separate author-avatar cache corresponding to the Apple avatar invalidation
and cache-precedence changes.

Transient source failures now use one-minute per-URL and per-host retry delays,
doubling to a one-hour maximum. HTTP 408 and 429 are transient, not five-day
failures. A longer valid server `Retry-After` in seconds takes precedence, even
beyond one hour; unrepresentable intervals are reported and ignored.
Disk hits and unrelated hosts remain usable. Only a real non-transient network
response clears a host pause, and connectivity changes reset both kinds of
delay. Admission rechecks cooldowns after waiting for a thumbnail slot.
Nonempty unreadable HTTP-200 sources are transient, allowing captive-portal
recovery instead of a five-day failure or premature declared-icon fallback.
Unreadable revalidations preserve the good original and validators but reschedule
the next check; empty successes remain permanent failures. Windows requires a
native-decodable source rather than trusting an SVG MIME label alone.

Image metadata now lives in `Images.db` using opt-in SQLite WAL mode; other
database queues retain their existing DELETE default. Checked TRUNCATE checkpoints
replace image VACUUM maintenance and explicitly report busy readers. Migration
imports the original three legacy tables and optional Windows thumbnail/source
records in one transaction, preserves destination overrides and recovers legacy
WAL snapshots. The destination is checkpointed before removing `ImageMetadata.db`
and its sidecars. Unlike upstream's discard-on-error policy, import failures roll
back, retain the legacy database and propagate for retry. Account statistics now
include each account database's WAL/shared-memory sidecars.

The WinUI thumbnail service initializes and decodes on workers, creates native
presentation sources and availability notifications on the UI dispatcher, and
bounds decoded images by URL/mode to 128 MiB, accounting actual bitmap stride.
Memory pressure clears the caches, and shutdown drains work before closing the
metadata database. The native thumbnail control supports whole-image fitting,
rounded clipping, themed placeholders, and recycling to an empty image.
Native decoding now selects the first largest-dimension frame before both
significance checks and decoding, rather than validating a large frame but
decoding a tiny first frame. Unreadable frame headers are reported and skipped;
EXIF orientation remains applied to the selected frame.

Native WIC scaling now supports JPEG quality options for opaque images while
retaining PNG for source formats with an alpha channel. Small single-frame
images keep their original bytes. Header-only largest-frame dimensions avoid a
full managed pixel-buffer allocation. Retired named thumbnail cache folders are
removed on upgrade, and Current Activity recognizes thumbnail URLs.

Timeline navigation now measures the viewport below pinned toolbar and column
headings. Rows above it scroll minimally; rows below it are centered. Adjacent-row
context is revealed only when the selected row did not already need scrolling.
Virtualized rows and template changes are handled after layout, and obsolete
requests are canceled during selection changes and shutdown. Sidebar unread
badges already use native constrained WinUI layout rather than cached row widths.

The desktop status bar now includes subscription commands, count-mode cycling,
the latest successful refresh among active accounts, theme selection, full-link
Copy, shared visibility settings, and a pane-aligned resize grip. Refresh actions
respect active account sessions. Clicking progress opens Current Activity rather
than changing the saved count mode. Local RSS completion dates are now persisted,
including empty/successful refreshes, but not canceled refreshes.
Midnight, clock, locale, time-zone, and resume updates use the UI dispatcher;
timers and native message hooks are released during shutdown.

Preferences and About share an accessible, native copyable version label.
Its context-menu command copies the complete label without requiring selection.

The inventory currently has **152 Ported, 42 Covered, 79 Skipped, and 232 Pending**
source commits, backed by **167 source-attributed Windows port commits**.
The earlier partial primary-image/media rows are now complete. Skips have
specific reasons: independent Apple releases/appcasts/versioning, Xcode/Swift
toolchain adjustments, deletion of unused Apple asset-catalog resources, and
slot-only updates to eight older theme bundles not shipped by Windows,
Swift-only lock substitutions and database-wrapper simplification, and
iOS 18 deployment-target/older-UIKit compatibility cleanup, Apple app-group
preference migration, Swift defaults organization, Safari extension identities,
and UIKit-only contextual-preview behavior. The FreshRSS merge resolution was
reviewed separately: Windows retains the corrected feed/extracted-content paths
without reproducing the intermediate Swift shadowing/build error.
These counts do not imply that the remaining changes have been fully assessed.
Reviewed cache-specific platform adaptations retain the Windows binary-cache
semaphore: atomic replacement alone does not prevent Windows file-sharing errors
while managed reads are open. Foundation's `Data(mappedIfSafe:)` has no direct
zero-copy equivalent in the existing managed `byte[]` decoder API; mapping and
then copying would not deliver its reclaimable-page optimization, so no
ineffective mapping or unrelated unmanaged ownership rewrite was introduced.
The 64-MiB decoded-cache reduction is iOS-only; desktop Windows retains the
upstream desktop 128-MiB policy.

WinUI's native scroll anchoring preserves the top visible article and pixel
offset while preceding large-thumbnail rows shrink and grow; the actual timeline
probe verifies both directions. Search results reuse the same timeline and read
current shared display settings, avoiding the separate AppKit search controller's
stale pre-view-load snapshot.

Still pending are later image-cache rewrites,
finder/cache refinements, appearance
settings, remaining navigation/scroll fixes, and assessment of iPhone/iPad-specific
changes.

Attribution correction: Windows commit `dd3c573` records an erroneous source
trailer, `9b103e929b3580ed0f9fef2de4a9d7303992ebfa`. The correct FreshRSS source is
`9b103e929c2aae6dbb8b2601bd72036d3981cefe`, as recorded in the inventory.
The historical Windows commit has not been rewritten. The implementation also
covers the related renderer cleanup in `7ad27dc8017c4e564814051b3314d1d48a1ba6df`.

## Verification at this checkpoint

The latest reader/header/sidebar/navigation gate passes **159 image tests and
32 of 33 UI groups** after scaled-icon persistence and signed-resource integration.
The remaining assertion is the existing responsive-menu open-during-resize
check ("A still-open menu was replaced during resizing"). The preceding gate
also hit that assertion; an earlier size/power/portal run additionally timed out
in the unrelated webpage text-deletion check. These are not clean full-gate passes.
The last complete passing gate remains **145 image tests and 33 UI groups** after
changed-image presentation and host-retry integration.
The full shared application suite now passes **517 tests**; focused image-parser
coverage remains **46 tests**. Regular and probe x64 builds are warning-free.
Subsequent chosen-URL/preload work brings the image suite to **164 passing tests**.
The focused native thumbnail probe now also passes with article-ID-based failure
targeting, alongside signed-request rejection and scaled-icon persistence.
Conditional-request isolation and response metadata pass **141 web tests**. Existing equivalent
behavior passes 28 binary-cache and 46 image-parser tests.
The focused native thumbnail probe
also verifies 1600x800 offline reader images, automatic document routing,
one-shot remote fallback with publisher scripts disabled, hidden-mode behavior,
portable selection copying without changing the displayed image, UI-captured
feed-relative image bases, settled-placeholder connectivity recovery, and a
real multi-frame TIFF whose first frame is too small. Ordinary
main-world fallback callbacks fail with disabled publisher scripts; the isolated
reader-world implementation passes. The original HTML copier fails the new
native selection assertion. One copy-probe run produced an incomplete result
file; its repeat passed all native groups.
An initial URL-base native run timed out; its diagnostic repeat passed. Later
connectivity and largest-frame probes passed, with warning-free regular/probe
builds. Negative controls also fail for queued-reader admission, excess slot
handoff, old URL bases, the former cache threshold, missing network recovery,
first-frame decoding, active-image eviction, and all seven conditional-request
isolation cases.

Changed-image controls fail without source notifications, representation
observers, the native UI subscription, and favicon-cache replacement. The native
probe verifies replacement dimensions in both the already-visible timeline icon
and already-ready reader header, an unchanged source path, UI-thread delivery,
matching sidebar revision, untouched unrelated rows, and retained document state.
One initial native run timed out; its diagnostic and restored runs passed.
An integrated run passed 32 of 33 groups but timed out opening the unrelated
responsive overflow menu; the subsequent complete gate passed all 33 groups.
Retry controls fail on the original fixed half-hour pause, pre-disk rejection,
permanent 408/429 handling and per-URL-only cooldowns. Coverage includes exact
retry boundaries, exponential capping, connectivity/permanent/success resets,
long and malformed `Retry-After`, other hosts, cached sources during cooldown,
and in-flight successful/permanent responses ending a host pause.

New controls fail for fixed validation dates, absent timeline source-size limits,
unpromoted shared-reader requests, unrestricted prefetch, captive-portal
permanent failures, missing warm-hit access accounting, 512-pixel small decoding,
derived-copy eviction, absent scaled-copy reads/invalidation, unbounded access
tracking, and unsigned reader routes. Warm-hit native checks verify no extra
decode/HTTP, daily source access, and conditional icon validation. The 256-pixel
native probe passes exact geometry, EXIF/multiframe selection, pixel rendering
and LRU accounting. Power-aware prefetch had one timeout followed by a passing
repeat. Derived-copy round-trip/removal, cold scaled-icon reuse, corruption
recovery and changed-source tests pass in the 159-test image suite.

The signed-image native diagnostic captured a correctly loaded 1600-pixel signed
image and completed unsigned/tampered images with zero natural width. Its
JavaScript assertion had an extra call suffix, subsequently corrected. Later
focused runs terminated before writing results in `Microsoft.UI.Xaml.dll`,
exception `0xc000027b`; a synchronous-cleanup control reproduced the crash.
Subsequent corrected full focused probes passed, including unsigned/tampered
rejection without an HTTP request, signed offline originals, portable copying,
fallback and changed visible icons. The article-ID-targeted consumer had an
early termination and a timeout before a passing run of the unchanged binary.
The latest full reader gate completed, with only the menu assertion described
above failing; it has not been rerun after the chosen-URL/preload API work.
Chosen-cache controls fail without snapshot reuse and pre-notification
invalidation. Regressions cover mode isolation/capacity, same-ID article
replacement, HTML-cache reload dependency, saved-only preloading, missing and
negative records, no preload notification, and cancellation followed by retry.

The x64 Windows application builds with zero warnings/errors. The earlier complete parser
suite passed 535 tests. Earlier
related checks covered remote-account synchronization, OPML autosave, attachment
and thumbnail storage/migration, and NewsBlur model decoding.

New regressions were checked against pre-port behavior. Controls include lazy
image sources, tracking names, responsive-image choice, CSS-hidden images,
primary media, short untitled posts, versioned templates, and publisher duplicate
images. Exact restorations of the original shared remote adapter and enclosure
URL resolver proved the new metadata and protocol-relative-image assertions fail
without their corresponding Windows adaptations.

The isolated `--timeline-visibility` WinUI probe passes in standard and column
layouts, including virtualized rows, pinned headers, minimum upward movement,
centered downward movement, following-row context, and no-op movement. Restoring
the exact original selection-scroll path makes this probe fail. Three geometry
regressions also failed against the earlier minimum-scroll implementation.

The standalone `--status-bar` WinUI probe passes count cycling with hidden unread
counts, worker-thread and native clock notifications, full-URL Copy, gear-menu
sidebar toggling, divider alignment at three sidebar widths, Current Activity
actions, theme sharing, and two-window visibility. Original menu, count-action,
clock-hook, and centered-divider controls fail their new assertions. Restoring
the original refresh-session disposal also fails the completion-metadata
notification regression. The original local delegate fails the successful
refresh timestamp assertion, while cancellation preserves the prior date.

The reader/header/sidebar/navigation gate passes 57 image tests and
33 UI groups after the standard timeline integration. Its first run hit the
unchanged open-menu resize timing assertion; the repeat passed all groups.
Three subsequent focused failure-notification cases bring the image suite to
60 passing tests. The selected refresh/progress/lifecycle/worker tests pass all
50 cases. Regular and probe x64 Windows builds have zero warnings/errors.

The isolated version-label probe passes full-label Copy in both Preferences and
About without writing the real clipboard. Restoring each original window fails
the corresponding assertion. Seven plain-text address assertions also fail
after restoring the exact original explicit-link-only implementation; the
restored port passes all 18 targeted plain-text cases.

The image suite passes 60 tests, including thumbnail priority, recent-failure
fallback, metadata restart/batching/expiry, atomic-write rollback, HTML discovery,
and ordered fallback after cold reads. Restoring the exact single-image finder
with record-shape compatibility only fails both multi-candidate regressions.
The Core suite passes 332 tests, including 43 native image cases; restoring the
original PNG encoder path fails the opaque-JPEG assertion while the alpha-channel
case remains PNG. Original cache-cleaner and Current Activity implementations
fail their new thumbnail assertions.

The isolated `--thumbnails` WinUI probe passes worker initialization/decoding,
UI-thread notifications, coalesced discovery/decoding, mode isolation, decoded-LRU
eviction/accounting, visible native pixels, aspect-fill cropping, rounded
clipping, light/dark placeholders, view recycling, offline disk reuse, low-memory
cleanup, and shutdown. Original PNG-only processing and unlimited-memory cache
paths fail four new regressions; removing the network gate fails the offline
case. The original finder fails size-only fallback, and shared failure storage
fails both icon-safety regressions. All temporary controls were restored.

The same probe also verifies actual standard timeline rows, HTML-only
thumbnails, failing-provided-image fallback, UI-thread permanent-failure events,
bounded lookahead, immutable article replacement, rejection of a late previous
image, column exclusion, and scrolling after template changes. Restoring the
exact original row template or model update fails its respective new check.

Large-mode integration additionally passes 32 defaults tests and the 60-image,
33-group reader/header/sidebar/navigation gate. Native probes verify persisted
settings, UI-thread peer notifications, menu state, mode switching, selection,
compact image-less rows and failure refresh that leaves unrelated requests
alone. Exact original size/failure methods fail the corresponding checks.
The refined view's DPI-aware pixel probes verify whole-image fit, custom fill,
portrait top/bottom visibility, top-aligned crop, centered wide crop, resize and
rounded clipping; actual rows verify fixed height and icon/text alignment.
The original visual tree and previous size method fail the new checks. All
temporary controls were removed; restored native probes and regular builds pass
without warnings or errors.

The three-mode port adds five migration/invalid-value regressions. Removing the
legacy adapter fails the previous-large case, and the exact original visibility
predicate fails native hidden-mode propagation. Restored native checks verify
menu/Preferences round trips, None persistence, no request for a newly realized
hidden row, locked-file UI recovery and selected-row visibility. The subsequent
60-image/33-group reader gate and regular build pass.

Feed-icon coverage adds nine policy/invalid-value cases, native Automatic
single-feed suppression, Always/Never behavior, the column exception, shared
Preferences updates, save-failure recovery and icon-aware thumbnail alignment.
The original row icon wiring fails the new Never assertion. The first broad
gate exposed two older favicon-pixel checks that assumed Always; those checks
now select that mode explicitly, and the restored 60-image/33-group gate passes.

The availability regression renders the actual offline placeholder and drains
its requests before another consumer loads the image. Rendering is essential:
deferred WinUI binding phases can otherwise rebind the cell and accidentally
hide a missing availability subscription. After that precondition, restoring
the original missing subscriptions fails the no-scroll recovery assertion.
The restored native suite, 60-image/33-group reader gate, and regular/probe
builds pass. All temporary tracing and negative-control changes were removed.

The shared-source port passes 63 image tests, 134 Web tests and all 495 shared
tests. Coverage includes concurrent icon/thumbnail consumers with independent
cancellation, one disk source with distinct encoded sizes, offline source reuse,
corrupt-source repair, no duplicate HTTP-body storage, and one-time retired-folder
migration without deleting current sources, favicons or browser data.
Restoring the original per-size disk/network path, unconditional HTTP response
storage and old cleanup predicate fails all five selected controls. Restored
native thumbnails and the 63-image/33-group reader gate pass, and regular/probe
builds have zero warnings/errors. The temporary controls were restored.

Direct display-size decoding passes the 63-image/33-group reader gate and native
thumbnail suite. Native assertions cover 512/1200-pixel decode sizes, EXIF-rotated
dimensions and rendered pixels, too-small/narrow rejection, separate icon
usability, offline source reads and decoded-cache eviction. Restoring the original
untransformed decoder, ignoring EXIF, and omitting native dimension rejection each
fail their corresponding new assertion. All controls were restored; regular and
probe builds pass with zero warnings/errors.

Source metadata coverage brings the image suite to 69 passing tests. It checks
stored (not original) dimensions/byte counts, HTTP validators, suitability after
restart and beyond failure expiry, corrected metadata, atomic write failures and
finder-local decode exclusions. Restoring the original source-save, startup-load
and finder paths fails six assertions. Removing native codec-error recovery
fails a deterministic injected WIC error; restored native thumbnails and the
69-image/33-group reader gate pass, with warning-free regular/probe builds.

The disk-budget port passes 77 image tests and five cache-cleaner tests, covering
LRU targets, hard-cap recent-use eviction, save-grace boundaries, actual startup
and post-download deletion, daily access updates, batched deletion rollback and
preservation of shared sources. Six original-path assertions fail without
budget callbacks, transactional deletion and the updated cleaner; a seventh
control detects deletion of a freshly replaced file with an old metadata row.
Restored native thumbnails and regular/probe builds pass. The first broad reader
run timed out in unchanged peer-window sidebar ordering; the repeat passes all
77 image tests and 33 reader/header/sidebar/navigation groups.

Conditional revalidation brings the image suite to 94 passing tests; all 134 Web
tests also pass. Cases cover immediate cached display during a held request,
conditional headers, 304 retention, replacement, transport/HTTP/decoder failures,
thumbnail/network exclusions, shutdown cancellation, schema migration and
freshness bounds. Removing icon-check wiring, date selection and the migration
fails eight assertions. Restored native thumbnails, the 94-image/33-group reader
gate and warning-free regular/probe builds pass.

The broader backend run has one existing failure:
`AccountManagerTests.UnsupportedAccountFilesAreKept` throws
`InvalidDataException` because an existing default-account profile is not local.
The implementation and test files for that failure are unchanged from Windows
baseline `106603d`. Do not interpret that run as a completely passing full suite,
or fix the unrelated account-profile behavior as part of this synchronization.

## What changed upstream

- Desktop navigation fixes: magnet links, timeline scrolling and column sizing,
  sidebar unread-count layout, double-click behavior, and selection restoration.
- A desktop status bar with activity, refresh time, layout/theme commands, and
  visibility preferences.
- Media RSS and JSON Feed attachment metadata, attachment persistence, remote
  service images/enclosures, audio/video/image rendering, and primary-media
  template macros.
- Timeline thumbnails, HTML image discovery, configurable thumbnail/icon sizes,
  asynchronous decoding, shared image caches, conditional requests, retry/backoff,
  per-host limits, disk budgets, icon fallback/revalidation, and cache migration.
- Appearance and timeline preferences: sidebar icons/text size, visible smart
  feeds, feed icons, separators, full titles, read-item fading, byline placement,
  and grayscale icons.
- Reader fixes for MacRumors, FreshRSS footnotes, Rust blog noscript content,
  lazy-loaded iframes, duplicate publisher images, scroll anchors, and footnotes.
- New Plum and Gray article themes and updated default-theme styling.
- Parser/scanner fixes and tests, including Media RSS merging, Gravatar authors,
  raw-text HTML elements, entity decoding, and Atom XHTML CDATA titles.
- iPhone/iPad-specific settings, swipe/rotation/split-view/image-viewer behavior,
  UIKit changes, Apple build tooling, appcasts, and release bookkeeping.

Platform code is assessed by behavior, not just its directory: a desktop feature
implemented in AppKit may still need a Windows equivalent. Apple-specific
toolchain changes do not require translating Swift locks or UIKit APIs into C#.

## Reader theme compatibility

The loader prefers `template7.3.html` when present, otherwise `template.html`.
Theme authors can keep a legacy template for older NetNewsWire releases and put
the new media slots in the versioned template. Windows continues to accept
`Info.json` as an alternative to `Info.plist`; optional Apple Finder metadata is
not used by the Windows reader.

The document base is the extracted-content URL or article link, falling back to
the feed's home page and then its feed URL. Relative URLs resolve against that
base, so a template must not assume `document.baseURI` identifies an article.

| Template slot | Rendered content |
|---|---|
| `[[primary_media]]` | A video, otherwise audio with artwork, otherwise a lead image, normally before the body. |
| `[[enclosures]]` | Remaining audio, video, and images, normally after the body. |

Lead-image selection prefers the article image, then the first image enclosure,
then the thumbnail. Media already embedded in the body is omitted, including
resized copies and stills of an embedded YouTube video. Primary video consumes its
poster rather than displaying that image separately. Untitled posts shorter than
2,048 text elements keep their images after the text but can still have primary
audio or video.

Without a primary-media slot, the article's image can appear among the enclosures,
but a thumbnail-only fallback is not added. Without an enclosure slot, the reader
appends enclosures to a wrapper around the body; selectors that assume direct
children of `.articleBody` may need adjusting. Slot detection searches literal
template text, so do not put slot macros in template comments.

Media markup has no inline styling. Themes can override `nnw-primary-media`,
`nnw-primary-image`, `nnw-primary-image-caption`, `nnw-enclosures`,
`nnw-enclosure-image`, `nnw-enclosure-audio`, `nnw-enclosure-video`, and
`nnw-enclosure-caption`. Feed-supplied dimensions are included only when both are
positive; the lead-image caption is omitted when it repeats the article title.
Generated media URLs remain restricted to HTTP(S) on Windows.

## Continuing the synchronization

Start with `Pending` entries in the inventory, in recorded topological order.
Review each source diff against the Windows implementation before deciding to
port, mark already covered, or skip with an explicit reason. Keep implementation
commits separate and include the full source hash in `NetNewsWire-Commit`.
Only advance a fully incorporated watermark once every predecessor is resolved;
a later individually ported fix is not a new whole-repository baseline.

To inventory a future update from the last fully incorporated watermark:

```powershell
git -C D:\NetNewsWire log --ancestry-path --reverse --topo-order --format='%H %s' BASELINE..HEAD
git -C D:\NetNewsWire diff BASELINE HEAD
```

Use the ancestry-path filter: this target contains a merge of old historical
ancestry. Plain `BASELINE..HEAD` lists 11,565 commits, whereas the descendant
range contains 505 commits (480 on the first-parent mainline). Do not re-port
unrelated historical commits merely because they became reachable in that merge.

The historical audit in `TEST_AUDIT.md` remains pinned to its original baseline;
this file and the inventory track subsequent synchronization separately.
