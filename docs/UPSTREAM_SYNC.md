# NetNewsWire synchronization

Source repository: `https://github.com/Ranchero-Software/NetNewsWire`

Original port baseline: `fb086eb7684e9fdb90855c4e033a6beba4325c5e`.

Target of this synchronization: `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a` (2026-11-12). **This is a target,
not a claim that every change through it has been incorporated.**

The commit inventory in [UPSTREAM_COMMITS.csv](UPSTREAM_COMMITS.csv) records
individual source commits, changed paths, disposition, and corresponding Windows
commits (semicolon-separated when a Windows adaptation needs a follow-up).
`Pending` means not incorporated; it must not be interpreted as a
platform-specific skip. Source commit IDs are also recorded in the
`NetNewsWire-Commit` trailer of each Windows port commit.

## Incorporation checkpoint

The **fully incorporated watermark remains
`fb086eb7684e9fdb90855c4e033a6beba4325c5e`**. This synchronization is in progress.
The inventory records the individually completed ports; unresolved earlier
desktop, media, image-cache, and appearance changes prevent advancing that watermark.

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
enclosures, privacy-enhanced YouTube embeds, artwork fallback, and legacy-theme
enclosure placement. Separate thumbnail URLs now survive parsing, article equality,
database migration, and cold-cache reads. Feedbin, Feedly, NewsBlur, and Reader API
media metadata is incorporated, including NewsBlur's ordered secure-image mapping.

Other ports include feed-title OPML autosave, feed/theme protocol links, and
consistent successful-refresh completion dates. Windows-specific shared adapters
preserve attachment order, dimensions/media types, and thumbnail metadata.

The inventory currently has **45 Ported, 9 Covered, 41 Skipped, and 410 Pending**
source commits, backed by **49 source-attributed Windows commits**. Skips have
specific reasons: independent Apple releases/appcasts/versioning, Xcode/Swift
toolchain adjustments, and deletion of unused Apple asset-catalog resources.
These counts do not imply that the remaining changes have been fully assessed.

Still pending are the desktop status bar and follow-ups, the image-cache/thumbnail
discovery/downloading rewrite, appearance settings, remaining navigation/scroll
fixes, primary-media placement and versioned theme-template selection, publisher
image deduplication, and assessment of iPhone/iPad-specific changes. The bundled
`template7.3.html` files are shipped but are not yet selected by the theme loader.

Attribution correction: Windows commit `dd3c573` records an erroneous source
trailer, `9b103e929b3580ed0f9fef2de4a9d7303992ebfa`. The correct FreshRSS source is
`9b103e929c2aae6dbb8b2601bd72036d3981cefe`, as recorded in the inventory.
The historical Windows commit has not been rewritten. The implementation also
covers the related renderer cleanup in `7ad27dc8017c4e564814051b3314d1d48a1ba6df`.

## Verification at this checkpoint

The x64 Windows application builds with zero warnings/errors. Affected remote
account and OPML paths pass 216 tests; the remote-account compatibility suite
passes 47, article storage passes 100, RSS/Atom parsing passes 93, and NewsBlur
models pass 11. New regressions were checked against pre-port behavior, including
an exact restoration of the original shared remote adapter that loses thumbnails
and media types; the new synchronization assertions fail without those edits.

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
