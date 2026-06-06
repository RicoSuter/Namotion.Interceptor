# History System Design

## Problem

HomeBlaze tracks every property change in real-time but has no way to look back. Users need to query historical values, reconstruct past graph states, and run aggregations over long time ranges. The system must work on low-powered devices (Raspberry Pi / SD cards) with many changes over long retention periods.

## Constraints

- Fast writes, slow reads acceptable
- Low-powered devices: sequential I/O only, minimize random writes
- Many changes over very long time (months/years)
- Any value type (numbers, strings, booleans, complex objects)
- Subjects identified by path (no stable IDs — subject IDs are in-memory only, regenerated on restart)
- Must support multiple simultaneous sink implementations (in-memory, SQLite, TimescaleDB)
- Per-sink retention horizon (`MaxAge`) so storage is bounded on low-powered devices and at industrial scale
- Recent data (within the buffered-flush window) must always be queryable, regardless of which persistent sinks are attached

## Architecture

```
HistoryService (DI-registered BackgroundService)
├── Discovers sinks via lifecycle attach/detach events
├── Maintains per-sink cursors into shared buffer
├── Subscribes to CQP for property changes (dedup stage)
├── CQP write handler appends to shared buffer
├── Per-sink flush timers drain buffer via cursors
├── Move tracking (detects path changes in buffer)
├── Snapshot scheduling per sink
├── Default aggregation over raw values (sinks can override)
└── Read routing by priority, time range, native aggregation
```

### Sinks as Subjects

Sinks are InterceptorSubjects, configured via JSON files like any other HomeBlaze subject. Add a sink by dropping a JSON file:

```json
{
  "$type": "HomeBlaze.History.Sqlite.SqliteHistorySink",
  "DatabasePath": "./history",
  "FlushInterval": "00:00:10",
  "SnapshotInterval": "1.00:00:00",
  "PartitionInterval": "Weekly",
  "MaxAge": "365.00:00:00",
  "Priority": 50
}
```

`HistoryService` discovers sinks via lifecycle attach/detach events — no polling, instant discovery and cleanup.

### Multiple Sinks

Multiple sinks run simultaneously (e.g., in-memory for fast recent queries + SQLite for long-term). Each sink is independent. A failing sink doesn't block others.

### Sink Coverage Window

Each sink reports two time-anchored bounds:

