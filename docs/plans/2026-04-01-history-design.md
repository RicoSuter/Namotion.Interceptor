# History System Design

## Problem

HomeBlaze tracks every property change in real-time but has no way to look back. Users need to query historical values, reconstruct past graph states, and run aggregations over long time ranges. The system must work on low-powered devices (Raspberry Pi / SD cards) with many changes over long retention periods.

## Constraints

- Fast writes, slow reads acceptable
- Low-powered devices: sequential I/O only, minimize random writes
- Many changes over very long time (months/years)
- Any value type (numbers, strings, booleans, complex objects)
- Subjects identified by path (no stable IDs — subject IDs are in-memory only, regenerated on restart)
- Must support multiple simultaneous sink implementations (in-memory, SQLite, PostgreSQL)

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
  "RetentionDays": 365,
  "Priority": 50
}
```

`HistoryService` discovers sinks via lifecycle attach/detach events — no polling, instant discovery and cleanup.

### Multiple Sinks

Multiple sinks run simultaneously (e.g., in-memory for fast recent queries + SQLite for long-term). Each sink is independent — a failing sink doesn't block others.

For reads, `HistoryService` picks the best reader:
- Aggregation query → lowest priority reader with `SupportsNativeAggregation` covering the time range
- Raw query → lowest priority reader covering the time range
- Fallback → next reader in priority order

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
    public partial int RetentionDays { get; set; }  // default 365

    [Configuration]
    public partial int Priority { get; set; }  // default 100

    // Not [State] — plain properties to avoid self-recording feedback loop
    public long RecordsWritten { get; set; }
    public string? Status { get; set; }

    // IHistoryReader properties
    public abstract DateTimeOffset? OldestRecord { get; }
    public abstract bool SupportsNativeAggregation { get; }
    int IHistoryReader.Priority => Priority;

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
    double? Average, double? Minimum, double? Maximum,
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
    Average, Minimum, Maximum, Sum, Count, First, Last
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
| `HomeBlaze.History.Tests` | HistoryService tests + InMemoryHistorySink tests (move tracking, aggregation, snapshots, edge cases) |
| `HomeBlaze.History.Sqlite.Tests` | SQLite-specific tests (partitioning, schema, retention) |
| `HomeBlaze.AI` (existing) | MCP tools (`get_property_history`, `get_snapshot`, `get_snapshots`) — depends only on Abstractions |

Note: `StateAttributePathProvider` is moved from `HomeBlaze.AI.Mcp` to `HomeBlaze.Services` — it's a general-purpose path provider used by both history and MCP tools.

Later:
| `HomeBlaze.History.PostgreSql` | TimescaleDB / QuestDB sink for industrial scale |

### Scale Tiers

| Sink | Subjects | Changes/sec | Dependency |
|---|---|---|---|
| InMemory | ~5,000 | Unlimited | None (testing + fast recent lookups) |
| SQLite | ~5,000 | ~1,000 | Embedded, no server |
| TimescaleDB (planned) | 50K+ | 10K+ | External PostgreSQL server |

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

    [Configuration]
    public partial TimeSpan MaxRetention { get; set; }  // default 10 minutes
}
```

Time-based eviction on each write. No partitioning complexity — just store, query, and evict.

Default `FlushInterval` of 1s for near-real-time availability.

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

SQLite handles aggregation directly via SQL — no in-memory row loading:

```sql
SELECT
    (timestamp / @bucketTicks) * @bucketTicks AS bucket_start,
    AVG(numeric_value) AS average,
    MIN(numeric_value) AS minimum,
    MAX(numeric_value) AS maximum,
    SUM(numeric_value) AS sum,
    COUNT(*) AS count,
    -- First/Last require subqueries or window functions
    (SELECT numeric_value FROM history h2
     WHERE h2.subject_path = @path AND h2.property_name = @property
       AND h2.timestamp >= bucket_start AND h2.timestamp < bucket_start + @bucketTicks
     ORDER BY h2.timestamp ASC LIMIT 1) AS first_value,
    (SELECT numeric_value FROM history h2
     WHERE h2.subject_path = @path AND h2.property_name = @property
       AND h2.timestamp >= bucket_start AND h2.timestamp < bucket_start + @bucketTicks
     ORDER BY h2.timestamp DESC LIMIT 1) AS last_value
FROM history
WHERE subject_path = @path
  AND property_name = @property
  AND timestamp BETWEEN @from AND @to
GROUP BY bucket_start
ORDER BY bucket_start;
```

For cross-partition queries, aggregate per partition in SQL, then merge:
- Average: weighted by count (`(sum1 + sum2) / (count1 + count2)`)
- Minimum: min of minimums
- Maximum: max of maximums
- Sum: sum of sums
- Count: sum of counts
- First: earliest by timestamp
- Last: latest by timestamp

`SupportsNativeAggregation => true` because all aggregations run in SQL.

### Housekeeping

- **Retention:** Delete partition DB files older than `RetentionDays`
- **Snapshots:** Periodic full graph serialization to gzipped blob in the current partition's `snapshots` table
- **WAL checkpoint:** Periodic `PRAGMA wal_checkpoint(TRUNCATE)` to reclaim WAL space

## MCP Tools

### get_property_history

Query raw or aggregated property values over a time range.

- **Input:** `subjectPath`, `propertyNames[]`, `from`, `to`, `bucketSize?`, `aggregation?`
- **Output (raw):** `{ "records": { "Temperature": [{"t":"...","v":42.5}, ...] } }`
- **Output (aggregated):** `{ "buckets": { "Temperature": [{"from":"...","to":"...","average":42.5,"minimum":40.0,...}] } }`

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

See the architecture doc (`src/HomeBlaze/HomeBlaze/Data/Docs/architecture/design/history.md`) for the full comparison of future sink backends (TimescaleDB, QuestDB), ruled-out options (InfluxDB), and scale tiers.
