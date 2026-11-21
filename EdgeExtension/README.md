# Subscribe to Feed for Microsoft Edge

A Manifest V3 counterpart to NetNewsWire's Safari **Subscribe to Feed** extension.

## Install

1. Install the packaged Windows app (MSIX or Microsoft Store). Running an unpackaged
   executable alone does not register Windows protocol handlers.
2. In Edge, open `edge://extensions` and enable **Developer mode**.
3. Choose **Load unpacked** and select this `EdgeExtension` folder.
4. Pin **Subscribe to Feed in WinNewsWire** from Edge's Extensions menu.

No build, npm install, native messaging host, or changes to WinNewsWire are needed.
Only load extensions from sources you trust. This folder is an unpacked extension,
not a published Microsoft Edge Add-ons listing.

## Functionality

On an HTTP or HTTPS tab, click **Subscribe to Feed**. The extension sends the complete
page address as `x-winnewswire-feed:https://example.com/page` to Windows. Edge may ask
permission to open WinNewsWire. The existing app activation flow brings WinNewsWire
forward and opens its Add Feed dialog with that address. Choose the account/folder
and confirm there; the app discovers and subscribes to the feed. A website homepage
works as well as a direct feed URL, provided the app can discover a feed.

The original tab is not navigated or closed. A new tab initiates the external-app
handoff; Edge may leave that tab blank. The button is disabled for non-web addresses,
such as `edge:`, `file:`, and extension pages. It does not indicate whether a site
actually has a feed.

Right-click the extension and choose **Extension options** to select:

- **WinNewsWire** (default): use the app-specific `x-winnewswire-feed:` handler.
- **The system's default feed reader**: use `feed:` instead, honoring Windows'
  configured reader. Select the app for the **FEED** link type in Windows Default apps.

This mirrors Safari's reader preference. Edge stores its own setting locally; it
does not share NetNewsWire's macOS app-group defaults or change Windows defaults.
Subscription management, parsing, account selection, duplicate detection, and errors
finding feeds remain the desktop reader's responsibility.

## How it works

- `manifest.json`: toolbar action, module service worker, and settings page.
- `background.js`: declarative HTTP/HTTPS enablement and click-to-protocol handoff.
- `feed-url.js`: URL validation and protocol wrapping without losing query strings,
  percent escapes, or fragments.
- `options.html` / `options.js`: persisted reader choice, usage help, and API errors.

Like the inspected Safari source, the extension does not inject scripts, scan the
DOM, find feeds itself, or subscribe in the background. `activeTab` grants access to
the current URL when clicked; `declarativeContent` enables the button on web pages
without reading browsing history; `storage` persists the reader preference. No host
permissions, remote code, analytics, or network requests are used. Only the URL chosen
by the user is handed to the reader; that URL can contain private query parameters.

An API failure opens the settings/help page with an error. A successful browser API
call is **not** proof that a desktop app opened: extensions cannot observe Windows'
handler selection, a canceled Edge prompt, or successful subscription. Install a
protocol-registered reader, approve Edge's prompt, and check its subscription dialog.
Enterprise policy may block external protocols. Pages requiring browser cookies may
not be discoverable because the desktop reader fetches them independently.

## Development

Run the dependency-free automated checks from the repository root:

```powershell
node --test .\EdgeExtension\tests\extension.test.mjs
```

Icons are included. To regenerate the original RSS glyph PNGs:

```powershell
node .\EdgeExtension\tools\generate-icons.mjs
```

After edits, click **Reload** in `edge://extensions`.

For an end-to-end check, open a site with a discoverable feed and click the button.
Approve the external-app prompt and check the prefilled Add Feed dialog. Cancel it
without subscribing if desired. Repeat with a direct feed and with query/fragment
URLs; verify the website stays open. Save the default-reader option and check the
Windows-selected reader receives the next request. Verify the button is disabled on
Edge settings and local files. Cancel an external-app prompt and confirm no
subscription is added.