- `MaxAge` (configuration): how far back the sink retains data.
- `MinAge` (intrinsic): how recent the data can be before the sink can serve it. This is the buffered-flush blind spot. For an in-memory sink it is zero. For persistent sinks it is approximately `FlushInterval` (data younger than that is still in the shared buffer, not yet in the sink's storage).

A sink is queryable for the time window `[now - MaxAge, now - MinAge]`. The `HistoryService`'s shared buffer covers `[now - bufferLength, now]` directly, so the system always answers recent queries even when no in-memory sink subject is attached.

### Read Routing

For reads, `HistoryService` splits the query at the `MinAge` boundary:

1. The young tail (`[now - MinAge_min, now]`, where `MinAge_min` is the smallest MinAge of any persistent sink) is answered from the shared buffer.
2. The remainder routes by priority and capability:
   - Aggregation query: lowest priority reader with `SupportsNativeAggregation` whose coverage window contains the range.
   - Raw query: lowest priority reader whose coverage window contains the range.
   - Fallback: next reader in priority order.
3. Results are stitched in `timestamp` ascending. For aggregated queries, partial buckets from each leg are merged using the same cross-partition merge logic (see Native Aggregation).

### Self-Recording Prevention

`HistoryService` filters out `IHistorySink` subjects from recording via type check in the CQP property filter. Without this, sink bookkeeping properties (`RecordsWritten`, `Status`) would create a feedback loop — every flush would record its own stats.

## Path Format

All paths in the history system use **canonical paths** from `ISubjectPathResolver.GetPath(subject, PathStyle.Canonical)`. Examples:

- `/` — root
- `/Demo/Conveyor` — direct child
- `/Items[0]/Name` — collection items with bracket notation

Canonical paths are used in `HistoryRecord`, `MoveRecord`, `HistorySnapshot`, and all query APIs. Individual sinks are responsible for encoding paths into their storage format (e.g., the file sink sanitizes bracket characters for filesystem compatibility).

### ISubjectPathResolver

`HistoryService` depends on `ISubjectPathResolver` (extracted interface) rather than the concrete `SubjectPathResolver`. This enables:
- Clean DI registration
- Mock path resolvers in unit tests (tests set exact paths, no `RootManager` needed)
- Integration tests in `HomeBlaze.E2E.Tests` use the real resolver

## Property Filtering

The `HistoryService` creates its own `ChangeQueueProcessor` with a property filter that includes:

- `[State]` properties — runtime values that change over time (temperature, speed, status)
- Excludes `IHistorySink` subjects — prevents self-recording feedback loop

`[Configuration]` properties are excluded — they are already persisted to JSON files by HomeBlaze's storage system.

### Structural Property Recording

Properties with `[State]` AND `CanContainSubjects` (dictionaries, collections of subjects) are recorded as **lightweight path references**, not full serialized content:

- Direct subject reference → canonical path string (or null)
- Dictionary → JSON array of keys
- Collection → JSON array of subject paths

This enables graph structure reconstruction between snapshots without bloating the history database with serialized object graphs.

## Integration

- `HistoryService` is registered in DI as a `BackgroundService` via `builder.Services.AddHistoryService()`
- It receives `IInterceptorSubjectContext` and `ISubjectPathResolver` via DI
- Deduplication interval configured via `IConfiguration` (appsettings.json)
- Creates its own `ChangeQueueProcessor` (does not share with connectors)
- Sinks are discovered as subjects via lifecycle attach/detach events (loaded from JSON config by FluentStorage)
- Unsubscribes from lifecycle events in `Dispose()`

## Two-Stage Buffering

History uses two distinct buffering stages:

### Stage 1: CQP Deduplication

The `ChangeQueueProcessor` deduplicates rapid property changes. If a property changes 10 times within the dedup interval, only one change is recorded (oldest old value + newest new value). Interval configured globally via appsettings.json:

```json
{
  "History": {
    "DeduplicationInterval": "00:00:01"
  }
}
```

The CQP write handler appends deduped changes to a shared in-memory buffer.

### Stage 2: Per-Sink Flush

Each sink has its own `FlushInterval` (`[Configuration]` property on `HistorySinkBase`, default 10s). The `HistoryService` manages per-sink cursors into the shared buffer:

```csharp
private readonly List<ResolvedHistoryRecord> _buffer = new();
private readonly Dictionary<IHistorySink, int> _sinkCursors = new();
```

On each sink's flush tick:
1. Slice `_buffer[myCursor..end]`
2. Call `sink.WriteBatchAsync()` with the slice
3. Advance cursor
4. Trim buffer prefix when ALL cursors have passed it

Sink lifecycle:
- `SubjectAttached` → add cursor at current buffer end
- `SubjectDetaching` → remove cursor (may allow buffer trimming)

Memory is bounded: buffer only holds records between the fastest and slowest sink.

The buffer also acts as the primary read source for the recent tail: any query for a range that extends into `[now - MinAge, now]` for some sink is answered from the buffer for that portion, regardless of which sinks are attached. The system never has a "no sink covers this" gap at the recent end. There is no validation requirement that an `InMemoryHistorySink` subject be attached; the buffer is intrinsic to `HistoryService`.

### Exception Handling

Per-sink try/catch on every flush — failing sink gets `Status = "Error"`, logged, and skipped. Service never crashes from a sink failure.

### Memory Leak Prevention

| State | Holds subject ref? | Cleanup |
|---|---|---|
| `_buffer` (shared) | No (resolved records with string paths) | Trimmed when all cursors advance |
| `_lastKnownPaths` | Yes | Removed on lifecycle detach |
| `_sinkCursors` | Yes (IHistorySink) | Removed on lifecycle detach |

## Value Serialization

All values are stored as JSON for clean round-tripping:

| .NET Type | HistoryValueType | Storage | Read Back |
|---|---|---|---|
| `null` | Null | — | `JsonSerializer.SerializeToElement<object?>(null)` → `JsonValueKind.Null` |
| `double`, `float`, `int`, `long`, `decimal` | Double | `numeric_value` column | `JsonSerializer.SerializeToElement(reader.GetDouble())` |
| `bool` | Boolean | `numeric_value` (1.0/0.0) | `JsonSerializer.SerializeToElement(reader.GetDouble() != 0.0)` → `true`/`false` |
| `string` | String | `raw_value` (JSON-encoded) | `JsonSerializer.Deserialize<JsonElement>(reader.GetString())` |
| everything else | Complex | `raw_value` (JSON-serialized) | `JsonSerializer.Deserialize<JsonElement>(reader.GetString())` |
| `IInterceptorSubject` | String | `raw_value` (canonical path) | Path string |
| `IDictionary` (subjects) | Complex | `raw_value` (JSON array of keys) | Key array |
| `ICollection` (subjects) | Complex | `raw_value` (JSON array of paths) | Path array |

Key design choices:
- Strings stored JSON-encoded (`"Running"` with quotes) for clean `JsonDocument` round-trip
- `JsonSerializer.Deserialize<JsonElement>()` used instead of `JsonDocument.Parse()` to avoid `IDisposable` memory leaks
- Booleans round-trip as `true`/`false`, not `1.0`/`0.0`
- Null values produce `JsonValueKind.Null`, not `JsonValueKind.Undefined`

## Read Path

Reads are user-initiated, infrequent queries. No allocation-free requirements — simple return types.

### Typed Deserialization

`HistoryRecord.Value` is a `JsonElement` (dynamic). Extension methods in `HomeBlaze.History` provide typed access:

```csharp
// Caller knows type at compile time
record.DeserializeValue<double>();

// Caller has type at runtime
record.DeserializeValue(typeof(double));

// Resolve type from live subject via registry
record.DeserializeValue(subject);
```

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
    TimeSpan MinAge { get; }   // recent-end blind spot; routing uses [now - MaxAge, now - MinAge]
    TimeSpan MaxAge { get; }   // operational ceiling on retention
    bool SupportsNativeAggregation { get; }

    Task<DateTimeOffset?> GetLatestSnapshotTimeAsync();
    Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query);
    Task<IReadOnlyList<AggregatedRecord>> QueryAggregatedAsync(HistoryQuery query);
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
    public partial TimeSpan FlushInterval { get; set; }  // default 10s

    [Configuration]
    public partial TimeSpan SnapshotInterval { get; set; }  // default 1 day

    [Configuration]
    public partial TimeSpan MaxAge { get; set; }  // default 365 days; null-as-Zero means unlimited

    [Configuration]
    public partial int Priority { get; set; }  // default 100

    // Not [State] (plain properties) to avoid self-recording feedback loop
    public long RecordsWritten { get; set; }
    public string? Status { get; set; }

    // IHistoryReader properties
    public abstract DateTimeOffset? OldestRecord { get; }
    public abstract bool SupportsNativeAggregation { get; }
    int IHistoryReader.Priority => Priority;
    TimeSpan IHistoryReader.MaxAge => MaxAge;

    // MinAge defaults to FlushInterval (the buffered-flush blind spot for persistent sinks).
    // InMemoryHistorySink overrides to TimeSpan.Zero because it queries the buffer directly.
    public virtual TimeSpan MinAge => FlushInterval;

    // Abstract — each sink implements storage
    public abstract Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    public abstract Task WriteSnapshotAsync(HistorySnapshot snapshot);
    public abstract Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
    public abstract Task<DateTimeOffset?> GetLatestSnapshotTimeAsync();
    public abstract Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query);
    public abstract Task<IReadOnlyList<AggregatedRecord>> QueryAggregatedAsync(HistoryQuery query);
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
    double? Average,        // time-weighted: sum(value_i * duration_i) / sum(duration_i)
    double? SampleMean,     // count-weighted: sum(value_i) / count
    double? Minimum, double? Maximum,
    double? Sum, long Count,
    JsonElement? First, JsonElement? Last);

