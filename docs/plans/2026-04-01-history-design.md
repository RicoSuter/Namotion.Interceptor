# History System Design

## Problem

HomeBlaze tracks every property change in real-time but has no way to look back. Users need to query historical values, reconstruct past graph states, and run aggregations over long time ranges. The system must work on low-powered devices (Raspberry Pi / SD cards) with many changes over long retention periods.

## Constraints

- Fast writes, slow reads acceptable
- Low-powered devices: sequential I/O only, minimize random writes
- Many changes over very long time (months/years)
- Any value type (numbers, strings, booleans, complex objects)
- Subjects identified by path (no stable IDs)
- Must support multiple sink implementations (file, SQLite, InfluxDB)

## Architecture

```
HistoryService (DI-registered BackgroundService)
├── Discovers sinks via lifecycle attach/detach events
├── Maintains local HashSet<IHistorySink> (no scanning)
├── Subscribes to CQP for property changes
├── Buffers changes in memory (flush every 10s or max count)
├── On flush: batch-write to all current sinks in parallel
├── Move tracking (detects path changes in buffer)
├── Snapshot scheduling per sink
├── Default aggregation over raw values (sinks can override)
└── Read routing by priority, time range, native aggregation
```

### Sinks as Subjects

Sinks are InterceptorSubjects, configured via JSON files like any other HomeBlaze subject. Add a sink by dropping a JSON file:

```json
{
  "$type": "SqliteHistorySink",
  "DatabasePath": "./history",
  "SnapshotInterval": "1.00:00:00",
  "PartitionInterval": "Weekly",
  "RetentionDays": 365,
  "Priority": 50
}
```

`HistoryService` discovers sinks via lifecycle attach/detach events — no polling, instant discovery and cleanup.

### Multiple Sinks

Multiple sinks run simultaneously (e.g., file sink for local history + InfluxDB for central). Each sink is independent — a failing sink doesn't block others.

For reads, `HistoryService` picks the best reader:
- Aggregation query → lowest priority reader with `SupportsNativeAggregation` covering the time range
- Raw query → lowest priority reader covering the time range
- Fallback → next reader in priority order

## Path Format

All paths in the history system use **canonical paths** from `SubjectPathResolver.GetPath(subject, PathStyle.Canonical)`. Examples:

- `/` — root
- `/Demo/Conveyor` — direct child
- `/Items[0]/Name` — collection items with bracket notation

Canonical paths are used in `HistoryRecord`, `MoveRecord`, `HistorySnapshot`, and all query APIs. Individual sinks are responsible for encoding paths into their storage format (e.g., the file sink sanitizes bracket characters for filesystem compatibility).

## Property Filtering

The `HistoryService` creates its own `ChangeQueueProcessor` with a `StateAttributePathProvider` — the same path provider used by MCP tools and connectors. This filters to:

- `[State]` properties — runtime values that change over time (temperature, speed, status)
- Structural properties (`CanContainSubjects`) — for graph reconstruction

`[Configuration]` properties are excluded — they are already persisted to JSON files by HomeBlaze's storage system.

## Integration

- `HistoryService` is registered in DI as a `BackgroundService` via `builder.Services.AddHistoryService()`
- It receives `IInterceptorSubjectContext` via DI
- Creates its own `ChangeQueueProcessor` (does not share with connectors)
- Sinks are discovered as subjects via lifecycle attach/detach events (loaded from JSON config by FluentStorage)

## Write Path (Hot Path — Zero Allocation)

### Internal Buffer

```csharp
struct HistoryEntry
{
    public int SubjectPathKey;     // interned string key
    public int PropertyNameKey;    // interned string key
    public long TimestampTicks;
    public HistoryValueType ValueType;
    public double NumericValue;    // inline for numbers (90% case)
    public int RawValueOffset;     // offset into shared byte buffer
    public int RawValueLength;     // for complex/string values
}

enum HistoryValueType : byte
{
    Null, Double, Boolean, String, Complex
}
```

- `HistoryEntry` is a struct — no heap allocation per change
- Subject paths and property names interned to int keys — no string allocation per change
- Numeric values stored inline as `double` — no JSON serialization on the hot path
- Complex values serialized to a shared pooled byte buffer

### Flush Cycle

1. CQP delivers change → append `HistoryEntry` to pre-allocated ring buffer (one lock, no I/O)
2. Timer fires (10s) or buffer hits max count → signal flush
3. Swap buffer under lock (minimal lock time — pointer swap only)
4. Resolve int keys to strings
5. Fan out `WriteBatchAsync` to all sinks in parallel
6. Flush pending moves

### Exception Handling

Per-sink try/catch on every flush — failing sink gets `Status = "Error"`, logged, and skipped. Service never crashes from a sink failure.

