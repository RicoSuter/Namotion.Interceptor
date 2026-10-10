# HomeBlaze storage: reconcile instead of per-event handling

Status: design agreed, not implemented. This file is deleted before the pull request is ready; the lasting description goes into `src/HomeBlaze/HomeBlaze/Data/Files/Docs/architecture/design/storage.md`.

## Goal

Simplify and harden how `FluentStorageContainer` follows changes on disk. Today every file system event is coalesced per path and handled on its own, concurrently with other events and with UI operations. That needed special cases for renames, temp files, casing and event order, a lock with re-checks, own-write marks, and two separate code paths for startup and for lost events. Most bugs fixed in #668 and #672 came from those special cases.

The rework replaces all of it with one mechanism: a debounced, sequential pass that compares the tree with a listing of the disk and applies the difference.

## Decisions

| Question | Decision |
|---|---|
| Size of a watched folder | Up to a few thousand files. Every pass walks the whole tree. |
| When is a file reloaded | When its size or modification time differs from the last pass, or when an event named its path and its content hash differs. |
| Tree changes from the UI | Go through the same sequential worker as the passes. |
| Changes the watcher never reports | A periodic pass, configurable, on by default. |
| Path casing | Exact on-disk names everywhere. |
| Shape of the reconcile | Snapshot diff: one listing per pass, compared with an index of what was applied last. |
| Renamed JSON subjects | Keep their instance, matched by content hash within one pass. |

## Out of scope

- Scoping a pass to the directories that changed. Not needed at the assumed size.
- A mode without `FileSystemWatcher`.
- Keeping the instance of a renamed file subject (`MarkdownFile`, `GenericFile`, `JsonFile`). They take their path in the constructor.
- Change notification for shared or cloud backends beyond what the periodic pass gives for free.

## Components

All internal, in `src/HomeBlaze/HomeBlaze.Storage/Internal`.

| Class | Purpose | Depends on |
|---|---|---|
| `StorageFileWatcher` (shrinks) | Wraps `FileSystemWatcher`. Reports the path of every event that is not on an ignored path, reports errors, restarts itself after an error. | nothing else |
| `ReconcileTrigger` (new) | Collects named paths and decides when a pass runs. | `TimeProvider` |
| `StorageWorker` (new) | A queue that runs one item at a time, plus the inline rule. | nothing else |
| `StorageIndex` (new) | What was applied last: one entry per exact path, and subject to path. | nothing else |
| `StorageReconciler` (new) | One pass: listing, comparison, loading, batch apply. | blob listing, `FileSubjectFactory`, `StorageIndex` |
| `FluentStorageContainer` | Wires these together. Its public methods hand work to the worker. | all of the above |

Removed: `FileEventCoalescer`, `StoragePathRegistry`, `StorageHierarchyManager` (its child key rule moves into the reconciler), and in the container the per-path handlers (`SyncPathAsync`, `SyncDirectoryAsync`, `AddFileAsync`, `RemoveIfMissing`, `EnsureFolder`, `RemoveMissingChildren`), `ScanAsync`, `ResyncAsync`, the hierarchy lock and the own-write marks. `StorageFileWatcher.CoalesceEvents` goes away. `JsonSubjectSynchronizer` shrinks to "apply this JSON text to this subject".

Unchanged: `FileSubjectFactory`, `StoragePathFilter`, `VirtualFolder`, the file subject types, and the public surface of `FluentStorageContainer` apart from one new setting.

## Triggers

A pass is requested by:

- A watcher event for a path that is not ignored. The path is added to the set of named paths.
- A watcher error. The watcher restarts and the next pass treats every file as named.
- The periodic timer.
- Startup, which is the first pass against an empty index.

`ReconcileTrigger` requests a pass after 1 second without a new trigger, or 5 seconds after the first pending trigger, whichever comes first. The maximum keeps a folder with a steady writer from starving the pass. Triggers that arrive while a pass runs lead to exactly one more pass after it. The set of named paths is handed to the pass and cleared at that moment.

Events for ignored paths (a hidden or temp-named segment, see `StoragePathFilter`) are dropped before they reach the trigger, so a busy hidden folder such as `.git` causes no passes. A rename counts when its old or its new path is not ignored, and names both.

