# OneDrive synchronization

## Result and scope

OneDrive is a new account provider, not an OPML backup and not a synchronized
SQLite database. It synchronizes the subscription graph, user-edited feed
names, publisher titles/homepage URLs, folder identities/names, root and
multiple-folder membership,
read/unread and starred/unstarred state, and retained article content.
Publishers are still fetched directly by each PC using the existing local
feed downloader, including its out-of-process Windows refresh worker.

The upstream behavioral inventory is in [ICLOUD_SYNC_PARITY.md](ICLOUD_SYNC_PARITY.md).
This provider replaces CloudKit's transport; it cannot join a NetNewsWire
iCloud account or exchange data with unmodified NetNewsWire.

## Setup and everyday use

1. Sign in to the OneDrive Windows client on each PC.
2. In WinNewsWire's Accounts preferences, add **OneDrive**.
3. Keep the suggested `OneDrive\Apps\WinNewsWire` folder, or choose another
   dedicated folder inside a registered OneDrive root. On every PC choose the
   local path to the **same cloud folder**, not a copy of the folder.
4. In Explorer, use **Always keep on this device** on that folder. This is
   recommended for reliable offline access; online placeholders are hydrated
   by normal file reads.
5. Account creation performs an initial exchange and publisher refresh with
   cancellation. Cancellation keeps the account and its durable pending work.

The standard subscribe, folder, rename, copy, move, remove, undo, OPML
import/export, mark-read and star controls work unchanged. A feed can appear
at the root and in several folders, including empty folders. Existing local
subscriptions can be copied into the OneDrive account using normal
cross-account copy or imported as OPML.

Subscription edits are committed locally and immediately handed to the local
OneDrive folder when available. Read/star changes use the existing persistent
status queue. Every ordinary refresh exchanges data before and after direct
publisher downloads; the existing status timer also exchanges data every two
minutes. OneDrive accounts opt out of the timer's thirty-minute idle backoff
because there is no CloudKit push subscription. The OneDrive client controls
actual upload/download latency; WinNewsWire does not claim that a successful
local exchange proves cloud delivery.

**OneDrive Settings** in Accounts allows reconnecting a moved folder, choosing
whether to synchronize unread article bodies, scanning visible sync data, and
cleaning up unneeded synced bodies. Starred bodies always synchronize.
Unread-content sync defaults **off** for a new library. An explicit existing
local value is preserved; otherwise a joining PC adopts the library's shared
preference. Changing this setting does not itself initiate publisher refresh
or immediate backfill. Other preferences and account display names stay local.
Removing the account removes only that PC's account data; it does not
unsubscribe other PCs or delete the
cloud folder. Deactivating an account uses the existing account activation
policy.

## Why use the Windows sync client?

This is the closest equivalent to CloudKit's system-account experience without
requiring a new OAuth application registration, embedded client secret, token
entry, or a separately hosted sync server. Windows OneDrive handles sign-in,
MFA, account selection, enterprise policies, offline transfer, quota messages,
and upload retries. Personal and work/school roots are discovered from the
OneDrive environment variables and registered Windows user accounts; the
folder picker lets the user choose the intended root/library.

This is deliberately **not Microsoft Graph** and does not request Graph
permissions. `Apps\WinNewsWire` is an ordinary dedicated OneDrive directory,
not the permission-scoped Graph app-folder endpoint. A signed-in, enabled
OneDrive desktop client is a prerequisite. Signing out, pausing OneDrive,
running out of quota, selective-sync exclusion, or organizational restrictions
can delay delivery even when local writes succeed. The UI directs the user
to OneDrive's tray status for those conditions.

## Data model and convergence

`OneDriveReplica.json` lives in the local account directory, outside OneDrive.
It contains a random installation/account replica ID, a Lamport clock,
the merged register map, the last applied subscription structure, and content
application checkpoints. It is the durable outbox and recovery state.
Do not copy this local file to another PC: create a new account on that PC so
it gets a unique writer identity.

The shared folder contains `devices-v1\<replica>.<partition>.json` files:

| Partition | Contents |
| --- | --- |
| `subscriptions` | Folder records, feed titles/edited names/homepages, independent memberships, shared unread-content preference |
| `statuses` | Separate read, starred and deletion registers for each article |
| `content-0` through `content-f` | Sixteen stable hash partitions of retained article bodies |

Each device writes only its own files. It publishes its merged knowledge, so
another device can recover even if some original device files are unavailable.
Files use versioned envelopes and atomic temporary-file replacement; unchanged
partitions are not rewritten. SQLite databases and stored service-credential
files are not synchronized. Private feed URLs and article text can themselves
contain sensitive data and are part of the synchronized content.

