---
title: Time-Series History
navTitle: History
status: Implemented
---

# Time-Series History Design

## Overview

The history system records property changes over time, enabling historical queries, trend analysis, and AI-driven insights. It runs on low-powered devices (Raspberry Pi / SD cards) with many changes over long retention, and scales up to industrial deployments.

Each history store is a standalone subject: an `[InterceptorSubject]` `BackgroundService` with its own `ChangeQueueProcessor`, recording and serving history independently. There is no central orchestrator. This follows the connector pattern (OPC UA, MQTT, WebSocket): stores are configured by dropping a JSON subject into the graph, discovered through the registry, and queried by a stateless cross-store merger.

**Status.** v1 ships the InMemory and SQLite stores, the cross-store merger, the property-history chart dialog, and the `get_property_history` MCP tool. The TimescaleDB industrial tier is designed against the same abstractions and is the planned third tier. Snapshots and structural recording are the v1.1 layer (designed, deferred).

## Architecture

Each store independently subscribes to the change pipeline, filters with the same eligibility predicate, deduplicates within its own buffer window, and records to its backend. Stores expose a coverage window (`CurrentCoverage`) and a `Priority`; a stateless merger takes the set of `IHistoryStore` (HomeBlaze supplies it from the registry's `KnownSubjects`) and stitches their results.

### Recent-tail coverage

The InMemory store (priority 100, `CurrentCoverage.To = now`) answers the most recent samples at full resolution while persistent stores trail "now" by roughly their flush interval. Persistent stores (SQLite, TimescaleDB) set `CurrentCoverage.To` to their last committed sample's timestamp, so the merger routes the live edge to InMemory, and set `CurrentCoverage.From` to their oldest retained sample's timestamp (not the retention horizon), so a freshly started store never claims a range it has no data for.

To avoid a blind window between InMemory eviction and a persistent store's commit, the sizing constraint is `InMemory.MaxAge >= 2 * FlushInterval` of any companion persistent store. The default configs satisfy it.

### Package structure

| Package | Role |
|---|---|
| `HomeBlaze.History.Abstractions` | `IHistoryStore`, the query/result records, `HistoryAggregations`, `HistoryEligibility`, `HistoryColumns`, `BucketAlignment`, `HistoryAggregationNotSupportedException`. Pure interfaces, DTOs, and helpers only. |
| `HomeBlaze.History` | The stateless cross-store merger (`HistoryStoreMerger.QueryHistoryAsync`). |
| `HomeBlaze.History.InMemory` | `InMemoryHistoryStore` engine + `InMemoryHistoryStoreSubject` adapter. Ring buffer per property path. Priority 100. |
| `HomeBlaze.History.Sqlite` | `SqliteHistoryStore` engine + `SqliteHistoryStoreSubject` adapter. Partitioned database files, typed columns, native SQL aggregation. Priority 50. |
| `HomeBlaze.History.Blazor` | The cross-store property-history chart dialog. |
| `*.Blazor` (per store) | One edit component per store. |
| `HomeBlaze.History.TimescaleDb` (planned) | `TimescaleDbHistoryStore` engine + adapter. Npgsql binary `COPY`, hypertable storage, `drop_chunks` retention. Priority 10. |

The `get_property_history` MCP tool lives in `HomeBlaze.AI`. Target framework `net10.0` throughout.

### Engine vs subject

Each store is split into a graph-free engine (the public, clean-named `IHistoryStore`: `InMemoryHistoryStore`, `SqliteHistoryStore`) that operates only on path strings and typed values, and a thin `[InterceptorSubject]` adapter (`InMemoryHistoryStoreSubject`, `SqliteHistoryStoreSubject`) that owns the `ChangeQueueProcessor`, the cached path resolution, and write-side move detection, delegating all storage and querying to the engine. The adapter also implements `IHistoryStore` (by delegation) so registry discovery via `KnownSubjects.OfType<IHistoryStore>()` works. This split keeps a later lift into a generic library mechanical: the engine is already decoupled from the graph.

## Abstractions

```csharp
public interface IHistoryStore
{
    int Priority { get; }                               // higher = preferred for overlapping ranges
    HistoryCoverage CurrentCoverage { get; }
    IReadOnlySet<string> SupportedAggregations { get; }

    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);

    // Most recent sample at or before asOf for the property path (following move chains), or null.
    // Serves TimeWeightedAverage integration and Last LOCF gap-fill.
    ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken);
}

public readonly record struct HistoryCoverage(DateTimeOffset From, DateTimeOffset To);

public record HistoryQuery(
    string PropertyPath, DateTimeOffset From, DateTimeOffset To,
    TimeSpan? Bucket = null,                             // null => raw query
    string Aggregation = HistoryAggregations.Last,
    int MaxPoints = 10_000,
    HistoryPoint? CarrySeed = null);                     // value held entering From, supplied by the merger

public record HistoryPoint(DateTimeOffset Timestamp, double? Number, JsonElement? Json);
public record HistorySeries(string PropertyPath, ImmutableArray<HistoryPoint> Points, bool Truncated);
```

A single `QueryAsync` serves both raw (`Bucket == null`) and bucketed queries. Every store returns at most `MaxPoints` results, ascending by timestamp, representing the newest samples or buckets, so the merger can compose them.

### Aggregations

PascalCase string identifiers, not a closed enum: `Last`, `First`, `SampleAverage`, `TimeWeightedAverage`, `Minimum`, `Maximum`, `Sum`, `Count`, `StandardDeviation`. `HistoryAggregations.AlwaysAvailable` is `{ Last, Count }`, which the eligibility check skips. The MCP boundary accepts case-insensitive input and normalizes; internal equality uses `StringComparer.Ordinal`. `TimeWeightedAverage` weights each sample by how long its value held and is the UI default for numeric properties (labelled "Average"); `SampleAverage` is the count-weighted mean (labelled "Sample Average").

### Eligibility

`HasHistory()` is the single source of truth for whether a property is recorded and whether the UI offers the history action. It requires the `[State]` attribute, excludes subject-bearing properties (`CanContainSubjects`, deferred to v1.1), and accepts only recordable types: `double`/`float` (value_double), the integer types and `bool` (value_long), and `decimal`/`string`/`enum` (value_json).

### Column dispatch

`HistoryColumns.GetValueColumnFor(Type)` is the single source of truth for routing a value into one of three typed columns: `value_long`, `value_double`, `value_json`. Typed columns preserve `long` exactly and keep `decimal` lossless. They map cleanly across backends: `bigint` / `double precision` / `jsonb` in PostgreSQL, `INTEGER` / `REAL` / `TEXT` in SQLite, and a typed sample in memory. A `ulong` above `long.MaxValue` spills to `value_json`; read paths COALESCE across both columns.

### Bucket alignment

All backends produce buckets at identical timestamps for the same `(bucket size, sample timestamps)` using the epoch-anchored formula matching Postgres `time_bucket`, so the merger never interleaves duplicates:

```csharp
public static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan bucket)
{
    var ticksFromEpoch = (ts - DateTimeOffset.UnixEpoch).Ticks;
    return DateTimeOffset.UnixEpoch.AddTicks((ticksFromEpoch / bucket.Ticks) * bucket.Ticks);
}
```

## Recording path

Each store's adapter constructs a `ChangeQueueProcessor` (own `BufferTime` coalesce window, opt-in `maxQueueDepth` bounded queue, oldest dropped on overflow) and runs its process loop. For each change in a flushed batch it resolves the property's canonical path (cached per property, recomputed only on a structural lifecycle event), routes the value into the correct column, and records it. Oversize strings (over `MaxJsonSize`, default 8 KB) record a `{ "$oversize": true, "size": n }` placeholder so the timeline is preserved.

## Move tracking

Subjects are identified by canonical path. When a subject is renamed or reparented while the app runs, the adapter detects it on the path cache (a recomputed path that differs from the cached one) and writes a `MoveRecord(changeTimestamp, fromPath, toPath)` to the store's own moves table. Querying follows the chain backwards through the moves table inside `QueryAsync` / `GetSampleAtOrBeforeAsync`, time-scoping each path to its valid interval and returning results under the queried path, so the merger never has to know moves exist. Move tracking is runtime-only (in-memory identity); moves across restarts are not detected.

## Query path

Two shapes, dispatched on whether `Bucket` is null; both honor the newest-N contract and return ascending by timestamp. Empty buckets are explicit `null` entries (not absent): `Count` returns 0 (a fact), `Last` and `TimeWeightedAverage` carry the held value, other aggregations return null and the chart shows a gap. Numeric aggregations on a `value_json`-stored property (decimal, string, enum) raise `HistoryAggregationNotSupportedException`.

## Cross-store merge

The merger (`HistoryStoreMerger.QueryHistoryAsync`, with an `ISubjectRegistry` convenience overload and a multi-path fan-out overload) is a stateless function over the store set: a query-type-specific planner plus a shared executor.

- `EnsureEligibility`: every part of `[From, To]` must be servable by some store supporting the requested aggregation (`AlwaysAvailable` aggregations skip the check); otherwise `HistoryAggregationNotSupportedException` carries the `Available` set.
- Raw planner: higher-priority stores claim their range first; lower-priority stores fill the remaining gaps (coverage subtraction).
- Bucketed planner: each bucket is assigned to a single store, the highest-priority one that supports the aggregation and whose `CurrentCoverage` fully contains the bucket. Consecutive same-store buckets group into one ranged sub-query. No bucket is ever computed from two stores, which sidesteps the average-of-averages problem.
- Sequential-budget executor: stores queried in priority order, each receiving the remaining budget; newest-first; dedup on identical timestamps (higher priority wins); `Truncated` set honestly.

### Carried-value resolution

For `Last` and `TimeWeightedAverage`, an empty bucket equals the value already held when the bucket began. No single store is guaranteed to hold that prior sample, so the merger resolves the carry: it seeds the value held entering the whole range with a priority-ordered `GetSampleAtOrBeforeAsync(path, From)` walk, then threads the running carried value as the `CarrySeed` of each store-segment's bucketed sub-query oldest to newest. The persistent tier seeds the held value and InMemory's live-edge buckets carry it forward instead of rendering a spurious gap.

## Time-weighted average

Time-weighted average is supported by every numeric-capable store via a portable `LEAD()`-over-ordered-samples implementation that clips each sample's validity interval to the bucket and, for the final sample, to the bucket end. SQLite sums `weighted_sum` / `total_duration` across overlapping partition files in a single ordered scan so a bucket that straddles a partition boundary is not double-counted. InMemory uses step/LOCF integration with the same look-back semantics. The TimescaleDB toolkit, when present, replaces the portable query with `average(time_weight('locf', ts, value))` as a transparent performance fast-path; it never changes which aggregations are available.

A cross-store parity battery (`HomeBlaze.History.Parity.Tests`) feeds identical samples to every store and asserts identical results across the edge cases (empty / single-sample / boundary / look-back / partition-straddle buckets, all aggregations, move chains, oversize, value routing). This equivalence is what makes the unified merger sound.

## Store implementations

- **InMemoryHistoryStore (priority 100).** `ConcurrentDictionary<string, PropertyBuffer>` keyed by path; each buffer is an array-backed ring. `MaxAge` (default 60 s) evicts old samples; `MaxPointsPerProperty` (default 1000) caps a runaway property. Records are immediately queryable; nothing survives restart (a hot buffer / dev / test store).
- **SqliteHistoryStore (priority 50).** The edge / Raspberry-Pi tier. One `WITHOUT ROWID` `(path, ts)` database file per configurable interval (Daily / Weekly / Monthly), plus a small moves database; WAL mode; batched `INSERT OR REPLACE` per partition; retention sweeps whole partition files older than `now - MaxAge`. All connection access is serialized through a single re-entrant lock.
- **TimescaleDbHistoryStore (priority 10, planned).** The industrial tier. Npgsql native (no ORM), hypertable, daily chunks, batched binary `COPY` per `FlushInterval`, `drop_chunks` retention, idempotent schema bootstrap, a toolkit probe driving the TWA fast-path, and a `CurrentCoverage.To` high-water-mark frozen during outages. Genuinely async, so it uses an async gate rather than a re-entrant lock.

## UI

The property-history chart dialog (`HomeBlaze.History.Blazor`) is reachable from any `[State]` property whose `HasHistory()` is true when at least one `IHistoryStore` exists. It offers range presets (1h / 6h / 24h / 7d / 30d / custom), an auto bucket (about range / 200 rounded to a sane interval), and an aggregation dropdown gated by column type and cumulativeness (cumulative counters offer only Last / First / Minimum / Maximum / Count; JSON columns offer Last / First / Count; numeric columns offer the full set, intersected with the union of the stores' `SupportedAggregations`). It renders a `MudTimeSeriesChart`, splitting the series at null entries so gaps render as visual breaks; carry-dependent aggregations draw a continuous line. Non-numeric results fall back to a table. Each store also ships an edit component for its settings.

## MCP tool

`get_property_history` in `HomeBlaze.AI` queries one or more canonical `[State]` property paths over a range, raw or bucketed, with a chosen aggregation:

| Parameter | Required | Default | Notes |
|---|---|---|---|
| `paths` | yes | | one or more canonical property paths |
| `from` | yes | | ISO 8601; bare timestamps treated as UTC |
| `to` | no | now | ISO 8601 |
| `bucket` | no | null (raw) | for example `5m`, `30s`, `1h`, `7d` |
| `aggregation` | no | `Last` | case-insensitive match against `HistoryAggregations` |

The response is a per-path map, each entry carrying a `value_type` hint (number / string / boolean / enum), the `points` array (null entries for gaps), and `truncated`. Unknown or non-servable aggregation returns a structured error with the `available` set; empty results and unknown paths are not errors. The tool reuses the cross-store merger's multi-path fan-out overload.

## Configuration summary

| Knob | InMemory | SQLite | TimescaleDB (planned) |
|---|---|---|---|
| `Priority` | 100 | 50 | 10 |
| `MaxAge` (retention) | 60 s | 365 d | 365 d |
| `FlushInterval` | n/a (direct) | 10 s | 5 s |
| `BufferTime` (coalesce) | 250 ms | 250 ms | 250 ms |
| `PartitionInterval` | n/a | Weekly | n/a (1-day chunks) |
| `MaxPointsPerProperty` | 1000 | n/a | n/a |
| `MaxJsonSize` | 8 KB | 8 KB | 8 KB |

## Known limitations

- Changing a property's declared type shifts the read column; old-type samples become invisible to the new query path.
- Up to `FlushInterval` of samples are lost on a hard crash; graceful shutdown drains.
- InMemory-only loses history on restart (a hot buffer, not a production substitute).
- When `bucket_size > InMemory.MaxAge`, the rightmost bucket may omit up to `FlushInterval` of samples; raise `MaxAge` for pixel-perfect live edges.
- Per-property resolution is bounded by `BufferTime` (the recorder coalesces to the latest value per property per window).
- Move tracking is runtime-only (in-memory identity).

## Roadmap

v1.1 (designed, deferred) adds snapshots (periodic whole-graph capture plus backwards-scan-and-replay reconstruction) and structural recording (subject-bearing `[State]` properties recorded as path references in `value_json`), plus `get_snapshot` / `get_snapshots` MCP tools. Both build additively on the v1 schema: a new table plus a widened eligibility predicate, with nothing in v1 undone.
