# COM Automation

WinNewsWire exposes NetNewsWire's scripting object model through a local,
out-of-process COM Automation server. The ProgID is **WinNewsWire.Application**;
the CLSID is `{4B97D46F-41B4-4689-9466-04B87EC7A2B0}`.

Enable **Allow local automation** in WinNewsWire Preferences first. This grants
programs running as your Windows user access to subscription/article data and
the mutations listed below. The setting remains disabled by default.

## Installation and activation

MSIX installation registers the server, ProgID, interfaces and type library.
Do not run the manual registration command for an MSIX installation.

For an unpackaged build, keep the generated `Automation` directory beside
`WinNewsWire.exe` and register once as your normal Windows user:

```powershell
& '.\Automation\WinNewsWire.ComServer.exe' --register
if ($LASTEXITCODE) { throw 'COM registration failed; see WinNewsWire\Logs\ComServer.log in LocalAppData.' }
```

Registration is per-user and does not require administrator privileges. Moving
the unpackaged application requires registering its new path. Unregister before
removing it:

```powershell
& '.\Automation\WinNewsWire.ComServer.exe' --unregister
```

Unregistering an old path does not remove a newer installation's registration.
Registration never enables local automation. COM starts the server on demand;
its first data request tries the running application, then attempts to start the
adjacent `WinNewsWire.exe` once if the endpoint is unavailable. A missing/disabled
endpoint produces an explicit error, not empty collections. After startup,
retry the script if loading a large profile exceeded the connection deadline.

The server uses an STA message pump and dual `IDispatch` interfaces, not an in-process COM DLL or
`regsvr32`. It has its own dependency directory and follows the app's architecture
and self-contained deployment policy. Both 32-bit and 64-bit scripting clients
can call it. COM execution is restricted to the current user/local machine; the
underlying pipe additionally rejects remote clients. Do not configure DCOM
remote access or run scripts elevated to bypass an access failure.

## PowerShell

Windows PowerShell 5.1 and PowerShell 7 on Windows:

```powershell
$nnw = New-Object -ComObject WinNewsWire.Application
$nnw.Name
$nnw.Version
foreach ($account in $nnw.Accounts) {
    foreach ($feed in $account.AllFeeds) {
        '{0}: {1}' -f $feed.Name, $feed.Url
    }
}

$account = $nnw.Accounts.Item(1)
$folder = $account.CreateFolder('Scripting')
$feed = $folder.CreateFeed('https://example.com/feed.xml', 'Example')
foreach ($article in $feed.Articles.Where('Read', $false)) {
    $article.Starred = $true
}
$account.OpmlRepresentation
```

Creation validates/downloads feeds using the existing account provider. It can
fail for network, authentication or invalid-feed reasons. Mutations return
after the account operation completes. Creating an already subscribed URL
returns the existing feed; it does not duplicate or move it.

Generic `Make` matches the native new/at/with-data/with-properties parameters:

```powershell
$properties = New-Object -ComObject Scripting.Dictionary
$properties.Add('name', 'Reading')
$folder = $nnw.Make('folder', $account, [Type]::Missing, $properties)
$feed = $nnw.Make('feed', $folder, 'https://example.com/feed.xml')
$nnw.Count($folder.Feeds)
$nnw.Exists($feed)
$nnw.Delete($feed)
$folder.Delete()
$nnw.OpenLocation('feed:https://example.com/feed.xml')
```

`Make` accepts `feed`, its `webFeed` synonym, or `folder`. Omit the location to
use the default account. `withData` is a feed URL; `withProperties` is a
`Scripting.Dictionary` containing `name` and/or `url` (a .NET dictionary also
works for managed callers). Folder creation without properties uses an empty
initial name, as upstream does. Nested folders, account creation and
account/article/author deletion are not supported by the native scripting API.
There is deliberately no `Quit` command in this interface.

`OpenLocation` uses the existing application URL routes and their UI
confirmation/error flow. As upstream, it returns no result; it is not an
arbitrary command runner or a synchronous subscription operation.

## VBScript, JScript and VBA

VBScript (requires the optional Windows VBScript engine):

```vb
Set nnw = CreateObject("WinNewsWire.Application")
For Each account In nnw.Accounts
    For Each feed In account.Feeds
        WScript.Echo feed.Name & ": " & feed.Url
    Next
Next
Set article = nnw.CurrentArticle
If Not article Is Nothing Then article.Starred = True
```

Windows Script Host JScript:

```js
var nnw = new ActiveXObject("WinNewsWire.Application");
for (var e = new Enumerator(nnw.SelectedArticles); !e.atEnd(); e.moveNext()) {
    e.item().Read = true;
}
```

VBA supports the same late-bound `CreateObject` calls and `For Each` loops.
The installed `Automation\WinNewsWire.Automation.tlb` is the versioned interface
reference for object browsers and typed clients. Its library ID is
`{163CB58F-AB4C-43E3-9458-89744B418B14}`, version **1.0**. Interfaces have fixed
IIDs and DISPIDs; only `IApplication` is activated by CLSID. Child objects and
collections come from the application, not independent activation. Add a VBA
reference to the type library to declare `WinNewsWire_Automation.IApplication`.
Dates are Automation `DATE` values (local wall-clock time), not JSON strings.
Absent optional article dates/object references are empty values; test
`IsEmpty`/`Is Nothing` as appropriate to the scripting host.

[`Samples\ReadingList.bas`](Samples/ReadingList.bas) is an importable VBA module
that exports a feed to an Excel workbook using bounded pages, batched titles,
URLs and native dates. `ExportReadingList(progID, feedUrl, outputPath)` returns
the number of saved article rows and propagates failures.

## Object model and native parity

The reference is `Mac\Resources\NetNewsWire.sdef` and `Mac\Scripting` at upstream
commit `7908c801bf7c97fa7cca9ef78cf51b33fb853e6a`. Every property and collection
declared there is exposed; spaces in AppleScript names become PascalCase.

| Object/interface | Read-only properties and collections | Writable properties |
|---|---|---|
| Application / `IApplication` | Name, Version, CurrentArticle, SelectedArticles, SelectedFeeds, Accounts, Articles, Feeds, WebFeeds | None |
| Account / `IAccount` | Name, ID, AccountType, AllFeeds, AllWebFeeds, OpmlRepresentation, Feeds, WebFeeds, Folders | Active |
| Folder / `IFolder` | Name, ID, OpmlRepresentation, Feeds, WebFeeds, Articles | None |
| Feed / `IFeed` | Name, ID, Url, HomePageUrl, IconUrl, FaviconUrl, OpmlRepresentation, Authors, Articles | None |
| Article / `IArticle` | ID, Title, Url, ExternalUrl, Permalink, Contents, Html, Summary, PublishedDate, ArrivedDate, ModifiedDate, Deleted, ImageUrl, Feed, Authors | Read, Starred |
| Author / `IAuthor` | Name, ID, Url, AvatarUrl, EmailAddress, Email | None |

`DatePublished`, `DateArrived` and `DateModified` additionally alias the upstream
Cocoa accessor names. Child objects have an `Exists` property; feeds and folders
have `Delete()`. Accounts expose `CreateFolder(name)` and
`CreateFeed(url, name="")`; folders expose `CreateFeed(url, name="")`.
The application exposes the same creation conveniences for its default account.

AccountType retains native four-character enum codes:

| Native account type | Code |
|---|---|
| onmymac (local; shown as On My PC on Windows) | Locl |
| cloudkit | Clkt |
| feedly | Fdly |
| feedbin | Fdbn |
| newsblur | NBlr |
| fresh rss | Frsh |
| inoreader | Inrd |
| bazqux | Bzqx |
| the old reader | Tord |

The enum is represented as text for unambiguous use from Automation hosts.
Listing Clkt does not enable CloudKit on Windows; that remains an accepted
platform exclusion.

Important native semantics are preserved:

- Application `Articles` means selected articles, not the entire database.
  Selection/current article come from the active main window. SelectedFeeds
  excludes folders and smart feeds.
- Application `Feeds` includes only top-level feeds in active accounts.
  Account `Feeds` is top-level; `AllFeeds` includes folder contents even in an
  inactive account. Feed/folder articles are newest-first by logical date.
- Article ID is its feed-supplied UniqueID, not the database article ID.
  Account/feed/optional-folder scope remains part of its reference. Folder IDs
  are integers; account/feed/article/author IDs are strings.
- Feed `Name` is the publication's name, not an edited sidebar label.
  Article Url prefers the permalink over an external URL. Optional text retains
  native empty-text/null distinctions. Deleted is always false, as upstream.
- Read/Starred changes go through the account provider and its persistence/sync
  paths; they do not edit the database behind the running application's back.
  Provider restrictions still apply.

### Collections and object specifiers

All collections implement `IObjectCollection`:

| Member | Meaning |
|---|---|
| `Count` | Current member count |
| `Item(1)` | One-based ordinal lookup; `Item("name")` looks up a name |
| `ByID(id)` | ID lookup within this collection; numeric IDs only for folders |
| `ByName(name)` | Exact name lookup, returning the first matching member |
| `Exists(indexOrName)` | False for a missing member; operational errors still fail |
| `Get("Title")` | SAFEARRAY of a scalar property from each object |
| `GetPage(1, 128)` | Snapshot of up to 128 members, starting at one-based ordinal 1 |
| `Where("Read", False)` | Snapshot of members with an equal scalar property |
| `Delete()` | Delete every feed/folder in an evaluated membership snapshot |
| `_NewEnum` (DISPID -4) | Standard IEnumVARIANT for `For Each`, `foreach`, Enumerator |

The default member is Item (DISPID 0). `.ByID` disambiguates a string ID from
a name and a numeric folder ID from an ordinal. Collections reevaluate live
membership when accessed, while enumeration and `Where` freeze membership so
mutations during a loop do not change its traversal. Object properties remain
live. A missing/deleted object fails lookup/property access; Exists can check it.
Bulk operations are sequential, not transactional; an explicit error stops
the operation without undoing already completed account mutations.

`GetPage(start, count)` requires `start >= 1` and `1 <= count <= 4096`; a start
past the end returns an empty collection. Page membership is frozen, but its
objects' properties remain live. Separate page calls are not a transaction:
concurrent insertions/deletions can shift offsets and cause skips or repeats.

### Large datasets

Article counts and membership queries no longer hydrate article bodies.
Individual article reads use a feed-scoped `(feedID, uniqueID)` SQL index;
the index is deliberately nonunique so ambiguous publisher IDs still fail.
`Get` and `Where` fetch live properties in batches of at most 128 references.
Use pages for exports instead of retaining every article or HTML body:

```powershell
$articles = $feed.Articles
$count = $articles.Count
for ($start = 1; $start -le $count; $start += 128) {
    $page = $articles.GetPage($start, 128)
    $page.Get('Title')
}
```

Measured through a real out-of-process COM fixture, with roughly 4 KiB HTML
per article (milliseconds; local development machine, not a performance SLA):

| Operation | 1,000 before | 1,000 after | 5,000 before | 5,000 after |
|---|---:|---:|---:|---:|
| Count | 134.64 | 35.94 | 205.81 | 11.40 |
| 200 live title reads | 2,460.35 | 225.18 | 6,453.48 | 323.92 |
| Project all titles | 10,332.12 | 87.20 | 175,372.86 | 380.38 |
| Filter unread | 9,788.77 | 85.23 | 252,272.87 | 303.36 |

Remaining bounds matter:

- Paging bounds returned references/IPC/body memory, **not SQL cursor work**.
  Each page still loads and sorts all metadata identities in its scope.
- Full `Get` returns an O(N) SAFEARRAY; enumeration/`Where` retain O(N)
  membership snapshots. Paging is preferable for large exports.
- Batches are bounded by object count, not bytes. A huge body or batch can
  still exceed the 64 MiB pipe-response limit; use smaller pages for HTML.
  Requests are limited to 1 MiB and operations to five minutes.
- Writes remain sequential provider operations, without bulk setters or
  cross-page snapshot isolation. Network services impose their own limits.

`Tests\WinNewsWire.ComServer.Tests\Clients\Measure-LargeDataset.ps1` provides
the repeatable measurement client; the fixture supports `--articles N`.

### Known parity qualifications

Account `Feeds.ByName`/`ByID` currently searches top-level membership, whereas
upstream's account name/ID accessors also search folder contents. Use
`account.AllFeeds` for those lookups. Article URL fallback currently distinguishes
null from an empty permalink; upstream also falls back for an empty permalink.
`Where` is equality-only and `Exists` accepts objects/collections, not arbitrary
AppleScript specifiers or predicates.

AppleScript's parser, AppleEvents wire protocol, arbitrary `whose` predicates,
insertion-position syntax and Script Editor are not Windows components.
Translate their intent to these object operations or the scripting host's own
filtering/iteration. NetNewsWire's unordered subscription sets do not promise
positional insertion either. Existing AppleScript source is not executed by COM.

## Implementation and verification

`Modules\Automation` contains the public contracts. A build-only, pinned dSPACE
COM exporter generates their type library; it does not ship as a runtime
dependency or register anything during builds. `Tools\ComServer` contains the
class factory, STA host and live wrappers. `Shared\Scripting\ScriptingService`
remains the single implementation of account/article operations. A small
`Modules\ScriptingProtocol` assembly shares the existing wire records without
loading account databases into the COM process.

