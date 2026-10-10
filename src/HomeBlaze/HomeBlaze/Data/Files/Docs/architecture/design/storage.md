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
| `DeleteBlobAsync` | Blob deletion |
| `GetBlobMetadataAsync` | Query size and modification time |
| `AddSubjectAsync` | Serialize and persist a subject |
| `DeleteSubjectAsync` | Remove a subject |

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

The subject tree follows the storage through reconcile passes. A pass lists the storage once, compares the listing with what was applied in the last pass, and applies the difference. The startup scan is simply the first pass.

**When a pass runs**

- After a file system event, once the storage has been quiet for 1 second, and after 5 seconds at the latest. The event only names the path. What happened to it is decided by the listing. This needs a storage on disk and `enableFileWatching` switched on.
- At once after the file watcher reported an error, such as a buffer overflow. The watcher restarts, and the pass compares the content of every file, because events may be missing.
- Every `reconcileIntervalSeconds`, which covers changes that no event reported. It also runs when file watching is off.
- An event or a request that arrives while a pass runs leads to one more pass after it, never to a second pass next to it.

| Setting | Default | Meaning |
|---|---|---|
| `enableFileWatching` | `true` | Whether file system events lead to passes. Only a storage on disk has events. |
| `reconcileIntervalSeconds` | `300` | Seconds between the periodic passes. Zero or a negative value switches them off, and a value longer than a timer accepts (about 49 days) is shortened to that. |

**What a pass does with a path**

| In the storage | Applied before | Action |
|---|---|---|
| present | no | The file is loaded and its subject added. A folder becomes a `VirtualFolder`. |
| gone, or a file that became a folder or the reverse | yes | The subject or folder is removed. |
| size or modification time differs, or an event named it | yes | The file is hashed and the subject reloaded only if the hash differs. The size and time that a file subject shows follow the listing either way. |
| same size and time, not named | yes | Nothing. The file is not opened. |

After a lost event, and in the pass after one that failed or was cancelled, every file counts as named, so the content of every file is compared once.

- **Exact names.** Paths are compared exactly as the storage reports them, on every platform. A rename is the old path gone and the new one present, which includes a rename that only changes the casing.
- **Moved JSON subjects keep their instance.** A configurable JSON subject keeps its instance when its file moves to another folder or its folder is renamed: in the same pass, exactly one JSON subject with that content disappears and a new `.json` file with the same content appears in another folder. The subject is neither stopped nor recreated. A rename within the same folder creates a new instance.
- **Ignored paths.** A path with a segment that starts with a dot (`.DS_Store`, `.idea`, `._*`) or has a temp name (`~` prefix or suffix, `.tmp` suffix, `.tmp.` in the name) never becomes a subject. That includes everything below a folder with such a name. A visible folder that holds only ignored files shows as an empty `VirtualFolder`.
- **JSON files.** What a `.json` file is follows its content. A file that was first read as plain or invalid JSON becomes its subject once its content describes one, a subject whose file names another type is replaced by a subject of that type, and a subject whose file loses its type becomes a plain JSON file. Content that is not valid JSON, such as a file that is still being written, leaves a subject as it is.
- **Plain files.** A generic file and a plain JSON file hold only size and time, which come from the listing without a call per file.
- **Keys.** A subject that is in the tree keeps its key. A file whose key is taken (`Docs.json` next to a `Docs` folder) is left out with a warning and loaded again in the first pass in which the key is free. The same holds for a folder. Its files are loaded in the pass that places it or in the one after it.
- **Failed loads.** A file that cannot be loaded is retried when it changes or when an event names it. After an IO error, such as a file that its writer still holds, the next pass tries again.
- **Folders.** The children of a folder are assigned once per pass, and only when they changed. When a subject moved, they are assigned in two steps, first everything that is added and then everything that is removed, so the moved subject is not stopped and restarted.

**One thing at a time**

Passes and the operations that change the storage (create, delete, write, save configuration) run one after another on a single worker. Only the worker changes the tree, so there is no lock. An operation that arrives during a pass waits for it. Reading a file or its metadata does not go through the worker.

After a write, the worker records the new size, time and hash, so the event that the write raises reloads nothing. `WriteBlobAsync` first copies the content it is given, writes from that copy and then refreshes the subject of the file. If refreshing the subject after the write fails, the write still succeeds for the caller, the failure is logged, and the next pass reloads the file, with or without an event.

