# Changes since the November 11, 2026 Store package

## Comparison and release status

This document describes the final behavior of the newer WinNewsWire source,
not a claim that a replacement Store package has already been published.

| Reference | Scope |
|---|---|
| Store baseline | **1.2.0.0**, packaged November 11, 2026, from Windows commit `106603d128a34c17907ba10625e754b88cb8ff0a`. The recorded Store-install inspection is in [PORT_STATUS](PORT_STATUS.md#microsoft-store-upload-package). |
| Committed changes reviewed | **355 Windows commits**, after that baseline through `86440cc8b0b9ac67b05ca10ea87be24a9ea9f3e5`. The immutable range changes 259 files. |
| Additional changes included here | The subsequent scrollbar, floating-toolbar, column-footer, failed-page Reload, preferences-layout, website-link, standalone Edge-extension and packaging/documentation changes accompanying these notes. |
| Upstream checkpoint | NetNewsWire `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a`, following original upstream baseline `fb086eb7684e9fdb90855c4e033a6beba4325c5e`. |
| New Store release | **Not assigned or certified.** The source manifest still says `1.2.0.0`; a new upload needs a higher unused version checked against Partner Center. Reusing the distributed version does not deliver these changes as an update. |

The complete Windows commit catalog is
[CHANGES_SINCE_STORE_1.2_COMMITS.csv](CHANGES_SINCE_STORE_1.2_COMMITS.csv).
Source-by-source NetNewsWire dispositions and Windows mappings remain in
[UPSTREAM_COMMITS.csv](UPSTREAM_COMMITS.csv).

Some implementations were refined or replaced during the port. The sections
below describe the resulting behavior; the commit catalog also includes the
intermediate implementations, tests and checkpoint updates.

## 1. Reader, feed parsing and media

- **Richer feed media.** RSS and Atom now parse Media RSS attachments, including
  media types, dimensions, durations, descriptions, artwork and ordering.
  Podcast metadata, iTunes artwork and author/Gravatar metadata are merged
  appropriately. Feed-level artwork is not mistaken for an article enclosure,
  and sibling image renditions prefer the widest suitable image.
- **Existing sync providers retain more media information.** Feedbin images,
  separate thumbnails and podcast enclosures; Feedly visual images; NewsBlur's
  ordered images and secure-image mapping; and Reader API images/enclosures
  now reach the shared article model and renderer. These are adapter
  improvements, not newly added providers or live-service certification.
- **Full images and thumbnails are distinct.** Separate thumbnail URLs survive
  parsing, article comparison, remote conversion, database migration and
  cold-cache reads instead of replacing the original artwork.
- **Attachment storage is more faithful.** Ordered, value-compared attachments
  are stored as article-scoped JSON. Legacy Windows attachment tables migrate
  to that representation, including updates that remove attachments.
- **Audio, video and image enclosures render in the reader.** Primary media,
  captions, filenames and enclosure placement work with modern templates and
  legacy-theme fallbacks. YouTube enclosures use privacy-enhanced players.
- **Fewer duplicate or misplaced lead images.** The renderer suppresses resized
  copies of an image already in the body, avoids stills for embedded YouTube
  videos, normalizes Engadget size prefixes, upgrades Flickr images and keeps
  microblog images in their intended position below text.
- **Better plain-text articles.** Plain text gains paragraphs, line breaks,
  escaping and clickable web/email addresses, including bare domains, `www`
  addresses, Unicode and punycode hosts. Malformed partial addresses and
  unsupported schemes are not turned into links.
- **Parser and site-specific fixes.** HTML raw-text handling, title/textarea
  entity decoding, Atom XHTML-title CDATA, MacRumors line-break cleanup,
  FreshRSS footnote attributes, Rust blog script/noscript handling and
  Optimole lazy iframes are covered.
- **More reliable footnotes.** Popovers stay inside the article column,
  center on their reference where possible, clamp to edges and reposition
  immediately when the layout changes.
- **Desktop link handling.** Magnet links and supported feed/theme protocol
  links are sent to the appropriate desktop handler rather than being
  incorrectly loaded inside the reader's embedded browser.

Primary evidence: the early reader/media commits beginning at `3aa978a`,
the attachment/parser/renderer tests, and
[UPSTREAM_SYNC](UPSTREAM_SYNC.md#incorporation-checkpoint).

## 2. Complete canonical article themes

The source now offers **eleven choices**:

**Default, Appanoose, Biblioteca, Gray, Hyperlegible, NewsFax, Plum,
Promenade, Sepia, Tiqoe Dark and Verdana Revival.**

- The two initially ported themes were followed by the remaining canonical
  bundles, their licenses, embedded fonts and both legacy and 7.3 templates.
- The default theme includes upstream typography, line-length, color, caption,
  icon, monospace-font and link-styling refinements.
- File-based themes use their processed stylesheet, including text-size
  substitutions, Windows font fallbacks and theme-specific grayscale metadata.
- Theme-local resources and fonts use separate HTTPS origins and the required
  font CORS headers. Two themes with identical CSS cannot accidentally share
  the wrong local assets.
- Theme menus, preferences, checkmarks and open readers stay synchronized
  across windows. Editing or deleting the selected theme updates readers and,
  when necessary, returns them to Default.
- Imports inspect an immutable staged copy and show theme information before
  installation. Replacements require confirmation; cancellation, invalid
  replacements and closing the window preserve installed files.
- Built-in bundles cannot be overwritten through the UI. HTML/CSS-only themes,
  XML/binary plist metadata and the existing Windows JSON authoring option
  remain supported.
- Theme-specific designs are retained, including Biblioteca's markup,
  NewsFax's header behavior and the fixed Sepia/Tiqoe Dark palettes.

Completion is recorded in `a7dd3a2` and the
[theme-parity follow-up](PARITY_REVIEW.md#theme-parity-follow-up-2026-11-15).
An unsigned x64 validation package contained the complete theme collection;
that inspection was not a new Store release or an installed upgrade.

## 3. Timeline, thumbnails and appearance

- **Thumbnail choices:** None, Small and Large, synchronized through
  Preferences. New profiles default to None; earlier explicit Windows choices
  are preserved. The final UI keeps thumbnail choices in Preferences rather
  than adding a competing View-menu setting.
- **Small thumbnails:** orientation-aware squares, with centered horizontal
  cropping and top-preserving vertical cropping. Decoding avoids upscaling and
  caps the small image's shorter side at 256 pixels.
- **Large thumbnails:** complete-image fitting in a fixed 180-DIP row image
  height, decoded for display scale with a 1200-pixel longest-side cap.
  Portrait, landscape, rotated and wide images retain appropriate proportions.
- **Cleaner image presentation:** faint themed placeholders only while
  unloaded, rounded fitted images rather than rounded oversized slots, and
  proper empty-state reset when rows are recycled.
- **No column thumbnails:** the column table retains its compact presentation.
- **Feed-icon visibility:** Automatic, Always and Never choices, with a
  contextual explanation for Automatic. Standard rows honor the choice;
  column-layout feed icons retain their intended behavior.
- **Separate Timeline and Appearance preference sections.** Added or completed
  live settings for application light/dark/system appearance, sidebar
  icon visibility and text size, visible smart feeds, timeline icon/text size,
  title/preview line budgets, full titles, read fading, separators and
  byline/date placement. Hiding a smart feed clears stale selections, and
  sidebar text/icon sizes update together.
- **Full-title and preview behavior:** titles and previews share a line budget;
  full-title mode suppresses normal previews, caps titles at twelve lines and
  gives titleless entries a four-line preview. Small thumbnails reserve their
  full height independently of text.
- **Bylines and dates:** can appear above or below article text. Above-title
  placement is the new default without overriding a saved choice, and spans
  the small-thumbnail column where appropriate.
- **Read-state and separators:** read content fades without incorrectly fading
  selection/separators; standard-row separators suppress selected and final
  rows. The explicit Windows thin column-row dividers remain intact.
- **Grayscale feed icons:** sidebar, timeline and reader icons update live,
  using shared content-based variants. Original images and thumbnails keep
  their color; changing the preference does not reload the reading document.
- **Navigation corrections:** double-click opens the row actually clicked,
  rather than another selected article. Reveal and anchoring account for
  pinned headings, column context, recycled rows and image-height changes.

Author-avatar metadata is recognized, but selecting/downloading author avatars
for actual icon display remains a separate gap. One existing selection-centering
probe also remains unresolved; these changes are not a claim that every
navigation or avatar behavior now matches macOS.

## 4. Image discovery, caching and network resilience

This is a substantial replacement/refinement of the image pipeline, not just
new thumbnail controls.

- **Better candidates:** ordered, unique image discovery handles lazy sources,
  responsive `srcset`, CSS dimensions, relative URLs and hidden content while
  rejecting tracking pixels, placeholders, avatars, sharing buttons and banner
  strips. Provided-image failures can fall through to another candidate.
- **Mode-aware selection:** Small prefers the provided thumbnail. Large prefers
  full images/significant attachments, then suitable HTML images, leaving the
  provided thumbnail last to avoid enlarging small square crops.
- **Off-UI preparation:** for enabled thumbnail layouts, missing HTML image
  discovery is completed before fetched rows are published, with cancellation
  and superseded-load checks. The final design has no first-1,000 cutoff or
  offscreen image prefetch.
- **Bounded, shared work:** image sources are shared across icons, reader images
  and thumbnail sizes, with coalesced reads/downloads. Thumbnail downloads use
  four slots and at most two per host; native decoding has its own four-active,
  sixty-waiting queue. Overflow is transient rather than a poisoned URL.
- **Visible-only image loading:** recycled/offscreen rows do not retain a bitmap
  for every article. Waiting work can be reprioritized for currently visible
  rows, and full-size reader requests can start a queued source immediately
  without duplicating it.
- **Memory policies:** decoded thumbnails use a 128-MiB desktop budget. Chosen
  URLs, including negative choices, are bounded to 50,000 entries per mode and
  retain image fields rather than entire articles/HTML.
- **Final discovery storage:** HTML candidates and negative discovery results
  are in memory, not an article-thumbnail database table. The obsolete table
  and superseded discovery/preload APIs were removed. Changing display modes
  retains reusable choices and decoded pixels.
- **Disk policies:** startup pruning limits non-icon original-source files to
  300 MiB using their actual on-disk sizes. Icons are outside that byte budget
  and expire after 180 days without use. Active/recent files are protected;
  downloads may exceed the budget until the next launch.
- **Scaled icon reuse:** size/encoding-specific representations persist to disk.
  A successful atomic scaled write can replace the larger icon original;
  failed writes preserve it. Corrupt or outdated copies recover safely.
- **Correct frame/format handling:** decoding selects the largest image frame
  rather than validating one frame and displaying a tiny first frame.
  Orientation remains respected, resized native representations use PNG, and
  header-only dimension checks avoid a full managed pixel-buffer allocation.
- **Conditional icon refresh:** cached icons remain usable while owned
  background requests revalidate them with ETag/Last-Modified and staggered
  schedules. Unchanged results preserve files and displayed images; refreshed
  validators are retained.
- **HTTP correctness:** conditional requests do not coalesce with ordinary
  body requests or replace their cached body with a bodyless 304 response.
- **Recovery and backoff:** transient URLs use the final 37-minute retry policy;
  HTTP 429/503 and transport failures can pause a host. Longer server
  `Retry-After` waits are honored. Unreadable nonempty HTTP-200 responses are
  treated as recoverable rather than permanently poisoning the image.
- **Network behavior:** cached images remain available offline. Connected
  metered/constrained connections allow thumbnails; background icon
  revalidation remains restricted. Recovery retries appropriate visible
  placeholders without replacing healthy images.
- **Size safety:** new timeline downloads above 25 MiB are rejected without
  preventing full-size reader/icon use of the source. The pre-existing
  independent 64-MiB transport limit remains.
- **Stable icon precedence:** declared feed image, homepage image and favicon
  now share one policy across sidebar/timeline consumers. Failed sources fall
  back promptly; recovered preferred images replace fallbacks without a later
  article refresh or stale asynchronous result undoing the replacement.
- **Homepage-aware mappings:** feeds sharing a homepage share discovered icons
  and absence verdicts. Metadata changes can replace icons live; unavailable
  metadata/captive portals do not erase good mappings. HTTPS homepage
  migrations are supported, while rejected HTTP cross-host redirects do not
  poison stored metadata.
- **Safer cleanup and migration:** image metadata moves to WAL-backed
  `Images.db`, with checked checkpoints, legacy-WAL recovery and transactional
  import. Failed imports or directory enumeration retain metadata for retry.
  Retired image folders are cleaned once, not through recurring legacy purges.
- **Reader-source sharing:** generated image requests use the trusted local
  HTTPS resource origin and process-scoped HMAC authentication. Copy/export
  restores portable original URLs; missing local images fall back without
  enabling publisher scripts. This sharing applies to matching timeline
  sources in standard thumbnail-enabled layouts; None/column layouts retain
  ordinary reader image URLs.
- **Less noise and allocation:** successful thumbnail loads no longer flood
  activity history, failures retain useful details, and HTML metadata/attribute
  scanning avoids redundant parsing, discarded body scanning and common
  temporary allocations.

The detailed final policies and their intermediate superseded versions are
explained in [UPSTREAM_SYNC](UPSTREAM_SYNC.md).

## 5. Window layout, footers and scrolling

- **One pane-aligned footer**, replacing the stacked old/new footer rows.
  Sidebar counts/last-refresh cycling and Current Activity remain, alongside
  original article-list totals, article metadata, hovered links and page URLs.
- **Theme switching stays at the bottom right.** Footer visibility and theme
  preferences remain synchronized across windows, including compact layouts
  and hidden-sidebar refresh progress.
- **The settings gear moves to the top-right menu bar.** Menu overflow reserves
  its space without reparenting canonical menu controls.
- **Edge-flush reading panes:** only the combined pane's top-left corner is
  rounded; outer margins are removed and the sidebar gutter is reduced.
- **Clean lower seam:** square bottom fills, one footer-owned separator and no
  shadow bleeding into the footer. The original subtle left shadow is retained
  in a separate, clipped, non-interactive layer.
- **Native article-list scrolling:** the article list and horizontal column
  wrapper no longer attach custom wheel handling, per-device inertia changes,
  manipulation cancellation or middle-button autoscrolling. Normal WinUI
  scrolling, virtualization and selection/thumbnail observation remain.
- **Sidebar/browser motion:** added wheel/touchpad inertia is off by default,
  while native touchscreen momentum is retained. This preference does not
  override the article list's native behavior; browser changes require restart.
- **Scrollbar placement:** the article-list scrollbar starts below visible
  toolbar and column headings instead of losing its upper end underneath them.
  The sidebar scrollbar moves slightly right without narrowing its resize
  target or changing feed-row/header widths.
- **No blank divider strips:** side-by-side panes meet their single visible
  divider. Its eight-DIP resize target overlays the reader side, away from the
  list scrollbar, and the footer divider follows the same alignment.
- **Column footer:** totals and article/webpage details are adjacent and
  left-aligned with a short vertical divider. Long totals are constrained at
  the text block so they cannot push details or the theme picker out of view.
- **Hidden-toolbar access:** the floating options button moves slightly right,
  fades/slides right away from a distant mouse and returns left on approach.
  Keyboard focus, touch/pen input and an open menu preserve access; reduced
  motion is honored. Native cursor tracking covers WebView2 without page
  scripts or input interception and stops for inactive windows/visible toolbars.

## 6. Preferences, links and failed-page recovery

- **Uniform settings cards:** dropdown and toggle rows share a 64-DIP minimum
  card and a centered 40-DIP control row instead of inconsistent natural
  container heights.
- **Empty messages do not consume space.** In particular, Thumbnail Size and
  Unread Counts no longer have oversized blank lower areas. Real descriptions,
  save errors and larger wrapped labels still expand naturally.
- **Timeline icon corrected:** a consistently sized list FontIcon replaces the
  constrained SymbolIcon in the preferences header.
- **Preferences behavior is retained:** live explanations, disabled-option
  rules, persisted values and cross-window appearance updates continue to work.
- **Copyable versions:** About and Preferences share a native accessible label
  whose context menu copies the complete displayed version.
- **Canonical website links:** Help, About and Privacy Policy use
  `https://www.winnewswire.com`; Preferences Help reuses the shared help URL.
  Feedback email addresses and upstream NetNewsWire links are unchanged.
- **Inline Reload for failed webpages:** the error bar offers a localized
  Reload action using WebView2's native reload behavior, retrying the failed
  page rather than a cached previous page or the selected article's URL.
- **Retry state and history:** another failed attempt remains retryable;
  recovery clears the error and restores loading/presentation correctly.
  Error-page reloads can replace native history IDs, so application history
  replaces the current entry rather than adding a duplicate. Later
  same-document navigation, Back and retained form state remain intact.

## 7. Subscription, account and storage correctness

- Feed display-name changes are saved back to subscription OPML.
- Successful local refresh completion dates persist, including successful
  empty refreshes; canceled refreshes do not advance the success date.
- Sidebar deletion revalidates live identities after confirmation, handles
  moved/removed rows and chooses a surviving row rather than a deleted child.
- Account database statistics include WAL/shared-memory sidecars.
- The contradictory unsupported-account test was fixed without weakening data
  protection: ordinary unsupported profiles remain untouched, and an
  unsupported account in the reserved local-default directory still raises an
  error rather than overwriting that profile.
- Image/attachment schema migrations, source replacement and shutdown paths
  have expanded cancellation, rollback and data-preservation coverage.

These changes do **not** activate automatic deletion of old reading history.
Startup retention remains a separate policy/integration decision.

## 8. Performance fixes and regression prevention

- Removed a UI wait on portable network-monitor initialization. A controlled
  cold-start contention case changed from roughly **606 ms to 22 ms**; this is
  a targeted measurement, not a claim about every startup.
- Reused native title measurement instead of measuring twice and retaining a
  cache of every visited title.
- Shared grayscale variants by file-content identity rather than unrelated
  per-view revision counters.
- Stopped hover/time/status-only changes from relaying out command headers
  when command availability had not changed.
- Preserved virtualization, bounded image work and background disk/decode
  operations as richer article rows were introduced.
- Removed app-added browser coasting in the default input mode. Controlled
  browser-event-to-frame measurements reached about **15.2 ms in the reader**
  and **32.9 ms in the embedded page**, without the extra animation interval.
  These are not physical touchpad or touchscreen certification.
- Improved diagnostics to distinguish queued automation commands from actual
  input-event delivery, composited frames, GC pauses and native dispatcher
  delays. The strict responsiveness gate retains a **100-ms** budget.

Four integration regressions were caught and corrected:

| Regression | Correction |
|---|---|
| Explicit Windows column-row dividers disappeared. | Restored the Windows exception in `e6c59b6`. |
| Grayscale conversions were duplicated across views. | Corrected shared content identity in `c680e67`. |
| Titles were measured twice and retained. | Reused native measurement in `b84b507`. |
| Status-only changes caused unnecessary header layout. | Scoped invalidation in `8d7fab5`. |

**Long UI pauses are not fully resolved.** Both baseline and newer builds
exhibited intermittent WinUI/DirectComposition/DXGI stalls. Richer rows also
allocated more in some paired workloads, so this is not an across-the-board
performance victory. Removing one startup block or extra coasting is not proof
that the reported half-second physical-input pauses are gone. See
[the performance investigation](PARITY_REVIEW.md#test-and-performance-results).

## 9. Standalone Edge extension and release tooling

- Added the separately loadable **`EdgeExtension`** Manifest V3 package, matching
  the Safari-style Subscribe to Feed handoff.
- It forwards the complete HTTP/HTTPS page URL to WinNewsWire or the system's
  default feed reader, preserving query strings, escapes and fragments.
- The original tab stays open. A separate tab initiates the external-protocol
  handoff; non-web tabs are disabled.
- The reader choice is stored locally. No content-script feed scanning, host
  permissions, remote code, analytics or extension-side network requests are
  introduced.
- Installation/help instructions, original RSS glyph assets and
  dependency-free automated checks are included.
- This is **source-loadable**, not a published Edge Add-ons release. Browser
  API success does not certify Windows handler selection or subscription;
  the app still asks the user to confirm the feed.
- Store build documentation now covers x86/x64/ARM64 restore/build, separate
  reader/refresh-worker runtime deployment, manifest inspection and the need
  to use Rebuild when changing package versions.
- The upload recipe rejects the already-used `1.2.0.0` version and derives
  output paths from the chosen manifest version. Publisher identity is
  preserved; no new release number, signing certificate or Store certification
  is implied.

## 10. Verification and documentation improvements

- Completed the **505-entry upstream processing ledger**: 271 Ported,
  99 Covered, 135 Skipped and 0 Pending, with 295 source-attributed Windows
  commits. Apple-only changes have explicit dispositions rather than silently
  becoming Windows requirements.
- Added source-attributed port/checkpoint records and corrected attribution
  notes without rewriting historical commits.
- The November 14 backend run passed **2,462 cases, none skipped**, including
  363 Account cases. The November 15 theme follow-up passed the **541-case
  Shared suite**. These are dated suite results, not totals to add together or
  a percentage of product parity.
- Added focused native coverage for media/images, thumbnail geometry and
  caching, themes/imports/fonts, preference propagation, footers/chrome,
  scrollbar hit targets, floating-toolbar behavior, failed-page retry/history
  and settings-card sizing.
- Regression checks use actual old production source as negative controls and
  verify byte-for-byte restoration, including preservation of other
  uncommitted work when necessary.
- Reports retain input ownership and actual responsiveness results.
  Foreground/presentation checks blocked on the shared desktop are not counted
  as completed passes, and input guards were not bypassed.
- Build and test status, carry-forward gaps and intentional Windows UX choices
  are documented in [TEST_AUDIT](TEST_AUDIT.md),
  [PARITY_REVIEW](PARITY_REVIEW.md) and [UPSTREAM_SYNC](UPSTREAM_SYNC.md).

## What this comparison does not claim

The November 11 baseline already had local reading, OPML/subscription tools,
remote-provider implementations, desktop utilities, Windows activation/share
contracts, a canonical extension under `Windows\BrowserExtension`, an opt-in
named-pipe/PowerShell automation interface and the separate refresh worker.
Their existence must not be presented as entirely new in this release.

Remaining boundaries include:

- **iCloud/CloudKit is an explicitly accepted Windows exclusion.**
- Turnkey release configuration and authorized live-account verification for
  some remote providers remain incomplete.
- Author-avatar display, some keyboard routing, activation of startup
  retention and extraction/Markdown compatibility work remain open.
- Full language, Narrator/high-contrast/IME/RTL/mixed-DPI and installed
  sharing/extension/upgrade coverage is not certified.
- A consented remote crash-submission service has not been delivered.
- Recurring native graphics/input stalls and the pre-existing timeline
  selection-centering failure are not declared fixed.
- Publishing source changes does not publish a new Store package or prove a
  safe installed upgrade.

The authoritative gap descriptions and mitigation boundaries are in
[PARITY_REVIEW](PARITY_REVIEW.md#confirmed-gaps-and-recommended-order).

## Complete source trace

The companion CSV contains every committed change in this immutable range:

```powershell
git log --reverse --oneline 106603d128a34c17907ba10625e754b88cb8ff0a..86440cc8b0b9ac67b05ca10ea87be24a9ea9f3e5
```

The post-`86440cc` follow-ups described above are represented by the
`MainContent` scrollbar/footer/floating-toolbar/browsing changes, preferences
and Help/About links, their native diagnostics, `EdgeExtension`, the package
manifest and updated documentation accompanying this file.

For exact upstream-to-Windows mappings, use `UPSTREAM_COMMITS.csv` rather than
inferring coverage from a commit subject alone. Its corrected mapping for the
FreshRSS port is authoritative; the old `dd3c573` commit trailer contains a
documented source-hash typo.
