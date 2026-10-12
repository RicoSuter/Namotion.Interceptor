---
title: Storage
navTitle: Storage
status: Implemented
---

# Storage Design

## Overview

HomeBlaze persists several categories of data through a pluggable storage layer. Live state is NOT persisted — it recovers from the source of truth (devices, peers) on restart. Everything else flows through `IStorageContainer`.

## What Is Stored

| Category | Examples | Format | Persisted? |
|----------|----------|--------|-----------|
| Subject configuration | Subject settings, topology, plugin list | JSON (`[Configuration]` properties) | Yes |
| Knowledge files | Documentation, runbooks, operating procedures | Markdown with YAML frontmatter |  Yes |
| Documents | PDFs, images, data files linked to subjects | Binary blobs | Yes |
| Dynamic metadata | Annotations, tags, links between subjects | JSON (separate from subject config) | Yes |
| Plugin configuration | NuGet feed URLs, package list | JSON | Yes |
| Property values (live state) | Sensor readings, device status | — | No — recovers from field devices (satellites), peers via WebSocket Welcome snapshot (central/standby) |
| Time-series history | Property change history | Via history sink (see [History](history.md)) | Yes (separate concern) |

### Recovery on Restart

Each layer recovers from its source of truth:

| Instance | Recovers from | Mechanism |
|----------|--------------|-----------|
| Satellite | Field devices | Connectors reconnect, read current values |
| Central UNS | Satellites | Satellites reconnect, send Welcome snapshot with full state. Disconnected satellites are not visible — central only shows live-connected data |
| Standby | Primary | Reconnects, receives Welcome snapshot |

## Storage Abstraction [Implemented]

All persistent files flow through `IStorageContainer` (`HomeBlaze.Storage.Abstractions`), which provides blob-level operations:

| Operation | Description |
|-----------|-------------|
| `ReadBlobAsync` | Stream-based blob reading |
| `WriteBlobAsync` | Stream-based blob writing |
| `DeleteBlobAsync` | Deletes the file of a subject. A path without a subject is left alone |
| `GetBlobMetadataAsync` | Query size and modification time |
| `AddSubjectAsync` | Serialize and persist a subject |
| `DeleteSubjectAsync` | Remove a subject |
| `ChildNames` | Snapshot of the names under which the direct children are stored: a JSON subject without its extension, any other file with it, a folder by its name |

### Backends

The default implementation (`FluentStorageContainer`) uses the FluentStorage library, making the backend pluggable:

| Backend | Use Case |
|---------|----------|
| Local filesystem | Single-instance, development, small deployments |
| In-memory | Testing |
| Azure Blob Storage | Cloud deployments, shared storage for HA pairs |
| Amazon S3 | Cloud deployments, shared storage for HA pairs |
| Other FluentStorage providers | Any backend supported by the FluentStorage ecosystem |

The backend is selected via configuration (`StorageType` + `ConnectionString`), not code changes.

### Planned Backend Strategies [Planned]

Beyond the FluentStorage blob providers, two additional backend strategies are worth investigating for production HA and operational workflows:

**PostgreSQL (primary/standby replication):** Store configuration and knowledge files as rows in PostgreSQL, using streaming replication for HA pairs. The primary node writes, standbys read from read replicas. PostgreSQL handles durability, replication lag visibility, and point-in-time recovery. This is the most robust option for deployments that already run PostgreSQL and need strong consistency guarantees for configuration. Requires a custom `IStorageContainer` implementation (not FluentStorage).

**GitOps:** Store configuration and knowledge files in a Git repository. Changes are committed automatically, providing full version history, diff-based auditing, and rollback to any previous state. Enables infrastructure-as-code workflows where configuration changes go through pull requests before being applied. Well-suited for deployments with existing Git infrastructure and teams that prefer declarative, reviewable configuration management. Could be implemented as a backend that commits on write and pulls on startup.

### Following Changes in the Storage

The subject tree follows the storage through reconcile passes. A pass lists the storage once, compares the listing with the index of what earlier passes loaded, and applies the difference. The startup scan is simply the first pass.

**When a pass runs**

