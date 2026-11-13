# NetNewsWire synchronization

Source repository: `https://github.com/Ranchero-Software/NetNewsWire`

Original port baseline: `fb086eb7684e9fdb90855c4e033a6beba4325c5e`.

Target of this synchronization: `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a` (2026-11-12). **This is a target,
not a claim that every change through it has been incorporated.**

The commit inventory in [UPSTREAM_COMMITS.csv](UPSTREAM_COMMITS.csv) records
individual source commits, changed paths, disposition, and corresponding Windows
commits. `Pending` means not incorporated; it must not be interpreted as a
platform-specific skip. Source commit IDs are also recorded in the
`NetNewsWire-Commit` trailer of each Windows port commit.

## Incorporation checkpoint

The **fully incorporated watermark remains
`fb086eb7684e9fdb90855c4e033a6beba4325c5e`**. This synchronization is in progress.
The inventory records the individually completed ports; unresolved earlier
desktop, media, image-cache, and appearance changes prevent advancing that watermark.

The latest individually incorporated source commit by source-history order is
`81345183cd1125aca8eaf2d52704ee9b53f9a22b` (Atom XHTML CDATA regression coverage).
It is **not** a whole-repository checkpoint. Other completed ports include magnet
links, MacRumors/FreshRSS/Rust reader fixes, raw-text scanning and entity decoding,
footnote positioning, lazy iframes, Plum/Gray themes, and theme-specific reader-icon
grayscale. Versioned media templates shipped with the themes are not yet selected:
media macros and their supporting models/rendering remain pending.

Attribution correction: Windows commit `dd3c573` records an erroneous source
trailer, `9b103e929b3580ed0f9fef2de4a9d7303992ebfa`. The correct FreshRSS source is
`9b103e929c2aae6dbb8b2601bd72036d3981cefe`, as recorded in the inventory.
The historical Windows commit has not been rewritten. The implementation also
covers the related renderer cleanup in `7ad27dc8017c4e564814051b3314d1d48a1ba6df`.

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