record HistoryQuery(
    string SubjectPath,
    IReadOnlyList<string> PropertyNames,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? BucketSize = null,
    AggregationType? Aggregation = null);

enum AggregationType
{
    Average,      // time-weighted; correct for irregularly-spaced state values
    SampleMean,   // count-weighted; raw arithmetic mean of samples
    Minimum, Maximum, Sum, Count, First, Last
}
```

## Move Tracking

Subjects are identified by path (no stable IDs). When a subject moves in the graph, history must remain continuous.

### Write Side (Implemented)

The `HistoryService` detects path changes at runtime:

1. Maintain `Dictionary<IInterceptorSubject, string> _lastKnownPaths` — object reference identity is stable while the app is running
2. On each CQP write handler call, compare current path to last known for each subject
3. Path changed → emit `MoveRecord(timestamp, fromPath, toPath)`, update dictionary
4. Fan out `WriteMovesAsync` to all sinks
5. Lifecycle detach → remove from `_lastKnownPaths`

**Limitation:** Move tracking depends on in-memory object identity. Moves across app restarts cannot be detected (subject IDs are regenerated on restart). In practice this is rarely a problem since moves are manual actions that happen while the app is running.

### Read Side (Implemented)

All queries automatically follow moves. When the sink receives a query for `/Factory/Motor`:

1. **Resolve path chain** from move records — follow backwards: `/Factory/Motor` ← `/Devices/Motor` ← `/OldPath/Motor`. Use a visited set to prevent cycles.
2. **Time-scope each path** — each path in the chain is valid for a specific time range (from move-in to move-out).
3. **Expand query** — instead of querying one path, query each path for its valid time range.
4. **Merge results** chronologically.

```
/OldPath/Motor:    [2026-01-01 ... 2026-03-01]
/Devices/Motor:    [2026-03-01 ... 2026-03-15]
/Factory/Motor:    [2026-03-15 ... now]
```

Same expansion applies to `GetSnapshotAsync` — resolve aliases when reconstructing. The cost of following moves is one extra query to the moves table — if there are no moves (the common case), it returns empty immediately.

## Snapshot Reconstruction

To reconstruct the graph at time T:

1. Find the newest periodic snapshot before T (scan partitions **backwards** from T, stop at first found — typically 1-2 partition reads)
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
| `HomeBlaze.History` | `HistoryService`, `HistorySinkBase`, `InMemoryHistorySink`, `ISubjectPathResolver`, DI extensions, `HistoryRecordExtensions` |
| `HomeBlaze.History.Sqlite` | `SqliteHistorySink` |
| `HomeBlaze.History.TimescaleDb` | `TimescaleDbHistorySink` (Npgsql, hypertables, `drop_chunks` for retention) |
| `HomeBlaze.History.Tests` | HistoryService tests + InMemoryHistorySink tests (move tracking, aggregation, snapshots, edge cases) |
| `HomeBlaze.History.Sqlite.Tests` | SQLite-specific tests (partitioning, schema, retention) |
| `HomeBlaze.History.TimescaleDb.Tests` | TimescaleDB-specific tests via Testcontainers (hypertable schema, native aggregation, chunk drop) |
| `HomeBlaze.AI` (existing) | MCP tools (`get_property_history`, `get_snapshot`, `get_snapshots`). Depends only on Abstractions. |

Note: `StateAttributePathProvider` is moved from `HomeBlaze.AI.Mcp` to `HomeBlaze.Services`. It is a general-purpose path provider used by both history and MCP tools.

### Scale Tiers

| Sink | Subjects | Changes/sec | Dependency |
|---|---|---|---|
| InMemory | ~5,000 | Unlimited | None (testing + fast recent lookups) |
| SQLite | ~5,000 | ~1,000 | Embedded, no server |
| TimescaleDB | 50K+ | 10K+ | External PostgreSQL + TimescaleDB extension |

## In-Memory Sink

Provides two purposes:

1. **Testing** — deterministic unit tests for all edge cases (cross-partition queries, aggregation merging, snapshot reconstruction, move tracking, structural recording) without file system dependencies.
2. **Fast recent lookups** — last N minutes in memory for instant MCP tool queries.

```csharp
[InterceptorSubject]
public partial class InMemoryHistorySink : HistorySinkBase
{
    private readonly List<ResolvedHistoryRecord> _records = new();
    private readonly List<MoveRecord> _moves = new();
    private readonly List<HistorySnapshot> _snapshots = new();