- After a file system event, once the storage has been quiet for 1 second, and after 5 seconds at the latest. The event only names the path. What happened to it is decided by the listing. This needs a storage on disk and `enableFileWatching` switched on.
- After an operation of the application that leaves something for a pass, with the same quiet period and on every storage, see "One thing at a time".
- After the file watcher reported an error: at once, or after the quiet period when a file system event arrives before that pass has started. The pass compares the content of every file, because events may be missing. After a buffer overflow the watcher starts over at once. After any other error it is stopped and started again when the next pass begins.
- Every `reconcileIntervalSeconds`, which covers changes that no event reported. It also runs when file watching is off.
- 5 seconds after a pass that failed, or sooner when an event arrives. That happens at most three times in a row. After that the next event or the periodic pass tries again. The first pass is retried the same way, also when the caller of the connect cancelled it.
- At once when the storage answers again after it refused a call of a pass, which is when the last call that was given up has returned. The refused pass did not reach the storage, so it is not one of the three retries, and no timer retries it. An answer that no refused pass waits for starts nothing. A pass whose own call hit the limit is retried like any other pass that failed, so a storage whose calls always outlast the limit gets that pass and its three retries and then waits for the next event or the periodic pass.
- An event or a request that arrives while a pass runs leads to one more pass after it, never to a second pass next to it.

| Setting | Default | Meaning |
|---|---|---|
| `enableFileWatching` | `true` | Whether file system events lead to passes. Only a storage on disk has events. |
| `reconcileIntervalSeconds` | `300` | Seconds between the periodic passes. Zero or a negative value switches them off, and a value longer than a timer accepts (about 49 days) is shortened to that. |

**What a pass does with a path**

| In the storage | In the index | Action |
|---|---|---|
| present | no | The file is loaded and its subject added. A folder becomes a `VirtualFolder`. |
| gone, or a file that became a folder or the reverse | yes | The subject or folder is removed. |
| size or modification time differs, or an event named it | yes | The content is hashed and the subject reloaded only if the hash differs. The size and time that a file subject shows follow the listing either way. |
| same size and time, not named | yes | Nothing. The file is not opened. |

After a lost event every file counts as named, so the content of every file is compared once. A pass that failed or was cancelled hands the paths it was asked to compare to the pass after it. More than 1,000 such paths are handed on as every file.

- **Exact names.** Paths are compared exactly as the storage reports them, on every platform. A rename is the old path gone and the new one present, which includes a rename that only changes the casing.
- **Moved JSON subjects keep their instance.** A configurable JSON subject that is in the tree keeps its instance when its file moves to another folder or its folder is renamed: in the same pass, exactly one JSON subject with that content disappears and a new `.json` file with the same content appears in another folder. The subject is neither stopped nor recreated. A rename within the same folder creates a new instance.
- **Ignored paths.** A path with a segment that starts with a dot (`.DS_Store`, `.idea`, `._*`) or has a temp name (`~` prefix or suffix, `.tmp` suffix, `.tmp.` in the name) never becomes a subject. That includes everything below a folder with such a name. A visible folder that holds only ignored files shows as an empty `VirtualFolder`.
- **JSON files.** What a `.json` file is follows its content. A file that was first read as plain or invalid JSON becomes its subject once its content describes one, a subject whose file names another type is replaced by a subject of that type, and a subject whose file loses its type becomes a plain JSON file. A type that is not known counts as no type. Content that is not valid JSON, such as a file that is still being written, leaves a subject as it is.
- **Plain files.** A generic file and a plain JSON file hold only size and time, which come from the listing without a call per file. A generic file is never opened.
- **Subjects that are not placed.** A subject that the storage created and that does not reach the tree is taken out of the context again, which stops it when it is a hosted service: a subject whose key is taken, a subject whose file is gone or became something else before a pass placed it, and the subjects of a pass that ended early when its connection ends or the storage is stopped before a later pass placed them. The same holds for the subjects embedded in a markdown page that does not reach the tree.
- **Keys.** A subject that is in the tree keeps its key. A file whose key is taken (the subject file `Docs.json` next to a `Docs` folder) is left out with a warning and loaded again in the first pass in which the key is free. The same holds for a folder. Its files are loaded in the pass that places it or in the one after it.
- **Failed loads.** A file that cannot be loaded is retried when it changes or when an event names it. After an IO error, such as a file that its writer still holds, the next pass tries again. A subject that is in the tree and fails half way through a reload can hold part of the rejected content, and is loaded again when its file changes, which includes a file that is put back to what it was.
- **Folders.** The children of a folder are assigned once per pass, and only when they changed. When a subject moved, everything that is added is assigned before anything is removed, so the moved subject is not stopped and restarted. That also holds for a subject that moves into a folder which takes over the key of another subject that moved away.