Every register has `(logical clock, replica ID, value)`. Merging takes the
greatest `(clock, replica ID)` in ordinal order, independently for each key.
Receiving a revision advances the local clock before subsequent local edits.
This produces idempotent, order-independent convergence without using wall
clocks. Concurrent edits of the same field choose a deterministic winner, not
necessarily the latest physical timestamp. Independent fields and independent
feed memberships do not overwrite each other.

Deletion is a new null-valued revision, not absence from a snapshot. These
markers are retained indefinitely: an offline or restored old snapshot must
not resurrect subscriptions or bodies. Moving between folders updates
independent membership registers. Folder identity is a GUID, so a rename does
not destroy membership. A folder deletion suppresses memberships referencing
it; restoring the folder/membership emits newer revisions.

Article IDs retain the existing feed-scoped WinNewsWire identity. Status-only
records can arrive before article bodies and create database statuses for
later publisher downloads. Incoming statuses never overwrite a local status
still pending in the queue, including selected/in-flight entries. Local
pending fields are reasserted after receiving remote revisions and before
publication. A queue entry is acknowledged only after the durable local
replica and the device's OneDrive snapshots have been written successfully.
A newer UI change replaces the selected queue row and is not accidentally
acknowledged by an older send. This is a local OneDrive handoff, not proof of
server upload. Interrupted or failed publication is retried.

Publisher-retention deletion is a distinct synchronized status operation,
not the same thing as retiring a cloud body. Incoming deletion removes the
local article; orphan local statuses follow local retention. Any pending
star-kind change (including unstar) suppresses deletion; pending read alone
does not. The refresh worker carries deletions over IPC. The publisher
transaction also stores a deletion journal in the articles database, so exit
after database commit but before the IPC notification cannot silently lose
the synchronized deletion.

Bodies include HTML/text/Markdown, title, source identity and URLs, summary,
image/thumbnail URLs, dates, authors, and attachment metadata. They do not
include downloaded image/media binaries. Device-local arrival dates are not
part of the synchronized payload, and author ordering is canonical, avoiding
endless content revisions between devices that downloaded the same story at
different times. Content application checkpoints avoid rewriting unchanged
bodies; a missing local body can still be recovered from cloud content.

## Failure, cleanup, and privacy

All incoming envelopes, keys, values, identities, URLs, and body payloads are
validated before remote subscription changes are applied. Unknown schema
versions and corrupt records fail visibly through the activity/error path and
OneDrive account status; they are not treated as an empty account.

When the OneDrive directory is unavailable, local subscription edits and
status revisions remain durable. A full refresh still downloads publisher
feeds, then reports the synchronization failure. Reconnecting and refreshing
resumes exchange. Shutdown and removal use the existing serialized account
lifecycle; no untracked synchronization worker or file watcher remains alive.

Read/unstarred bodies no longer required by local policy are retired from
sync while local article history remains intact. Explicit cleanup also
retires unneeded/orphan bodies from merged sync data. It preserves current
starred content (including orphaned starred content) and, when enabled,
current subscribed unread content. It does **not**
discard deletion markers or prune another offline device's files.

The local feed-based retention policy removes unstarred articles absent from
a nonempty publisher update after thirty days of local arrival, even if they
are unread; these removals synchronize. Starred history is protected. Local
startup cleanup uses the existing thirty-day orphan-status policy and
unsubscribed-feed cleanup. Received cloud content is never interpreted as a
complete publisher feed. New old items retain the existing 183-day stale-item
default-read rule. **Remote status expiry is not enabled**, matching the
upstream exposed cleanup paths.

Automatic body maintenance initializes its timestamp on the first ordinary
exchange. Once it is more than six days old, the timestamp advances before
the attempt, and at most 400 eligible bodies are retired. Manual cleanup
revalidates eligibility, processes all eligible bodies with 400-record
progress updates, and preserves local history.

The scan UI separates status/content totals into exclusive starred,
unstarred-unread and unstarred-read groups, folds orphaned unstarred content
into the read group, and shows snapshot-reading/merging/publication progress.
Incomplete or canceled scans cannot authorize cleanup. A separate confirmation
checkbox warns about scope and duration before the cleanup action. Results
older than five minutes are treated as stale; cleanup always rescans the
currently visible snapshots instead of trusting old counts. Cleanup reports
read/orphaned and unread deletion totals, preserves old scan results as stale,
supports cancellation, and warns about possible partial success after failure.
**Copy Report** supports sharing scan or cleanup results via the user's chosen
Windows application.