## The index

One entry per path, keyed by the exact relative path as the listing returns it, compared ordinally.

- Kind: file or folder.
- Size and modification time as last applied.
- The subject, if one is placed in the tree.
- The content hash, for subjects that hold content: configurable JSON subjects, `MarkdownFile`, `JsonFile`. Not for `GenericFile`, which holds only size and time.
- A state: placed, key taken, or failed to load.

It also maps a subject to its path, for `WriteConfigurationAsync` and `DeleteSubjectAsync`. Only the worker reads or writes the index, so it is a plain dictionary without locking.

## A pass

1. List the folder once, recursively: path, kind, size, modification time. Drop ignored paths.
2. Compare each path with the index and decide per the table below.
3. Load subjects for new files and refresh changed ones.
4. Apply as a batch: for each folder whose set of children changed, build its `Children` dictionary once and assign it once. New folders are assigned bottom up, so a folder is attached complete.

| Listing | Index | Action |
|---|---|---|
| present | absent | Load the subject and add it. A folder gets a `VirtualFolder`. |
| absent | present | Remove the subject or folder. |
| present, size or time differs | present | Refresh the subject. |
| present, same size and time, named | present | Hash the file. Refresh only if the hash differs. |
| present, same size and time, not named | present | Nothing. No file is opened. |
| kind differs | present | Remove, then add. |

Rules that follow from this:

- **Named but unchanged.** The hash decides. A `GenericFile` has no hash and nothing to reload, so it is left alone.
- **Renames and casing.** A rename is the old path gone and the new path present. With exact names that includes a rename that only changes the casing, on every platform.
- **Renamed JSON subjects keep their instance.** A new `.json` file is read once. If its hash equals the hash of exactly one configurable JSON subject whose path is gone in the same pass, that instance is moved to the new path instead of creating a new one. With more than one candidate, or with none, the file is deserialized from the text that was already read. This also removes the second read of JSON files that exists today. Property history follows a move by object identity, so a renamed device keeps its history.
- **Refreshing a JSON subject** stays as it is: the configuration is applied only when the hash changed.
- **Key clashes.** A file whose key in its folder is taken (`Docs.json` next to a `Docs` folder) is recorded as "key taken" with its size and time. It is not loaded again while both stay the same, and it is retried in the pass where the key is free.
- **Failed loads.** A file that fails to load is recorded as failed and retried when its size or time changes or an event names it. The periodic pass alone does not retry it.
- **After a watcher error** every file is named, so the pass hashes all content files once.
- **Ignored paths** count as absent. A subject whose path becomes ignored through a rename is removed.

A file that is being written while the pass reads it can load with partial content or fail. The write raises further events, so a later pass corrects it.

## The worker

An unbounded queue of work items, processed one at a time by a single loop. A UI operation returns a task that completes when its item has run, and an exception inside the item reaches the caller.

| Operation | What the item does |
|---|---|
| Reconcile pass | As described above. |
| `AddSubjectAsync` | Validate the name (not ignored, no file at the path, key free), write the file, place the subject, record size, time and hash. |
| `DeleteBlobAsync`, `DeleteSubjectAsync` | Delete the file, remove the subject and the index entry. |
| `WriteBlobAsync` | Write the file, refresh the subject, record size, time and hash. |
| `WriteConfigurationAsync` | Serialize, write the file, record size, time and hash. |

`ReadBlobAsync` and `GetBlobMetadataAsync` only read from the storage and do not go through the queue.

**Own writes.** Because a write records the new size, time and hash, the event it raises names a path whose hash matches, and nothing is reloaded. This replaces the own-write marks and their grace period.

**Inline rule.** Code that runs on the worker can call the storage again: a markdown page that saves its embedded subjects calls `WriteBlobAsync`, and a subject's `ApplyConfigurationAsync` may call `AddSubjectAsync`. Queued normally, that call would wait for the item that waits for it. A call made from within a running item therefore runs inline. The worker marks its own execution flow to recognise this.

**Waiting.** A UI operation that arrives during a pass waits for the pass. The first pass loads everything, so an operation during startup waits until the tree is complete.