    // MaxAge inherited from HistorySinkBase; default for InMemory is shorter (10 minutes).

    // Recent-tail wrapper: a queryable view onto the HistoryService shared buffer
    // for the [now - MinAge, now] window of other sinks.
    public override TimeSpan MinAge => TimeSpan.Zero;
}
```

Time-based eviction on each write (against `MaxAge`). No partitioning complexity. Default `FlushInterval` of 1s for near-real-time availability.

`InMemoryHistorySink` is a convenience subject. The `HistoryService` always maintains its own shared buffer that covers `[now - bufferLength, now]` regardless of whether this sink is attached; the sink simply gives that buffer a registered query identity for routing.

## SQLite Sink Implementation

### Database Partitioning

One SQLite database file per configurable time interval, plus a separate small DB for move records:

```
history/
  history-2026-W14.db       # week 14 (history + snapshots tables)
  history-2026-W13.db       # week 13
  history-2026-W12.db       # week 12
  history-moves.db          # single small DB for move records
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

-- No secondary index needed — PRIMARY KEY on WITHOUT ROWID table IS the clustered B-tree.
-- Queries on (subject_path, property_name, timestamp) are a single range scan.

-- Periodic graph snapshots (gzipped JSON blobs)
CREATE TABLE snapshots (
    timestamp INTEGER NOT NULL PRIMARY KEY,
    base_path TEXT NOT NULL,             -- "/" for full, "/Demo" for partial
    data BLOB NOT NULL                   -- gzipped JSON of full graph state
);
```

Move records in separate DB:

```sql
-- history-moves.db
CREATE TABLE moves (
    timestamp INTEGER NOT NULL,
    from_path TEXT NOT NULL,
    to_path TEXT NOT NULL,
    PRIMARY KEY (timestamp, from_path)
) WITHOUT ROWID;