**One thing at a time**

Passes and the operations that change the storage (create, delete, write, save configuration) run one after another on a single worker. Only the worker changes the tree, so there is no lock. An operation that arrives during a pass waits for it. Reading a file or its metadata does not go through the worker.

After a write, the worker records the new size, time and hash, so the event that the write raises reloads nothing. Where an operation leaves something for a pass, it asks for that pass itself by naming the path as a file system event would, so the pass runs after the quiet period whether or not a file watcher runs: a blob that is written to a path without a subject, a subject that could not be refreshed after its file was written, and a write or a delete that failed or hit the limit after it may have reached the storage. With a file watcher the event names the same path within the quiet period, so both lead to one pass. A path that never becomes a subject asks for none, and neither does an operation on a storage that was stopped. When size and time cannot be read after the write, the write still succeeds, and the next pass finds the file unchanged by its hash. `WriteBlobAsync` first copies the content it is given, writes from that copy and then refreshes the subject of the file. If refreshing the subject after the write fails, the write still succeeds for the caller, the failure is logged, and the next pass reloads the file, with or without an event.

Code that runs on the worker must not call an operation of its own storage that goes through the worker (create, delete, write, save configuration). Such code is the load of a file subject (`OnFileChangedAsync`) and the `ApplyConfigurationAsync` of a subject whose changed file a pass or a write applies. The call would wait for the work that waits for it, so it is refused. A refused call does not throw where it is made: it returns a task that has failed with an `InvalidOperationException`, and nothing is written. A load that awaits the call fails like any other failed load, and the pass goes on with the other files. Code that does not await the call does not learn that its write did not happen. Reading a file or its metadata is allowed, and so is every operation of another storage.

The same holds for a task or a timer that such code starts and does not await, for as long as the pass or operation that started it runs: its calls are refused until then and queued afterwards.

A subject that has to change its storage from such code makes the call in a task that is started inside `ExecutionContext.SuppressFlow()`, and does not wait for that task:

```csharp
using (ExecutionContext.SuppressFlow()) { _ = Task.Run(() => Storage.WriteBlobAsync(path, content, CancellationToken.None)); }
```

Only work that is started inside the scope, such as that task or a timer, runs without the execution flow. A call that is made directly inside the scope still runs with the flow of the pass and is refused. The call of the task is queued like any other and runs once the pass or operation has finished. Waiting for it there would stall the worker for good, because the call can only run after the code that waits for it has returned.

Nobody observes that task unless the code does. A call in it that is refused or fails, for example because the storage hit the time limit, is not connected or has lost its directory, is lost without a trace unless the task catches the failure and logs it. What the task writes must stay readable until the task runs, which is after the pass or operation has finished: a stream that the calling code disposes when it returns is closed by then.

The storage suppresses the flow itself wherever it creates a subject or assigns to the tree. A task or a timer that a subject starts when it is created or attached is therefore not part of the pass. A call that a constructor, a property setter or a lifecycle handler makes directly still is, and is refused.

**An unresponsive storage**

Every call on the storage client is started on the thread pool and limited to 30 seconds. When such a call hits the limit, a pass ends without changing the tree and sets the status of the storage to `Error`, and an operation fails with a `TimeoutException`. A pass that fails for another reason, such as a listing that meets a file that has just been deleted, ends the same way. No file is marked as failed because of it: the next pass does the work again and compares what the failed pass was asked to compare. While a call that hit the limit has not returned, further calls fail at once instead of waiting again, and a pass that fails this way is not retried on a timer. Once the call has returned, the pass that was refused runs at once. This is about the storage's own client: a storage that is a file of another storage and does not answer fails only its own file in the pass of that other storage.