OneDrive file version history, recycle-bin copies, and snapshots from a PC
that has not yet received cleanup can still contain older body data.
Cleanup is logical synchronization cleanup, not guaranteed secure erasure.
The files contain reading history, feed URLs, and article text in JSON.
They inherit OneDrive's access control and encryption; no additional
application-level end-to-end encryption is claimed. Do not share the folder
with people who should not see the account.

## Deliberate platform differences

The source-derived details and evidence are in
[ICLOUD_SYNC_PARITY.md](ICLOUD_SYNC_PARITY.md). This checklist maps its
principal user-visible behaviors to the Windows implementation:

| Behavior | OneDrive implementation |
| --- | --- |
| Subscription graph and metadata | Feed URL, publisher/edited name, homepage, stable folders, root/multiple memberships, empty folders; no icon synchronization |
| Everyday subscription operations | Existing subscribe/rename/copy/move/remove/undo and OPML controls; offline-first rather than cloud-first mutations |
| Article states | Bidirectional read/unread and star/unstar, status-before-body receipt, durable pending/in-flight protection, synchronized publisher deletions |
| Archived content | Starred bodies always, unread bodies optionally; article fields/authors/attachment metadata, not media binaries |
| Unread-content preference | New-library default off, explicit local preference preserved, shared propagation, no immediate publisher refresh on toggle |
| Refresh and availability | Initial receive then direct publisher refresh, exchange afterward, publisher refresh continues after sync failure; polling replaces push |
| Retention and maintenance | Thirty-day feed retention, protected starred history, separate body cleanup, six-day/400-body automatic pass, no remote status expiry |
| Storage management UX | Scan categories, progress, cancellation, confirmation, stale-plan revalidation, cleanup counts, copyable report and inline help |
| Account lifecycle and recovery | Normal activation/removal, local-only account removal, reconnect, durable replay; no destructive token/zone reset |

| CloudKit mechanism | Windows equivalent / difference |
| --- | --- |
| Apple ID and iCloud availability | Windows OneDrive sign-in and client status |
| Private CloudKit record zones | Dedicated user-owned OneDrive directory |
| Server change tokens and push | Mergeable partition snapshots, immediate local subscription handoff, two-minute polling |
| CloudKit conflict/retry machinery | Deterministic field merge, atomic writes, durable outbox, OneDrive transfer retries |
| CloudKit server deletion | Persistent deletion revision; older OneDrive versions may remain |
| CloudKit storage scan/cleanup | Merged sync statistics and safe body cleanup in account settings |

No background Windows service runs after WinNewsWire exits. OneDrive can finish
transferring published snapshots while WinNewsWire is closed; the next launch
or refresh consumes downloaded changes. Windows and Apple push delivery,
exact cloud-retention implementation, and storage-report presentation are not
claimed to be identical.

Unlike the native cloud-first mutation paths, this provider deliberately
accepts durable local edits while OneDrive is unavailable instead of rolling
back folder/rename operations. Missing folders are not interpreted as account
deletion and never trigger an automatic destructive reset. Reconnect restores
exchange from retained local data. Windows transfer failures and retries are
owned by OneDrive; local file/format errors retain pending work and are
reported for retry. Lamport/device ordering is an intentional transport design,
not an assertion about CloudKit's conflict algorithm. Receiving a subscription
does not itself fetch its publisher; an ordinary full refresh includes it.

## Verification

`Modules\Account\Tests\Account.Tests\OneDriveSyncTests.cs` exercises multi-replica
round trips, copies/moves/deletes/undo, independent offline changes, deterministic
concurrent renames, delayed separate transport directories, conflict copies,
both status directions, status-before-content delivery, restart recovery,
outbox-write failures, unavailable-OneDrive publisher refresh, content policy,
body cleanup, malformed/future documents, quiet-poll idempotence, provider
restoration, shared preference precedence, pending-star deletion protection,
transactional deletion-journal recovery, bounded weekly cleanup, deletion
notifications, and different local arrival dates.

`LocalAccount\LocalRefreshProcessTests.cs` also exercises actual out-of-process
publisher retention/deletion propagation while protecting starred history.

These are real filesystem/SQLite integration tests with isolated temporary
profiles, not a live Microsoft-account test. No production OneDrive folder or
user profile is modified by the tests. A real two-PC/cloud round trip still
requires the user's OneDrive accounts and cannot be inferred from local tests.
