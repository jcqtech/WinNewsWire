# Subscribe with WinNewsWire

This Manifest V3 extension provides the pinned NetNewsWire Safari extension's
subscription handoff for Microsoft Edge and Google Chrome on Windows. It reads
only the active tab URL after the user opens the toolbar popup. WinNewsWire's
FeedFinder performs discovery; no page HTML is scanned or injected.

Open the popup and choose **Open in WinNewsWire**. Windows/browser confirmation
may appear. The app then asks for the destination account/folder and optional
feed name; the extension does not create subscriptions itself.

The default handoff is `x-winnewswire-feed:https://example.com/page`. Selecting
the system's default feed reader uses `feed:https://example.com/page`. This
preference is stored locally in the browser; it is not synchronized with the
application preferences or another browser.

## Installation

The extension is not published or automatically installed. To try the source
on an approved machine, open `edge://extensions` or `chrome://extensions`,
enable Developer mode, choose **Load unpacked**, and select this directory.
WinNewsWire must be installed with its protocol registrations before the
dedicated handoff can work. Browsers may still request confirmation or block
an external-protocol launch; the popup does not claim that the app opened
successfully or that a feed was added.

The popup requires a second explicit click to hand off the URL. This is a
Windows-browser permission/gesture equivalent, not Safari's one-click native
extension UI. Restricted/internal pages display an error and cannot launch a
reader. Permissions are limited to `activeTab` and `storage`; there are no host
permissions, content scripts, background workers or external network requests.

## Tests

From a source checkout, run `node --test` in this directory (Node.js 20 or later).
Tests and `package.json` are not included in the application package.
These are standalone
extension-component tests, not replacements for blocked .NET assemblies or
proof of installed browser/Windows protocol behavior.