The storage limits its own reading of the content of a file in the same way: hashing a file, reading a JSON file, and the load of a markdown page. There the limit is a failure of that one file: the pass goes on with the other files, the status stays `Connected`, and the file is loaded when it changes or an event names it. A new file is left out until then, and a file that is in the tree keeps the content it has. A file type from outside this library that reads the stream of its file in `OnFileChangedAsync` has no such limit.

**Connecting and stopping**

Connecting creates the timers, starts the file watcher and then runs the first pass. A file watcher that cannot start, for example because the system has no watches left, is logged as a warning, once until it has run again, and does not keep the storage from loading: the storage is followed by the other passes, and every pass tries to start the watcher again. The pass that starts it compares the content of every file.

The directory of a storage on disk is examined before every call on the storage client that names a path: a listing, a read, a write, a delete. A directory that is missing or cannot be reached fails the call. A pass then fails, at its listing or at the file it was about to load when the directory went away, so the tree stays as it is and the status shows `Error` until the directory is back. Nothing is recorded against a file because of it, and the pass is retried like any other pass that failed. A read or an operation in that state (create, delete, write, save configuration) fails with the same error and writes nothing. Without the check the disk client would create the directory again, for a write and already when it is asked about a path in a folder, and the next pass would find a directory that is there and nearly empty. At connect a missing directory fails the connect, and the passes after it load the storage once the directory exists. A directory that exists and is empty passes this check, also when it is the mount point of a volume that is gone.

A connection to the storage owns its worker, its index and its timers. Connecting again ends the old connection, waits until the work it is running has ended, and starts a new connection with every subject rebuilt. Until the new connection is there, reads and operations fail with "Storage not connected". Applying the configuration of the storage therefore connects again only when a setting of the connection changed (storage type, connection string, container name, file watching, reconcile interval) or the status is not `Connected`. That includes `Error` after a pass that failed, so applying the configuration of the storage in that state rebuilds every subject. A connect that arrives while another one is running waits for it to finish, and applying unchanged configuration after such a wait does not connect again.

`StopAsync` stops the file watcher and the timers, so the tree no longer follows the storage, while writes still work. A pass that is running or already queued on the worker still completes. Behind those passes the worker releases the subjects that a pass created and did not place, because no pass places them any more. An operation after the stop does not place them either: their files count as not loaded, and are loaded anew when the storage follows again. The stop does not wait for the release. `StartAsync` connects again. A connect that its caller cancels, which a stop during the first pass does, leaves the status at `Disconnected` and not at `Error`. Unless the storage was stopped, the connection goes on following its storage and the cancelled first pass is retried like one that failed, whether it was cancelled while it ran or before it started, so the pass that finishes sets the status. `Dispose` ends the connection for good: a connect afterwards fails.

An operation that still waits on the worker when its connection ends fails with "Storage not connected", like an operation that arrives afterwards. Saving configuration returns `false` in both cases.

#### Known Limitations and Follow-ups