Code that runs on the worker, such as the load of a subject or its `ApplyConfigurationAsync`, must await its calls back into the storage. Background work that a subject starts when a pass attaches it is not part of the pass, and its later calls into the storage are queued like any other.

**An unresponsive storage**

Every call on the storage client is started on the thread pool and limited to 30 seconds. When such a call hits the limit, a pass ends without changing the tree and sets the status of the storage to `Error`, and an operation fails with an exception. No file is marked as failed because of it: the next pass tries everything again and compares the content of every file once. While a call that hit the limit has not returned, further calls fail at once instead of waiting again.

Reading the content of a file to its end on the worker has the same limit, but there it is a failure of that one file: the pass goes on with the other files, the status stays `Connected`, and the file is loaded when it changes or an event names it. A new file is left out until then, and a file that is in the tree keeps the content it has.

**Connecting and stopping**

A connection to the storage owns its worker, its index and its timers. Connecting again ends the old connection, lets its running work finish, and starts a new connection with every subject rebuilt. Applying the configuration of the storage therefore connects again only when a setting of the connection changed (storage type, connection string, container name, file watching, reconcile interval) or the storage is not connected. Saving a page in the editor applies the configuration of the storage that holds it, which changes nothing while the storage is connected. A connect that arrives while another one is running waits for it to finish.

`StopAsync` stops the file watcher and the timers, so the tree no longer follows the storage, while writes still work. A pass that is running or already queued on the worker still completes. `StartAsync` connects again.

#### Known Limitations and Follow-ups

- **Samba.** The tests run on Linux in CI. A data folder edited over an SMB share is the setup this was built for, and it has only been observed, not tested.
- **Coarse timestamps.** On a file system with timestamps of one or two seconds, an edit that keeps the size and falls into the same tick is only seen when its event arrives. The periodic pass alone misses it.
- **A long pass delays the UI.** An operation waits for the running pass, which matters at startup, when the first pass loads every file.
- **Subject code on the worker.** A subject whose `ApplyConfigurationAsync` hangs blocks the worker. The 30 second limit covers storage calls, not subject code.
- **Renamed subjects are recreated.** A JSON subject that is renamed within its folder gets a new instance, and so does a renamed markdown or other file. Keeping the instance needs a fix in the core library for entries that are re-keyed inside one dictionary.
- **Blocked keys.** A JSON subject whose key is taken is deserialized and dropped again each time the blocked file changes or an event names it.
- **Streams outside the worker.** The 30 second limit does not cover a stream that other code reads after `ReadBlobAsync`, for example a download in the UI.
- **Writes that hit the limit.** Such a write keeps running in the background.
- **Writes in memory.** `WriteBlobAsync` holds the content in memory while it writes.
- **Missing directory.** When file watching is on and the directory is missing at startup, the storage stays in `Error` until it is reconfigured or restarted.
- **Folders on disk.** The disk client creates the folder of a path it is asked about. A folder that is deleted while one of its files is being loaded can come back empty.
- **Reconnect.** A reconnect waits for a connect that is still running, including its first pass, and cannot end it early.
- **No periodic pass, no retry.** With `reconcileIntervalSeconds` at zero, a failed pass is retried only when the next event arrives.
- **File watcher that cannot restart.** A file watcher that fails and cannot restart leaves the storage on the periodic pass until it reconnects.
- **Cancelled calls.** A call that was given up because its caller was cancelled also makes the client refuse calls until it returns, so a pass in that moment fails and the status shows `Error` until the next pass.
- **After an unfinished pass.** After a pass that did not finish, the next one compares the content of every file once.
- **Cancelled operations.** A queued operation whose caller cancels is only dropped when the worker reaches it.

### File Hierarchy

Files are organized into a `VirtualFolder` tree. Each folder delegates storage operations to its parent `IStorageContainer`. A subject is in the index under its path if and only if it is placed in the tree. `AddSubjectAsync` writes the file and places the subject as one step on the worker, and throws without writing when the path is ignored, a file already exists there, or the key is taken.

### File Types

File subjects are created based on file extension via `FileExtensionAttribute`:

| Type | Extension | Description |
|------|-----------|-------------|
| `MarkdownFile` | `.md` | Markdown with YAML frontmatter, embedded subjects (```` ```subject(name) ````), and live expressions (`{{ path }}`) |
| `JsonFile` | `.json` | Plain JSON files (non-configurable subjects) |
| `GenericFile` | Other | Fallback for unknown extensions — metadata only |

Plugin authors can register additional file types via `[FileExtension]`.

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