### Memory Leak Prevention

| State | Holds subject ref? | Cleanup |
|---|---|---|
| `_buffer` (HistoryEntry[]) | No (int keys only) | Swapped on flush |
| `_lastKnownPaths` | Yes | Removed on lifecycle detach |
| `_pathKeys` / `_pathStrings` | No (strings only) | Grows monotonically, bounded by graph size |
| `_sinks` | Yes (IHistorySink) | Removed on lifecycle detach |

## Read Path

Reads are user-initiated, infrequent queries. No allocation-free requirements — simple return types.

## Interfaces

### Write Side

```csharp
interface IHistoryWriter
{
    Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    Task WriteSnapshotAsync(HistorySnapshot snapshot);
    Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
}

readonly struct ResolvedHistoryRecord
{
    public readonly string SubjectPath;
    public readonly string PropertyName;
    public readonly long TimestampTicks;
    public readonly HistoryValueType ValueType;
    public readonly double NumericValue;
    public readonly ReadOnlyMemory<byte> RawValue;
}
```

### Read Side

```csharp
interface IHistoryReader
{
    int Priority { get; }
    DateTimeOffset? OldestRecord { get; }
    bool SupportsNativeAggregation { get; }

    Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query);
    Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time);
    IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to,
        TimeSpan interval);
}
```

### Combined

```csharp
interface IHistorySink : IHistoryWriter, IHistoryReader { }
```

### Base Class

```csharp
[InterceptorSubject]
public abstract partial class HistorySinkBase : IHistorySink
{
    [Configuration]
    public partial TimeSpan SnapshotInterval { get; set; }

    [Configuration]
    public partial int RetentionDays { get; set; }

    [Configuration]
    public partial int Priority { get; set; }  // default 100

    [State]
    public partial DateTimeOffset? LastSnapshotTime { get; set; }

    [State]
    public partial long RecordsWritten { get; set; }

    [State]
    public partial string Status { get; set; }

    // Abstract — each sink implements storage
    public abstract Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    public abstract Task WriteSnapshotAsync(HistorySnapshot snapshot);
    public abstract Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
    public abstract Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query);
    public abstract Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time);
    public abstract IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to, TimeSpan interval);
}
```

## Data Model

```csharp
record HistoryRecord(
    string SubjectPath,
    string PropertyName,
    DateTimeOffset Timestamp,
    JsonElement Value);

record MoveRecord(
    DateTimeOffset Timestamp,
    string FromPath,
    string ToPath);

record HistorySnapshot(
    DateTimeOffset Timestamp,
    string BasePath,
    IReadOnlyDictionary<string, HistorySubjectSnapshot> Subjects);

record HistorySubjectSnapshot(
    IReadOnlyDictionary<string, JsonElement> Properties);

record AggregatedRecord(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    double? Avg, double? Min, double? Max,
    double? Sum, long Count,
    JsonElement? First, JsonElement? Last);

record HistoryQuery(
    string SubjectPath,
    IReadOnlyList<string> PropertyNames,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? BucketSize = null,
    AggregationType? Aggregation = null,
    bool FollowMoves = true);

enum AggregationType
{
    Avg, Min, Max, Sum, Count, First, Last
}
```

## Move Tracking

Subjects are identified by path (no stable IDs). When a subject moves in the graph, history must remain continuous.

### Current Behavior (v1)

History is recorded by canonical path. If a subject's path changes (rename, reorganization), history under the old path becomes orphaned. New history records under the new path. No continuity across the rename.

This works well for the common case: device paths are deterministic from configuration (e.g., `/Devices/HueBridge/Light1` is always the same after restart). Devices reconnecting after app restart produce the same paths — history is continuous without any move tracking.

### Planned Follow-Up: Move Operations

A proper move operation would:

1. Detect path changes at runtime via `Dictionary<IInterceptorSubject, string>` (object reference identity is stable while the app is running)
2. Emit `MoveRecord` to all sinks
3. Sinks rename their storage (file sink: rename directory, e.g., `subjects/Demo/Motor/` → `subjects/Factory/Motor/`)
4. Record the move for backward query resolution (`FollowMoves` in `HistoryQuery`)
5. Keep the subject alive — no detach/re-attach, no data loss

**Limitation:** Move tracking depends on in-memory object identity. Moves across app restarts cannot be detected (no stable IDs). In practice this is rarely a problem since moves are manual actions that happen while the app is running.

Until move operations are implemented, `MoveRecord`, `WriteMovesAsync`, and `FollowMoves` are defined in the interfaces but not active.

## Snapshot Reconstruction

To reconstruct the graph at time T:

1. Find the newest periodic snapshot before T
2. Replay only the logs between that snapshot and T
3. Apply last-before-T value for each property