- **Network shares.** Nothing has been tested on an SMB share or another network file system. The tests run on a local disk.
- **File watcher errors on Linux.** How the storage handles an error of the file watcher is tested with simulated errors only. It has not been verified against the errors that a watcher raises on Linux.
- **Coarse timestamps.** On a file system with timestamps of one or two seconds, an edit that keeps the size and falls into the same tick is only seen when its event arrives. The periodic pass alone misses it.
- **A long pass delays the UI.** An operation waits for the running pass, which matters at startup, when the first pass loads every file.
- **Subject code on the worker.** A subject whose `ApplyConfigurationAsync` hangs blocks the worker. The 30 second limit covers storage calls, not subject code. That includes a file type from outside this library that reads the stream of its file itself: a read that stalls there holds the worker.
- **Renamed subjects are recreated.** A JSON subject that is renamed within its folder gets a new instance, and so does a renamed markdown or other file. Keeping the instance needs a fix in the core library for entries that are re-keyed inside one dictionary.
- **Blocked keys.** A JSON subject whose key is taken is created and released again each time the blocked file changes or an event names it. A subject enters the context when it is created, so a device can be started and stopped once per such change without ever being in the tree. The same holds for the subjects embedded in a markdown page whose key is taken.
- **Embedded subjects of a page that fails to load.** When a block of a markdown page cannot be loaded, the subjects that the same load created from the blocks before it are not released. They stay in the context, and a hosted service among them stays started. The same holds for a subject whose block is removed from a page before that page has reached the tree.
- **Streams outside the worker.** The 30 second limit does not cover a stream that other code reads after `ReadBlobAsync`, for example a download in the UI.
- **Writes that hit the limit.** Such a write keeps running in the background. When it lands, the next pass sees a file that changed and applies it to the subject, also over what was changed in memory since.
- **Writes are not atomic.** A write goes through the storage client directly to the file, not to a temporary file that is then renamed. A write that is interrupted can leave a truncated file.
- **Writes in memory.** `WriteBlobAsync` holds the content in memory while it writes.
- **Empty mount point.** A volume that is unmounted cleanly leaves an empty directory behind, which cannot be told from a storage whose files were all deleted. The pass then removes every subject.
- **Folders on disk.** The disk client creates the folder of a path it is asked about. A folder that is deleted while one of its files is being loaded can come back empty.
- **Backslashes in names.** A backslash in a path is treated as a path separator, also on a platform where a file name can contain one. For a file named `a\b.md` the tree shows a folder `a`, and the disk client creates a folder `a` on disk.
- **Reconnect.** A reconnect waits for a connect that is still running, including its first pass, and cannot end it early.
- **No periodic pass.** With `reconcileIntervalSeconds` at zero, a pass that still fails after its three retries is tried again only when the next event arrives, or when the storage answers again after it refused a pass.
- **File watcher that cannot start.** While the file watcher cannot start, only the periodic pass sees changes that are made outside the application, and nothing does when that is switched off.
- **File watcher that stalls while it starts.** A connect waits for the start of its file watcher. While that start stalls, the first pass and the operations behind it wait as well. A stop or a dispose does not wait.
- **Unreadable folders.** The listing comes from the storage client as a whole. One folder that the process cannot enter fails it and with it every pass, also when the folder is hidden or temporary.
- **Files that vanish during a listing.** A file that is deleted while the storage is listed can fail that pass, which is then retried.
- **Cancelled calls.** A call that was given up because its caller was cancelled also makes the client refuse calls until it returns, so a pass in that moment fails and the status shows `Error` until the call has returned and the pass that then runs has finished.
- **Cancelled operations.** A queued operation whose caller cancels is only dropped when the worker reaches it.
- **A save that wrote nothing.** A writer returns `false` for a subject it does not hold and for a storage that is not connected, and the edit panel shows "Configuration saved." all the same. A save that arrives while its storage connects again is therefore reported as saved although nothing was written.
- **Deleting a subject whose file became a folder.** Deleting a subject deletes what is at the path of its file. When that file was replaced by a folder of the same name and no pass has seen it yet, the folder is deleted with everything in it.
- **Saves over outside edits.** Saving a subject writes its file as the subject holds it. An edit that was made to the file outside the application and that no pass has applied yet is overwritten.
- **Foreign write right after an own write.** After a write, the worker records the hash of what it wrote and then reads the size and time of the file. A write by someone else that lands in between is recorded as the own content, so no pass loads it until the file changes again or an event names it.
- **Links to folders.** A symbolic link to a folder is followed, so the files behind it are loaded as files of the storage. A link to a folder that contains the link is followed many levels deep, which loads the same file many times (16 times in a test on macOS).
- **Moved subjects and a pass that ends early.** A moved JSON subject keeps its instance only when the pass that sees the move also reads the new file. When that pass ends early at that read, for example because the storage does not answer, the next pass creates a new instance and the old one leaves the tree. A create or a delete that runs between the two passes takes the old one out of the tree already.
- **A subject that moves into the folder that takes over its key.** When `Docs.json` is moved into a folder `Docs`, which could not be in the tree while the subject held its key, the folder takes that key. The subject keeps its instance, but it is stopped and started once, because its old place and the folder that holds its new place are the same key of the same folder.
- **A stopped storage that connects again.** A storage that was stopped and connects again, by `ConnectAsync` or by applying changed configuration, does not follow its storage. When its first pass ends early, nothing retries it, and the subjects that the pass created stay in the context, and a hosted service among them stays started, until the storage is stopped again, connects again or is disposed.
- **File watcher after the directory was gone.** Not verified on Linux: after the storage directory was removed and created again, the file watcher may watch nothing without reporting an error, and the storage is then followed by the periodic pass alone. On macOS the events of the new directory arrive.