**Startup.** The watcher starts before the first pass, so a change during startup requests a second pass. `ConnectAsync` returns when the first pass is done.

**Reconnect.** `ConnectAsync` on a connected container disposes the watcher, trigger, worker and index and starts fresh, which rebuilds every subject, as today.

**Errors.** When a pass fails as a whole, for example because the listing throws, it is logged, the tree is left as it is and `Status` becomes `Error`. The next trigger or timer tick runs another pass, and a successful pass sets `Status` back to `Connected`.

**Shutdown.** Disposing stops the trigger and the watcher, lets the running item finish and cancels the items still queued.

## Setting

One new configuration property on `FluentStorageContainer`: `ReconcileIntervalSeconds`, default 300. 0 switches the periodic pass off. `EnableFileWatching` stays and only controls the watcher. With watching off and an interval set, the container still follows the disk on the timer.

## Verified facts the design relies on

Measured with a throwaway test on macOS, FluentStorage as referenced by the repository:

- The disk listing returns the exact on-disk name, the size, and the modification time with full precision for files, and returns folders including empty ones.
- The in-memory listing returns size and modification time for files, and a folder entry without a time. Folders are only compared by presence, so that is enough.

Not verified: the cost of a pass. See the measurements below.

## Tests

- **Reconciler.** Arrange a disk state, run one pass, assert the tree. One test per row of the table, plus: a renamed JSON subject keeps its instance, two candidates for a rename create a new instance, a key clash is retried when the key is free, a failed load is retried on change and not on an idle pass, casing-only rename, two files that differ only in casing, ignored paths. Most tests in `FluentStorageContainerFileEventTests` convert directly, because they already assert the tree for a disk state.
- **Trigger.** With `ManualTimeProvider`: quiet period, maximum wait, periodic tick, exactly one follow-up pass for triggers during a pass, named paths handed over and cleared.
- **Worker.** Items run in order and one at a time, an exception reaches the caller and does not stop the loop, the inline rule, shutdown cancels queued items, and a UI operation waits behind a running pass (with `GatedFile`).
- **Own writes.** After each write operation, a pass that names the written path reloads nothing.
- **Real watcher.** Three end-to-end tests stay: a file saved through a temp file and a rename, a registered file deleted, a new directory with a file.

Measured once and reported in the pull request, not kept as tests:

- An idle pass over 5,000 files.
- Adding 4,000 files to one folder in one pass. It was 270 MB of allocations when added one event at a time.

## Rollout

One pull request on a branch from master once #672 is merged, in commits that can be reviewed one by one:

1. `StorageIndex` and `StorageReconciler`, used for startup and for the pass after a watcher error.
2. `StorageWorker` for the UI operations. The hierarchy lock is removed.
3. `ReconcileTrigger` and the periodic setting. The watcher shrinks, `FileEventCoalescer`, `CoalesceEvents` and the own-write marks are removed.
4. Exact on-disk names.
5. `storage.md` rewritten for the new design, its limitations list pruned.

Splitting this into several pull requests would leave both mechanisms active side by side in between.

After the merge: deploy to the server whose data folder is edited over an SMB share and repeat the four scenarios from the original report. No test here covers Samba.

## What this closes from the limitations in storage.md

| Limitation | After the rework |
|---|---|
| Casing on macOS | Closed, through exact names from the listing. |
| Casing on case-sensitive file systems | Closed. |
| Concurrent JSON refresh | Closed, passes are sequential. |
| Key clash not retried | Closed. |
| Disk checks under the lock | Closed, there is no lock. A UI operation can instead wait for a pass. |
| Adding many files is quadratic | Closed, `Children` is assigned once per folder and pass. |
| Samba | Open until checked on the server. |

## Risks

- **A UI operation waits for a pass.** Bounded by the listing plus the files that changed. Long only at startup.
- **Coarse timestamps.** On a file system with timestamps of one or two seconds, an edit that keeps the size and lands in the same tick is only seen when its event arrives. The named paths cover that case; the periodic pass alone would miss it.
- **Reentrancy.** A deadlock if the inline rule misses a path back into the storage. Covered by tests for the two known callers and by the rule being general.
- **Latency.** A change shows up after about one second instead of half a second.