Daily snapshots mean worst case ~24 hours of log replay. Configurable per sink via `SnapshotInterval`.

Snapshots are cheap to create — serialize the current live graph state. This is a periodic background task.

**Partial snapshots:** `GetSnapshotAsync("/Demo", time)` filters to paths under `/Demo/` only.

**Snapshot series:** `GetSnapshotsAsync` returns `IAsyncEnumerable<HistorySnapshot>` — consumer processes one at a time without holding all in memory.

## Project Structure

```
HomeBlaze.History.Abstractions      ← interfaces, records, enums (lightweight)
    ↑              ↑
    │              │
HomeBlaze.History   HomeBlaze.AI    ← service orchestrator / MCP tools (independent)
    ↑
    │
HomeBlaze.History.Sqlite            ← sink implementation
```

### Projects

| Project | Contents |
|---|---|
| `HomeBlaze.History.Abstractions` | `IHistoryWriter`, `IHistoryReader`, `IHistorySink`, all records/enums |
| `HomeBlaze.History` | `HistoryService`, `HistorySinkBase`, DI extensions |
| `HomeBlaze.History.Sqlite` | `SqliteHistorySink` |
| `HomeBlaze.History.Tests` | HistoryService tests |
| `HomeBlaze.History.Sqlite.Tests` | SQLite sink tests |
| `HomeBlaze.AI` (existing) | MCP tools (`get_property_history`, `get_snapshot`, `get_snapshots`) — depends only on Abstractions |

Later:
| `HomeBlaze.History.TimescaleDb` | TimescaleDB sink for industrial scale |

### Scale Tiers

| Sink | Subjects | Changes/sec | Dependency |
|---|---|---|---|
| SQLite | ~5,000 | ~1,000 | Embedded, no server |
| TimescaleDB (planned) | 50K+ | 10K+ | External PostgreSQL server |

A file-based sink can be added later via `IHistorySink` if human-readable logs are needed.

## SQLite Sink Implementation

### Database Partitioning

One SQLite database file per configurable time interval:

```
history/
  history-2026-W14.db       # week 14 (history + snapshots tables)
  history-2026-W13.db       # week 13
  history-2026-W12.db       # week 12
  history-moves.db          # single small DB for move records (planned follow-up)
```

```csharp
[Configuration]
public partial HistoryPartitionInterval PartitionInterval { get; set; } // default: Weekly

enum HistoryPartitionInterval { Daily, Weekly, Monthly }
```

### Schema

```sql
-- Property change records
CREATE TABLE history (
    timestamp INTEGER NOT NULL,          -- ticks (UTC)
    subject_path TEXT NOT NULL,
    property_name TEXT NOT NULL,
    value_type INTEGER NOT NULL,         -- 0=Null, 1=Double, 2=Boolean, 3=String, 4=Complex
    numeric_value REAL,                  -- inline for numbers (fast path)
    raw_value TEXT,                      -- JSON for complex values
    PRIMARY KEY (subject_path, property_name, timestamp)
) WITHOUT ROWID;

-- Query index: property history over time range
CREATE INDEX ix_history_time ON history (subject_path, property_name, timestamp);

-- Periodic graph snapshots (gzipped JSON blobs)
CREATE TABLE snapshots (
    timestamp INTEGER NOT NULL PRIMARY KEY,
    base_path TEXT NOT NULL,             -- "/" for full, "/Demo" for partial
    data BLOB NOT NULL                   -- gzipped JSON of full graph state
);
```

`WITHOUT ROWID` with the clustered primary key means property history queries are a single index range scan — no secondary lookup.

Snapshots are stored as gzipped JSON blobs — always read whole and decompressed, never partially queried. This keeps snapshot storage compact over long retention periods.

### Write Path

```csharp
public override async Task WriteBatchAsync(
    ReadOnlyMemory<ResolvedHistoryRecord> records)
{
    // 1. Group records by partition (date → DB file)
    // 2. Per partition: single transaction, batch INSERT
    //    SQLite WAL mode handles concurrent reads during write
    // 3. Numeric fast path: set numeric_value, leave raw_value null
    //    Complex values: serialize to raw_value JSON
}
```

Connection pooling: one open connection per active partition (typically 1-2). Connections closed when partition rolls over.

### Read Path

```csharp
public override async Task<IReadOnlyList<HistoryRecord>> QueryAsync(
    HistoryQuery query)
{
    // 1. Determine which partition DBs cover the time range
    // 2. Query each, UNION results
    // 3. If aggregation requested and SupportsNativeAggregation:
    //    Use SQL AVG/MIN/MAX/SUM/COUNT with GROUP BY time bucket
}

public override bool SupportsNativeAggregation => true;  // SQL does this natively
```

