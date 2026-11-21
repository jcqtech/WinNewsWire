# Local Windows automation

For an object-oriented COM interface usable directly from PowerShell 5.1/7,
VBScript, JScript and VBA, see [COM Automation](COM.md). COM uses the same
permission and service described below; it does not bypass the opt-in.

WinNewsWire exposes a local, current-user named-pipe API as the Windows
counterpart of the pinned NetNewsWire scripting dictionary. It is **disabled by
default**. Enable **Allow local automation** in Preferences to permit other
programs running as your Windows user to read feed data and change subscriptions
or read/starred state. No network listener, arbitrary code evaluation, credential
export or automatic account creation is provided.

The pipe name includes a hash of the Windows user identity and data directory.
The Windows server grants access to the current user SID and sets
`PIPE_REJECT_REMOTE_CLIENTS`; it does not accept SMB/remote pipe clients. The
client also checks the pipe owner with `PipeOptions.CurrentUserOnly`. Run the
client as the same Windows user and elevation context as the application.
Requests are UTF-8 JSON with a
four-byte little-endian byte-length prefix, limited to 1 MiB; responses are
limited to 64 MiB. Each connection carries one request and one response.

Use PowerShell 7 or later with the bundled client:

```powershell
.\Invoke-WinNewsWire.ps1 -Request '{"command":"get","property":"accounts"}'
.\Invoke-WinNewsWire.ps1 -Request '{"command":"get","target":{"kind":"account","accountID":"OnMyMac"},"property":"feeds"}'
```

## Object references

References contain `kind` and the applicable parent IDs. Object kinds are
`application`, `account`, `folder`, `feed`, `article`, and `author`.

| Object | Required identifiers |
|---|---|
| Application | No IDs; omit `target` for this object |
| Account | `accountID` |
| Folder | `accountID`, numeric `folderID` |
| Feed | `accountID`, `feedID`; optional `folderID` identifies its membership |
| Article | `accountID`, `feedID`, `articleID`; optional `folderID` |
| Author | `accountID`, `feedID`, `authorID`; optional `articleID` and `folderID` |

As in native scripting, an article's `articleID` is its **feed-supplied UniqueID**,
not WinNewsWire's database article ID. Ambiguous article IDs are rejected.
Container scope is checked; a feed reference cannot claim a folder that does not
contain it.

## Commands

- `get`: retrieve `property`, or scalar `properties` when omitted.
- `count`: count an object collection identified by `property`.
- `exists`: return a Boolean for a valid object reference.
- `make`: create `newKind: "feed"` with `url` and optional `name`, or
  `newKind: "folder"` with `name`, at an application/account/folder target.
  Nested folders are not supported. An existing feed URL returns that feed.
- `delete`: remove a feed from the indicated root/folder membership or remove a
  folder. Account/article/author deletion is not supported.
- `set`: set account `active`, or article `read`/`starred`, using Boolean `value`.
  Other properties are read-only, as declared by the native dictionary.
- `openLocation`: enqueue an existing feed/theme/application URL route using
  `url`. Like native Open Location, it has no return value; UI confirmation and
  errors use the existing application flow.

Read properties retain native distinctions: application `feeds` means top-level
feeds in active accounts; account `allFeeds` includes folder membership. Feed and
folder `articles` are sorted by logical published date. Application
`currentArticle`, `selectedArticles` and `selectedFeeds` reflect the active main
window; folders and smart feeds are excluded from `selectedFeeds`.

Article properties include `id`, `title`, `url`, `permalink`, `externalUrl`,
`contents`, `html`, `summary`, `publishedDate`, `modifiedDate`, `arrivedDate`,
`read`, `starred`, `deleted`, `imageUrl`, `feed`, and `authors`. Optional native
text properties return empty text where the native accessor does; missing
article URLs/dates remain null.

Responses contain `success`, `hasValue`, `value`, and `error`, plus the optional
request `id`. Errors are explicit `invalidRequest`, `notFound`, or
`operationFailed` results. Mutation completion means the account operation has
finished, not merely been scheduled. `openLocation` is the explicit exception.
The PowerShell client produces no output for a successful no-value command; use
`-RawResponse` to inspect its envelope.

This protocol is not an AppleEvent binary implementation. Native scripting
inputs/assertions are translated to these object/command operations. Installed
application, current-user isolation and full UI selection/lifecycle verification
still require an approved Windows runtime.