CREATE INDEX ix_moves_to ON moves (to_path, timestamp);
```

### Write Path

```csharp
public override Task WriteBatchAsync(
    ReadOnlyMemory<ResolvedHistoryRecord> records)
{
    // 1. Group records by partition (date → DB file)
    // 2. Per partition: single transaction, batch INSERT OR REPLACE
    //    SQLite WAL mode handles concurrent reads during write
    // 3. Numeric fast path: set numeric_value, leave raw_value null
    //    Complex values: serialize to raw_value JSON
}
```

Connection pooling: one open connection per active partition (typically 1-2). Connections closed when partition rolls over.

### Read Path

```csharp
public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(
    HistoryQuery query)
{
    // 1. Resolve path chain from moves DB (always — no-op if no moves exist)
    // 2. Determine which partition DBs cover the time range
    // 3. Query each, UNION results (scoped by path time ranges if following moves)
    // 4. Return raw records
}
```

### Native Aggregation

SQLite handles aggregation directly via SQL. No in-memory row loading.

The default `Average` is time-weighted: each sample contributes proportionally to how long its value held before the next sample (or before the bucket end). `LEAD()` over the property's ordered samples gives the next timestamp; the last sample in a bucket carries forward to the bucket boundary. A "carry sample" from before the bucket's left edge is also needed so the bucket starts with a known value rather than appearing empty until the next change arrives.

```sql
WITH carry AS (
    -- One carry sample per (subject, property): the latest numeric record with
    -- timestamp < @from. This guarantees the first bucket has a starting value
    -- rather than appearing empty until the next change arrives.
    SELECT timestamp, numeric_value
    FROM history
    WHERE subject_path = @path
      AND property_name = @property
      AND value_type IN (1, 2)
      AND timestamp < @from
    ORDER BY timestamp DESC
    LIMIT 1
),
in_range AS (
    SELECT timestamp, numeric_value
    FROM history
    WHERE subject_path = @path
      AND property_name = @property
      AND value_type IN (1, 2)
      AND timestamp BETWEEN @from AND @to
),
samples AS (
    SELECT * FROM carry
    UNION ALL
    SELECT * FROM in_range
),
samples_with_next AS (
    SELECT
        timestamp,
        numeric_value,
        LEAD(timestamp, 1, @rangeEnd) OVER (ORDER BY timestamp) AS next_timestamp
    FROM samples
),
-- A sample's validity interval is [timestamp, next_timestamp). It may cross several
-- buckets, so explode per-bucket via a join against a bucket-boundary helper or by
-- emitting one row per (sample, bucket) using SQLite's generate_series / a recursive
-- CTE. Below we use a simpler approach: clip to the sample's HOME bucket only.
-- For samples that span multiple buckets (a value held for >1 bucket), use the
-- two-pass approach described in the SQLite sink implementation section below.
clipped AS (
    SELECT
        (timestamp / @bucketTicks) * @bucketTicks AS bucket_start,
        numeric_value,
        MAX(timestamp, (timestamp / @bucketTicks) * @bucketTicks) AS effective_start,
        MIN(next_timestamp, ((timestamp / @bucketTicks) + 1) * @bucketTicks) AS effective_end
    FROM samples_with_next
)
SELECT
    bucket_start,
    SUM(numeric_value * (effective_end - effective_start))
        / NULLIF(SUM(effective_end - effective_start), 0)  AS average,
    AVG(numeric_value)        AS sample_mean,
    MIN(numeric_value)        AS minimum,
    MAX(numeric_value)        AS maximum,
    SUM(numeric_value)        AS sum,
    COUNT(*)                  AS count,
    SUM(numeric_value * (effective_end - effective_start))  AS weighted_sum,
    SUM(effective_end - effective_start)                    AS total_duration