### File Hierarchy

Files are organized into a `VirtualFolder` tree. Each folder delegates storage operations to its parent `IStorageContainer`. After a pass that completed, a subject is in the index under its path if and only if it is placed in the tree. After a pass that ended early the two differ until the next pass completes: a subject whose file is gone is still shown, and deleting it fails with "Subject not found in storage registry". `AddSubjectAsync` writes the file and places the subject as one step on the worker, and throws without writing when the path is ignored, a file already exists there, or the key is taken.

### File Types

File subjects are created based on file extension via `FileExtensionAttribute`:

| Type | Extension | Description |
|------|-----------|-------------|
| `MarkdownFile` | `.md` | Markdown with YAML frontmatter, embedded subjects (```` ```subject(name) ````), and live expressions (`{{ path }}`) |
| `JsonFile` | `.json` | Plain JSON files (non-configurable subjects) |
| `GenericFile` | Other | Fallback for unknown extensions, metadata only |

Plugin authors can register additional file types via `[FileExtension]`. The storage sets `FileSize` and `LastModified` of such a file subject from its listing and calls its `OnFileChangedAsync` when the file is new or its content hash differs. That call runs on the worker of the storage, see "One thing at a time" for what it must not do. A read of the stream of the file in that call is not limited by the storage.

### Subject Files vs Documents [Implemented]

Files in storage become subjects in the knowledge graph through two different paths:

- **Subject files** — `.json` files with a `$type` discriminator are deserialized into typed subjects (e.g., `Motor`, `OpcUaServer`). The file is the persistence format; the subject is what appears in the graph. Only `[Configuration]` properties are persisted.
- **Documents** — all other files (Markdown, PDFs, images, plain JSON without `$type`) become document subjects (`MarkdownFile`, `JsonFile`, `GenericFile`, or custom types via `[FileExtension]` plugins). The file content is the document itself, visible as-is in the knowledge graph.

Documents are browsable in the subject tree, editable in the Blazor UI (Monaco editor for text files), and accessible via MCP tools (`query` to find them, `invoke_method` to read/write). Linking documents to other subjects (e.g., "this PDF is the manual for motor CNC-01") is handled via dynamic metadata / annotations (planned — see below).

## Subject Configuration [Implemented]

Subjects marked with `[Configuration]` properties have their settings persisted to JSON files via `IConfigurationWriter`. On startup, `RootManager` loads the root configuration and instantiates the subject tree.

The `[State]` attribute marks runtime-only properties that are not persisted.

`IConfigurationWriter` forms a chain resolved via the subject's parent hierarchy — the nearest parent that implements `IConfigurationWriter` handles persistence. This allows different storage containers to own different subtrees.

## Dynamic Metadata and Annotations [Planned]

User-created metadata (annotations, tags, links between subjects) are stored as dynamic attributes on the registry. These are persisted in their own JSON files, separate from subject configuration, so they survive restarts and can be reapplied to subjects as they are instantiated.

This enables operators and integrators to enrich the knowledge graph with domain-specific metadata without modifying subject code. Examples:

- `tags: ["floor-2", "critical", "hvac"]` — cross-cutting grouping independent of folder hierarchy
- `area: "Kitchen"` — physical location assignment (equivalent to Home Assistant areas)
- `group: "cooling-system"` — logical grouping across different folders
- `owner: "maintenance-team-b"` — organizational metadata

All dynamic attributes are queryable through the registry, transmitted over the wire via WebSocket sync, and visible in the UI — no separate subsystem needed.

## HA and Multi-Instance Storage [Planned]

For single-instance deployments, local filesystem storage is sufficient. For HA pairs and multi-instance topologies, both nodes need access to the same persistent files.

**Current state:** each node has its own local filesystem. No shared storage is implemented.

**Planned approach:** use `FluentStorageContainer`'s pluggable backends to point HA pairs at shared storage (Azure Blob, S3, or similar). The storage abstraction already supports this — the missing pieces are:
- Ensuring concurrent read/write safety when two nodes share a backend
- File locking or optimistic concurrency for configuration writes
- Change notification across nodes (filesystem watcher only works locally; shared backends need a different notification mechanism)

## Backup and Disaster Recovery [Planned]

### Why This Architecture Is Resilient

The "recover from source of truth" model means most data does NOT need backup for disaster recovery. Live state (property values, device status, derived properties) is never persisted — it is recovered automatically from external devices and peers on restart. This eliminates the largest and most complex category of data from the backup problem.

### What Needs Backup

| Data | Recoverable without backup? | Risk if lost |
|------|----------------------------|-------------|
| Subject configuration (JSON) | No | Must reconfigure all subjects manually |
| Knowledge files (Markdown, documents) | No | Operational documentation and runbooks lost |
| Dynamic metadata / annotations | No | User-created tags, links, and enrichments lost |
| Plugin configuration | No | Must reconfigure plugin list and feeds |
| Time-series history | **No — this is the critical one** | Historical data is generated locally and cannot be recovered from devices. Once lost, it is gone |
| Live state (property values) | **Yes** — recovers from devices/peers | No backup needed |
| Audit trail (when implemented) | No | Compliance and debugging history lost |

### What Does NOT Need Backup

- **Property values and device state** — recovered from field devices (satellites) or WebSocket Welcome snapshots (central/standby)
- **Derived property values** — recomputed automatically from their dependencies
- **Connector state** — reconnection re-establishes subscriptions and reads current values
- **In-memory change queues** — transient by design; new changes flow immediately after restart

### Backup Approaches by Backend

| Backend | Backup Approach | Restore |
|---------|----------------|---------|
| Local filesystem | File copy, rsync, or filesystem snapshots | Copy files back, restart instance |
| Azure Blob / S3 | Provider-native snapshots and versioning | Restore from snapshot, restart instance |
| PostgreSQL | `pg_dump` or continuous archiving with point-in-time recovery | `pg_restore` to target state, restart instance |
| GitOps | Already versioned — every change is a commit | `git revert` or `git checkout` to any previous state |

Time-series history has its own backup story, depending on the history sink:

| History Sink | Backup Approach |
|-------------|----------------|
| SQLite | File copy (while idle) or SQLite `.backup` command |
| InfluxDB / TimescaleDB | Native backup tools (`influx backup`, `pg_dump`) |
| File-based | File copy |

### Disaster Recovery Procedure (All Nodes Lost)

No special DR mechanism is needed beyond restoring persisted files. The architecture handles the rest:

1. Restore configuration, knowledge files, and plugin config from backup to new nodes
2. Restore history sink data from backup (if available)
3. Start instances — subjects are instantiated from restored configuration
4. Connectors reconnect to field devices and recover current live state automatically
5. Central receives Welcome snapshots from reconnecting satellites
6. System is fully operational; only history between last backup and disaster is lost

### Data Loss Window

The only unrecoverable data loss in a disaster is:
- **Configuration changes** made since the last backup (new subjects, setting changes, metadata)
- **Time-series history** recorded since the last history backup
- **Audit trail entries** since the last backup (when implemented)

Live state has zero data loss — it recovers to the current device state, not the backup-time state.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| No live state persistence | Recover from source of truth | External world (devices, peers) is authoritative. Avoids stale state. Individual subjects may cache last known state locally as an implementation detail (e.g., restore from history or file in `ExecuteAsync` before device reconnects), but the platform does not prescribe or automate this |
| Storage abstraction | `IStorageContainer` with FluentStorage backends | Pluggable without code changes; local filesystem for dev, cloud storage for production |
| File format | JSON for configuration, Markdown for knowledge | Human-readable, diffable, version-controllable |
| Configuration vs state | `[Configuration]` (persisted) vs `[State]` (runtime-only) | Clear developer intent, explicit persistence boundary |
| Configuration writer chain | Resolved via parent hierarchy | Different storage containers can own different subtrees |
| Dynamic metadata storage | Separate JSON files | Decoupled from subject configuration, reapplied on restart |

## Open Questions

- Shared storage concurrency model for HA pairs (locking, optimistic concurrency, conflict resolution)
- Change notification across nodes when using shared cloud backends
- Configuration migration strategy when subject types change across versions (see [Upgrade and Migration](upgrade-and-migration.md))
- Dynamic metadata schema and validation
- Configuration sync between instances (should central know satellite configs?)
- Backup and restore procedures for each storage backend