Run on Windows:

```powershell
dotnet test .\Tests\WinNewsWire.ComServer.Tests\WinNewsWire.ComServer.Tests.csproj
dotnet test .\Tests\WinNewsWire.Tests\WinNewsWire.Tests.csproj --filter FullyQualifiedName~ScriptingServiceTests
```

The COM tests create unique temporary ProgIDs/CLSIDs and fixture profiles, then
activate a real child server through COM from PowerShell 5.1/7, VBScript and
JScript, including x86 clients, and a strongly typed .NET client. They do not replace the real ProgID, enable
automation, install a package or alter your subscriptions. Object-model,
registration-ownership and error-path cases run separately. Installed-package
activation, actual UI selection changes, live-provider mutations and
cross-user/remote access-denial scenarios require separate deployment/runtime
checks; passing fixture clients alone does not certify those outcomes.

Verification completed with 20 COM tests and 11 scripting-service
tests passing. The server builds for x86, x64 and ARM64. The x64 Release MSIX
also builds; its actual payload contains the server, isolated self-contained
runtime, type library and all seven interface registrations, without test or
build-only exporter artifacts. The package was not installed over a live profile.

Negative controls were run by temporarily reversing the enumeration property
and VARIANT-input fixes. Real PowerShell then failed its IEnumVARIANT assertion,
and both x86/x64 VBScript hosts failed Delete respectively. Both implementations
were restored before the passing runs; these checks are not mocked successes.
The indexed scalar lookup and batched projection were also temporarily reversed:
both new scaling regression tests failed, then passed after restoration.

### Actual packaged UI and Office scenarios

A separate developer-registered package, disposable profile and real local HTTP
RSS server exercised the production COM CLSID/ProgID and Windows App Runtime
deployment policy. Diagnostic-only commands drove real windows and selections;
they were compiled with `ParityProbe=true`, not shipped in production.
The user's existing installation/profile remained untouched.

Verified with the actual packaged application:

- Cold COM activation launched the package-identified UI in about 6.17 seconds,
  without a client retry, and opened the isolated automation endpoint.
- Creating a folder/feed downloaded 320 realistic articles through the real
  local account provider; read/starred writes reached the visible timeline.
- Opening/activating a second reader window changed COM current/selected
  articles. Actual VBA starred that selection and the live UI reflected it.
- The provider refreshed updated content and imported article 321 while
  preserving flags. The diagnostic host aged only the localhost sample feed's
  timestamp to make it due: normal refresh honors a 29-minute minimum interval.
- A deliberate HTTP 503 subscription failed explicitly. A real BBC RSS
  subscription imported 29 articles (publication counts naturally vary).
- A full app/server restart retained 321 articles, the subscription/folder and
  starred status; subsequent scripted folder deletion succeeded.

Actual 64-bit Microsoft Access 16.0 VBA imported the generated type library,
used early-bound interfaces and `For Each`, verified `VarType(date) = vbDate`,
and changed a live selected article. The late-bound reading-list module paged
all 321 articles into a real `.xlsx` using Excel Automation. A separate Excel
instance reopened the saved workbook and verified all 321 article rows.
No macro-security setting was changed. Excel/Word did not expose their VBE in
this environment, so **Excel-hosted VBA itself is not certified**; Access-hosted
VBA plus actual Excel workbook integration is.

**Installation limitation:** normal `.msix` installation was attempted, not
silently skipped. Unsigned executable activation required elevation
(`0x80073D2B`); a signed test package failed certificate trust (`0x800B0109`).
The non-administrator session could not establish the necessary trust.
Developer registration (`Add-AppxPackage -Register`) succeeded and supplied
the packaged activation evidence above, but is **not proof of a normal
signed-MSIX install/update or Store delivery**. The isolated test package omitted
unrelated notification/protocol/share handlers to avoid taking over the user's
existing integrations; those paths were not certified by these scenarios.
Authenticated Feedly/Feedbin/etc. sync and cross-user/remote denial also remain
unverified; the live provider tested here was the local RSS account.

The scenario clients are in
`Tests\WinNewsWire.ComServer.Tests\Clients`: `Run-ComUiScenarios.ps1`,
`Run-OfficeScenarios.ps1`, `TypedOfficeClient.bas` and `SampleFeedServer.py`.
They require an explicitly prepared isolated diagnostic deployment and, for
Office scenarios, an installed VBA-capable Access host and Excel.