FROM clipped
GROUP BY bucket_start
ORDER BY bucket_start;
```

The clipped CTE above only attributes a sample to its starting bucket. When a single sample's validity interval spans multiple buckets (a value held for longer than `@bucketTicks`), the implementation either (a) issues one query per bucket with the carry semantics, or (b) explodes per-bucket via a numbers-table CTE. The implementation plan uses approach (b). First/Last are computed via separate `ORDER BY timestamp [ASC|DESC] LIMIT 1` per bucket and are gated on `AggregationType` so only the requested aggregation does the extra work.

For cross-partition queries (and for stitching the buffer's young-tail leg into a sink's older leg), aggregate per partition in SQL, then merge:

| Aggregation | Merge strategy |
|---|---|
| `Average` (time-weighted) | `(weighted_sum_1 + weighted_sum_2) / (total_duration_1 + total_duration_2)` |
| `SampleMean` | `(sum_1 + sum_2) / (count_1 + count_2)` |
| `Minimum` | `min(min_1, min_2)` |
| `Maximum` | `max(max_1, max_2)` |
| `Sum` | `sum_1 + sum_2` |
| `Count` | `count_1 + count_2` |
| `First` | earlier by timestamp |
| `Last` | later by timestamp |

`AggregatedRecord` carries the internal `weighted_sum` and `total_duration` so the merge has the inputs it needs without re-reading rows. `SupportsNativeAggregation => true` because all aggregations run in SQL.

### Housekeeping

- **Retention:** Whole-partition sweep against `MaxAge`. A partition file is deleted only when its time range falls entirely before `now - MaxAge`. No per-row `DELETE` (would thrash WAL and fragment the file). Sweep runs on the snapshot scheduler, plus a coarse timer (defaults to once an hour).
- **Snapshots:** Periodic full graph serialization to gzipped blob in the current partition's `snapshots` table.
- **WAL checkpoint:** Periodic `PRAGMA wal_checkpoint(TRUNCATE)` to reclaim WAL space.
- **Moves table:** Not swept in v1. Moves are sparse and the table stays small.

## TimescaleDB Sink Implementation

Targets industrial-scale deployments (50K+ subjects, 10K+ changes/sec). Talks to a Postgres server with the TimescaleDB extension via Npgsql. Uses hypertables for property history, plain tables for moves and snapshots.

### Schema

```sql
-- Property change records (hypertable; default 1 day chunks)
CREATE TABLE property_history (
    timestamp     TIMESTAMPTZ NOT NULL,
    subject_path  TEXT        NOT NULL,
    property_name TEXT        NOT NULL,
    value_type    SMALLINT    NOT NULL,
    numeric_value DOUBLE PRECISION,
    raw_value     TEXT,
    PRIMARY KEY (subject_path, property_name, timestamp)
);
SELECT create_hypertable('property_history', 'timestamp',
    chunk_time_interval => INTERVAL '1 day');