### Native Aggregation

SQLite handles aggregation directly — no need for `HistoryService` fallback:

```sql
SELECT
    (timestamp / @bucketTicks) * @bucketTicks AS bucket_start,
    AVG(numeric_value) AS avg,
    MIN(numeric_value) AS min_val,
    MAX(numeric_value) AS max_val,
    COUNT(*) AS count
FROM history
WHERE subject_path = @path
  AND property_name = @property
  AND timestamp BETWEEN @from AND @to
GROUP BY bucket_start
ORDER BY bucket_start;
```

### Housekeeping

- **Retention:** Delete partition DB files older than `RetentionDays`
- **Snapshots:** Periodic full graph serialization to gzipped blob in the current partition's `snapshots` table
- **WAL checkpoint:** Periodic `PRAGMA wal_checkpoint(TRUNCATE)` to reclaim WAL space

## MCP Tools

### get_property_history

Query raw or aggregated property values over a time range.

- **Input:** `subjectPath`, `propertyNames[]`, `from`, `to`, `bucketSize?`, `aggregation?`, `followMoves?`
- **Output (raw):** `{ "records": { "Temperature": [{"t":"...","v":42.5}, ...] } }`
- **Output (aggregated):** `{ "buckets": { "Temperature": [{"from":"...","to":"...","avg":42.5,...}] } }`

### get_snapshot

Reconstruct graph state at a point in time.

- **Input:** `path`, `time`
- **Output:** `{ "time":"...", "subjects": { "/Demo/Conveyor": { "CurrentSpeed": 42.5, ... } } }`

### get_snapshots

Snapshot series over a time range.

- **Input:** `path`, `from`, `to`, `interval`
- **Output:** `{ "snapshots": [{ "time":"...", "subjects": {...} }, ...] }`

All three resolve moves transparently when `followMoves` is true (default).

## Future: Time-Series Database Sink

For deployments beyond SQLite's scale (~5,000 subjects, ~1,000 changes/sec), a dedicated time-series database is needed.

### Candidates

| | QuestDB | TimescaleDB | ClickHouse | VictoriaMetrics |
|---|---|---|---|---|
| **License** | Apache 2.0 | Core: Apache 2.0, extras: TSL | Apache 2.0 | Apache 2.0 |
| **Query** | SQL (PG wire) | SQL (PostgreSQL) | SQL | MetricsQL (not SQL) |
| **.NET client** | Npgsql | Npgsql | ClickHouse.Client | HTTP API |
| **Docker** | Single container | Single container | Single container | Single container |
| **Resource usage** | Low | Medium (PostgreSQL) | Medium-high | Very low |
| **Best at** | Fast ingestion, IoT | General time-series | Analytics on huge datasets | Metrics at scale |
| **Maturity** | Growing | Very mature | Very mature | Mature |
| **Embedding in product** | No restrictions | Allowed (TSL only restricts offering it as a managed DB service) | No restrictions | No restrictions |

### Licensing Notes

- **QuestDB** (Apache 2.0): Fully open source, no restrictions. Can be embedded, redistributed, sold as part of a product.
- **TimescaleDB** (TSL for advanced features): Free to self-host and embed in products. The only restriction is offering TimescaleDB itself as a standalone managed database service (competing with Timescale Cloud). Building and selling a box with TimescaleDB storing data internally is explicitly allowed.
- **InfluxDB**: Not recommended. v3 is proprietary/cloud-only, v2 has restrictive license, Flux query language deprecated. Risky long-term investment.

### Recommendation

**QuestDB or TimescaleDB** — both use the PostgreSQL wire protocol, so the sink implementation can share most code via Npgsql. A single `HomeBlaze.History.PostgreSql` project could potentially support both backends with minimal configuration differences.

| Project | Backend |
|---|---|
| `HomeBlaze.History.PostgreSql` | TimescaleDB or QuestDB (shared Npgsql implementation) |

TimescaleDB is the recommended default — mature, PostgreSQL ecosystem, continuous aggregates and compression built in. QuestDB is a lighter alternative for resource-constrained deployments. Both are supported by the same sink via Npgsql; the user picks their backend by choosing which Docker container to run. TimescaleDB-specific features (e.g., `time_bucket()`, continuous aggregates) can be optionally detected and used when available.

## What This Replaces

The planned `IHistoryStore` from `Data/Docs/architecture/design/history.md` is superseded by this design. Key differences:
- Sink discovery via lifecycle events instead of DI registration
- Sinks are subjects (configurable via JSON) instead of BackgroundServices
- Allocation-free write path with batched flushes
- Snapshot reconstruction for full graph time-travel
- Move tracking for path-based identity continuity
