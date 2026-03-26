---
title: Storage
navTitle: Storage
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
| Central UNS | Satellites | Satellites reconnect, send Welcome snapshot with full state |
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

### File Watching and Change Detection

For filesystem backends, `StorageFileWatcher` provides:
- Reactive file monitoring with debouncing (500ms coalesce window)
- Self-write protection (2-second grace period to prevent feedback loops)
- SHA256 content hashing for change detection
- Automatic rescan on filesystem watcher buffer overflow

### File Hierarchy

`StorageHierarchyManager` organizes files into a `VirtualFolder` tree structure. Each folder delegates storage operations to its parent `IStorageContainer`. Path normalization handles platform differences (forward slashes, case-insensitive lookup on Windows).

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

This enables operators and integrators to enrich the knowledge graph with domain-specific metadata without modifying subject code.

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
| No live state persistence | Recover from source of truth | External world (devices, peers) is authoritative. Avoids stale state |
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