-- Moves (plain table; sparse)
CREATE TABLE property_moves (
    timestamp TIMESTAMPTZ NOT NULL,
    from_path TEXT        NOT NULL,
    to_path   TEXT        NOT NULL,
    PRIMARY KEY (timestamp, from_path)
);
CREATE INDEX ix_moves_to ON property_moves (to_path, timestamp);

-- Snapshots (plain table; periodic blobs)
CREATE TABLE property_snapshots (
    timestamp  TIMESTAMPTZ NOT NULL PRIMARY KEY,
    base_path  TEXT        NOT NULL,
    data       BYTEA       NOT NULL   -- gzipped JSON
);
```

Bucket size for `Average` (time-weighted) and `SampleMean` uses `time_bucket(@bucketSize, timestamp)`. Same `LEAD()`-with-clipping pattern as SQLite; the SQL fragment is portable.

### Write Path

Bulk write via `NpgsqlBinaryImporter` (`COPY ... FROM STDIN BINARY`) per batch. Connection pooling per the Npgsql defaults. One transaction per batch. Conflict policy: `ON CONFLICT DO NOTHING` (paths × property × timestamp must be unique; deduplication already happened upstream in the CQP stage).

### Read Path

Same shape as SQLite: resolve path chain from `property_moves`, then run the raw or aggregated query against the hypertable. TimescaleDB automatically restricts the scan to relevant chunks based on `WHERE timestamp BETWEEN ...`. `time_bucket()` replaces the manual bucket-start math.

### Housekeeping

- **Retention:** `SELECT drop_chunks('property_history', older_than => now() - @maxAge)` on a timer (defaults to once an hour). Cheapest possible retention; entire chunks are dropped atomically.
- **Snapshots:** Same as SQLite (gzipped JSON blob into `property_snapshots`).
- **Moves table:** Not swept in v1.

### What v1 explicitly does NOT use

- **Continuous aggregates.** Would require pinning bucket sizes; conflicts with per-query `bucketSize?`. Revisit in v1.1.
- **Native compression.** TimescaleDB compresses old chunks at 90%+ ratios. Adds a compression policy and changes how chunks are queried. Defer to v1.1.
- **TimescaleDB-specific aggregates** like `time_weight()` (in `timescaledb_toolkit`). The portable `LEAD()` formulation works against plain PostgreSQL too if we ever add a non-TimescaleDB sink.

## MCP Tools

### get_property_history

Query raw or aggregated property values over a time range.

- **Input:**
  - `subjectPath` (string)
  - `propertyNames` (string[])
  - `from`, `to` (ISO-8601 timestamps)
  - `bucketSize` (ISO-8601 duration; required if `aggregation` is set)
  - `aggregation` (enum; one of `"average"`, `"sampleMean"`, `"minimum"`, `"maximum"`, `"sum"`, `"count"`, `"first"`, `"last"`)
    - `"average"` is **time-weighted**: each sample contributes proportionally to how long its value held. Correct for irregularly-spaced state values.
    - `"sampleMean"` is the **count-weighted** arithmetic mean of the samples in the bucket. Use this only when the samples themselves are the unit of analysis; for state values it is misleading.
- **Output (raw):** `{ "records": { "Temperature": [{"t":"...","v":42.5}, ...] } }`
- **Output (aggregated):** only the requested aggregation field is populated per bucket. Internal merge fields (`weightedSum`, `totalDurationTicks`) are stripped from MCP responses.

```json
{
  "buckets": {
    "Temperature": [
      { "from": "2026-06-01T10:00:00Z", "to": "2026-06-01T11:00:00Z",
        "average": 21.3, "sampleMean": 60.0,
        "minimum": 20.0, "maximum": 100.0,
        "sum": 120.0, "count": 2,
        "first": 20.0, "last": 100.0 }
    ]
  }
}
```

### get_snapshot

Reconstruct graph state at a point in time.

- **Input:** `path`, `time`
- **Output:** `{ "time":"...", "subjects": { "/Demo/Conveyor": { "CurrentSpeed": 42.5, ... } } }`

### get_snapshots

Snapshot series over a time range.

- **Input:** `path`, `from`, `to`, `interval`
- **Output:** `{ "snapshots": [{ "time":"...", "subjects": {...} }, ...] }`

All three automatically follow moves — path renames are resolved transparently.

### Result Limits

MCP tools enforce per-request limits to prevent expensive queries from overwhelming the system (internal code is unconstrained):

| Tool | Limit |
|------|-------|
| `get_property_history` (raw) | Max 10,000 records |
| `get_property_history` (aggregated) | Max 1,000 buckets |
| `get_snapshot` | Single snapshot — no limit needed |
| `get_snapshots` | Max 100 snapshots per request |

When a limit is hit, the response includes a truncation indicator so the AI agent can narrow its query.

## Future Sink Backends

In v1: InMemory, SQLite, TimescaleDB.

Deferred (not in v1):

- **Plain PostgreSQL / QuestDB.** Both speak the PostgreSQL wire protocol but lack TimescaleDB's hypertables, `time_bucket()`, and `drop_chunks()`. The v1 TimescaleDB sink leans on those features for retention and aggregation performance; a portable PG sink would either re-implement them in stored procedures or accept much slower queries. Revisit if a deployment specifically asks for QuestDB.
- **Continuous aggregates and compression (TimescaleDB).** Tempting because they make wide-range aggregate queries near-free and shrink storage substantially. Continuous aggregates require fixed bucket sizes chosen ahead of time, which conflicts with the per-query `bucketSize?` parameter. Defer to v1.1 once real workloads inform bucket-size choices.
- **Cross-sink auto-tier migration.** A user can compose tiers manually (InMemory + SQLite + TimescaleDB attached simultaneously) but the service does not migrate old data between sinks. Each sink's `MaxAge` simply enforces a wholesale drop boundary.
- **InfluxDB.** Ruled out. v3 is proprietary / cloud-only, v2 has a restrictive license, Flux is deprecated.

See the architecture doc (`src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/history.md`) for the broader scale-tier picture and the ruled-out comparison.
