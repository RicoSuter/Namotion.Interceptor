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
| `MarkdownFile` | `.md` | Markdown with YAML frontmatter, widget blocks (`:: Full.Type.Name`) and live expressions (`{{ path }}`) |
| `JsonFile` | `.json` | Plain JSON files (non-configurable subjects) |
| `GenericFile` | Other | Fallback for unknown extensions — metadata only |

Plugin authors can register additional file types via `[FileExtension]`.

### Markdown Pages [Implemented]

A `MarkdownFile` turns its text into child subjects when the file is loaded and again each time it changes. [Markdown Pages](../../administration/pages.md) describes the syntax. The code is in `HomeBlaze.Storage`, and the rendering is in `HomeBlaze.Storage.Blazor`.

| Unit | Location | Responsibility |
|------|----------|----------------|
| `MarkdownPageParser` | `HomeBlaze.Storage/Pages` | Page text to a tree of blocks with their positions in the source. Owns the grammar of the head line (`MarkdownBlockHead`) and reports the errors that are visible from the syntax alone. Knows nothing about subjects. The parsed `MarkdownPage` tells the editor where a new block goes and how many colons it gets. For that alone it reads the slots of the types around the place, which the editor finds by their names |
| `MarkdownBlockContainerParser` | `HomeBlaze.Storage/Pages` | The Markdig block parser for the colon lines, which replaces Markdig's custom containers in the pipeline. A colon line starts with a run of two or more colons, and `MarkdownBlockFence` is the one place that names the character. A colon line is a head line only when the text after the colons starts with a letter, `_` or `{`. With any other text the line is no colon line and stays markdown text. A head line ends every open block with as many colons or more, together with the blocks that are still open inside them, and opens its block in the container that remains, so the open blocks always have more colons the further in they are. A colon line with nothing after the colons is a closing line. It closes the innermost open block with the same number of colons, together with the blocks that are still open inside it. The end of the page ends every open block, and a block without a closing line is no error. A colon line ends a block only when nothing but blocks is open between the line and that block: inside a quote or a list item it reaches no block around them, where a head line is reported as a block at a place that allows none and a closing line as a line that closes nothing. A block is never ended when Markdig asks it to continue, so the blocks inside it see the line first and a code block keeps a colon line as text. A raw HTML block would keep a colon line as well. It is ended for a closing line that has the number of colons of a block around it, and for a head line when the HTML block is of a kind that an empty line ends. Only blocks may be between the HTML and that block. A script, a style element, a comment and the other kinds that end at an end of their own keep a head line, and a closing line of another length stays part of the HTML. After a definition list, Markdig takes the first colon off a line before the parser sees it, so a colon line there is read with its first colon restored. A closing line that closes nothing becomes a node of its own, which the page parser reports as an invalid block of one line. The node of a block records whether a closing line of its own ended it and its last line, which without a closing line is its last line of content. The span of the node ends exactly only with a closing line |
| `MarkdownSegmentBuilder` | `HomeBlaze.Storage/Pages` | Renders the parsed page to HTML once and cuts the HTML into segments that follow the nesting of the blocks: HTML, expressions and blocks with their content. Writes the error boxes for syntax errors and for the old `subject(name)` form, a fenced code block with that language. Writes the braces inside markdown code as character references, so an expression in code is shown as written. `FindExpressions` gives the editor and the "Read Evaluated Content" query the expressions in the page text by the same rule: a path between two pairs of braces without a brace or a `<` in it, and no brace inside markdown code counts. It reads the text and not the rendered page, so it also finds an expression that the page does not render: one in the front matter, in the content of an invalid block or on the head line of a block, and one whose braces enclose markdown markup or the border of a block |
| `MarkdownContentParser` | `HomeBlaze.Storage/Internal` | Segments to subjects. Creates the subject of a block or updates the existing one in place, fills its slots, and reports the errors that need the types |
| `SlotMap` | `HomeBlaze.Storage/Pages` | Reads the `[Slot]` declarations of a type for the content parser and reports a declaration that cannot be used |
| `MarkdownFragment` | `HomeBlaze.Storage/Pages` | The subject that holds the content of one slot. Its `Children` have the same shape as the `Children` of the page |
| `MarkdownBlockWriter` | `HomeBlaze.Storage/Pages` | Writes the configuration of one block back into the page text. Replaces the head line and the `json` block and never touches the content of the block. A block without a closing line gets none. Also creates the text of a new block for the editor, which always has a closing line. What it adds gets the line ending of the page |
| `MarkdownContentView` | `HomeBlaze.Storage.Blazor/Files` | Renders the children of a page or of a fragment. HTML and expression values are joined into one markup string, every other child is rendered through `SubjectComponent` |
| `MarkdownDecorationParser`, `MarkdownBlockMatcher`, `MarkdownBlockNames` | `HomeBlaze.Storage.Blazor/Models` | The regions that the editor marks, the check that a block of the editor text is the same block as in the saved page, and the check of the name that the editor gives a new block |

The subjects of a page with a card and a named motor:

```
Page.md                     MarkdownFile
  Children
    Html_0_...              HtmlSegment
    Card_1                  Card
      Content               MarkdownFragment
        Children
          Html_0_...        HtmlSegment
          Expression_1_...  RenderExpression
      Footer                MarkdownFragment
    mymotor                 Motor
```

- **Keys.** The key of a block subject is its `#name`, or its type name and its position when it has no name. A reload finds an existing subject by its key in the same `Children` dictionary. Values of a typed list slot are matched by their name, and without a name by their position.
- **Snapshot.** `MarkdownFile` keeps the result of a parse as one immutable `MarkdownParseResult`: the children, the parsed page, the map from block index to the subject that the block created, and the map from name to subject. The snapshot is replaced as a whole, because reloads and write-backs run on different threads and must never pair the page of one parse with the subjects of another.
- **Write-back.** `MarkdownFile` is the `IConfigurationWriter` of the subjects on it. `WriteConfigurationAsync` finds the block of the edited subject through the map from block to subject. It walks up the parents for that, so a subject that is nested in the configuration of a block subject, like the child of a grid cell, is written with its block. The subject is serialized with `ConfigurableSubjectSerializer`, and the block writer puts single values on the head line and everything else into the `json` block.
- **Naming.** The configuration of a block is named by the property names of its type, on the head line and at every depth of the `json` block. Configuration files keep camelCase, and a page uses the property names, so that a page names a property the same way in an argument, in a path and in an expression. `ConfigurableSubjectSerializer` takes the naming as a `ConfigurationNaming` parameter of each call, with the naming of files as its default, and every call for a page passes `PropertyName`: creating, updating in place, checking a value and writing back. The keys that a parse remembers per subject to decide whether it can update in place, and the keys that a write-back records, therefore have the same naming. A property with an explicit JSON name keeps that name in files and has its property name on a page. The content parser checks the keys of the head line and the top level of the `json` block against the type and names the property when a key differs only in case. Below the top level the serializer skips a key it does not know, like in a file. In a file it also skips a value that does not convert, at every depth. On a page such a value is an error box: the content parser checks a value of the head line before the configuration is applied, and for the `json` block it passes a callback to `Deserialize` and `UpdateConfiguration` that receives every top level property the serializer skipped as a `SkippedConfigurationProperty`, also when the value failed deeper inside. The first one becomes the error of the block, and the subject is not used. A `null` that the property accepts is no skipped value.
- **Reload lock.** The reloads of one page run one after the other, because a parse changes the subjects it reuses in place. A write-back does not take the lock: the storage reloads the file from inside the write, on the same call chain. A subject on a page must therefore never wait for a write or a reload of its own page while it is created or configured.
- **Release.** A subject gets its context when it is created, which also starts its hosted service, and it leaves the context when the property that holds it lets it go. A subject that a parse created and that never reaches the page would therefore run forever: the subject of a block that ends as an error box, the slot values collected for such a block, and everything a parse created before it failed or was cancelled. The content parser records what it creates and hands these subjects to `ConfigurableSubjectSerializer.Release`, which detaches them with everything below them. It gets a new subject through a callback of `Deserialize` before the configuration is set, so that it also knows a subject whose configuration could not be applied. A release that fails is logged and does not stop the others. The checks that need no instance, such as an unknown slot name or content for a type without a default slot, run before the instance is created, so the common mistakes create nothing. A new subject that was already put into a reused subject is not released, because it is on the page through that subject.
- **Page scope.** `MarkdownFile` implements `IPathScope`. `SubjectPathResolverScopeExtensions` in `HomeBlaze.Services` resolves relative paths and names from the nearest scope above a subject. See [Paths](../../administration/paths.md).

### Subject Files vs Documents [Implemented]

Files in storage become subjects in the knowledge graph through two different paths:

- **Subject files** — `.json` files with a `$type` discriminator are deserialized into typed subjects (e.g., `Motor`, `OpcUaServer`). The file is the persistence format; the subject is what appears in the graph. Only `[Configuration]` properties are persisted.
- **Documents** — all other files (Markdown, PDFs, images, plain JSON without `$type`) become document subjects (`MarkdownFile`, `JsonFile`, `GenericFile`, or custom types via `[FileExtension]` plugins). The file content is the document itself, visible as-is in the knowledge graph.

Documents are browsable in the subject tree, editable in the Blazor UI (Monaco editor for text files), and accessible via MCP tools (`browse` or `search` to find them, `invoke_query` to read them, for example `ReadEvaluatedContent` of a Markdown page, which with `includeSourcePaths` set to true follows each value with the absolute path of its property). Markdown files in local storage can also be changed through the operations `ReplaceText`, `ReplaceContent` and `CreateMarkdownFile`, which write through the same path as the editor but cannot add, change or remove blocks or add other active content, which only the page editor can, see [Changing pages](../../administration/ai-agents.md#changing-pages). JSON and other files have no such operations. Linking documents to other subjects (e.g., "this PDF is the manual for motor CNC-01") is handled via dynamic metadata / annotations (planned, see below).

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
