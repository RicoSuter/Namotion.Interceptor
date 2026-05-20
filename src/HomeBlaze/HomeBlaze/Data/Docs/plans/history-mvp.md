---
title: History MVP
navTitle: History MVP
status: Planned
---

# Time-Series History MVP Plan

**Status: Planned**

**Companion to:** [History Design](../architecture/design/history.md) (architectural target).
This plan narrows that target to a first shippable slice.

## Problem

The architecture defines history as a plugin-based subsystem with multiple store types and tiered query merging. Nothing of it exists yet. The MVP delivers an end-to-end slice: one production store (TimescaleDB), one dev/test store (in-memory), a property-centric chart dialog in the UI, and one MCP tool. Just enough to prove the abstraction, recording path, and query path under real load, while leaving the seams in place for the tiered model the architecture document describes.

## Scope

**In:**

- `IHistoryStore` abstraction, query types, eligibility predicate.
- `InMemoryHistoryStore` for tests, samples, and the hot buffer that fills the Timescale write-batch lag.
- `TimescaleDbHistoryStore` for production, with Npgsql binary `COPY` writes and hypertable storage.
- Store edit components in the Blazor UI.
- A property history dialog reachable from any historizable `[State]` property.
- `get_property_history` MCP tool in `HomeBlaze.Mcp`.
- Integration tests against a real TimescaleDB container via Testcontainers.

**Out (deferred post-MVP):**

- Additional stores (file-based, Influx, Kusto, S3 archive).
- Dashboard history tile.
- Multi-property compare in the chart.
- Per-property opt-in or opt-out attributes (`[Historize]` / `[NoHistory]`).
- Store-level include/exclude filter UI.
- CSV/JSON export from the dialog.
- Streaming `IAsyncEnumerable` query path.
- Cross-instance history sync beyond what the existing WebSocket topology already provides as a side effect.

## Package Structure

| Package | Role |
|---|---|
| `HomeBlaze.History.Abstractions` | `IHistoryStore`, `HistoryQuery`, `HistoryPoint`, `HistorySeries`, `HistoryCoverage`, `HistoryAggregation`, `HistoryEligibility` extension methods, `HistoryQueryExtensions.QueryHistoryAsync` cross-store helper. No implementation. References `Namotion.Interceptor.Registry`. |
| `HomeBlaze.History.Abstractions.Tests` | Unit tests for `HistoryEligibility.HasHistory` and the `QueryHistoryAsync` merge helper, using fake `IHistoryStore` implementations. |
| `HomeBlaze.History.InMemory` | `InMemoryHistoryStore` subject. Ring buffer per property path. Priority 100. Intended for dev, tests, samples, and as the hot buffer covering the Timescale flush window. |
| `HomeBlaze.History.InMemory.Tests` | Unit tests for the in-memory store: recording, retention, raw and bucketed queries, oversize placeholder, refused types, `Coverage` correctness. |
| `HomeBlaze.History.InMemory.Blazor` | Edit component for the in-memory store. |
| `HomeBlaze.History.TimescaleDb` | `TimescaleDbHistoryStore` subject. Npgsql client, hypertable bootstrap, batched binary `COPY` writes. Priority 10. |
| `HomeBlaze.History.TimescaleDb.Blazor` | Edit component for the Timescale store. |
| `HomeBlaze.History.TimescaleDb.Tests` | Integration tests using Testcontainers with the `timescale/timescaledb` image. |

Target framework: `net10.0` for everything, matching the rest of the HomeBlaze tree.

## Abstractions

```csharp
public interface IHistoryStore : IInterceptorSubject
{
    int Priority { get; }
    HistoryCoverage Coverage { get; }
    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);
}

public readonly record struct HistoryCoverage(
    DateTimeOffset From,
    DateTimeOffset To);

public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    HistoryAggregation Aggregation = HistoryAggregation.Last,
    int MaxPoints = 10_000);

public enum HistoryAggregation { Last, Average, Minimum, Maximum, Sum, Count }

public record HistoryPoint(
    DateTimeOffset Timestamp,
    double? Number,
    JsonElement? Json);

public record HistorySeries(
    string PropertyPath,
    ImmutableArray<HistoryPoint> Points,
    bool Truncated);
```

### Coverage contract

`Coverage` returns the range a store guarantees it can answer queries over. A query passed to `QueryAsync` is always inside this range, enforced by the merger. Returning a tighter coverage than the store physically holds is safe. Returning a wider coverage is a bug.

### Eligibility predicate

`HasHistory()` is the single source of truth for whether a property is recorded and whether the UI offers the history action. Both stores and the UI gate on it.

```csharp
public static class HistoryEligibility
{
    public static bool HasHistory(this RegisteredSubjectProperty property)
    {
        if (!property.IsState) return false;
        if (property.HasChildren) return false;
        return IsRecordableType(property.Type);
    }

    public static bool IsRecordableType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(double) || t == typeof(float)) return true;  // value_double
        if (IsBigIntCompatible(t)) return true;                       // value_long
        if (t == typeof(decimal)) return true;                        // value_json (lossless)
        if (t == typeof(string)) return true;                         // value_json
        if (t.IsEnum) return true;                                    // value_json (enum name)
        return false;                                                 // complex types deferred
    }

    static bool IsBigIntCompatible(Type t) =>
        t == typeof(long)   || t == typeof(int)   || t == typeof(short) ||
        t == typeof(sbyte)  || t == typeof(byte)  ||
        t == typeof(ushort) || t == typeof(uint)  || t == typeof(ulong) ||
        t == typeof(bool);
}
```

The allow-list is deliberately tight for MVP: numerics, bool, string, and enums. Sub-subjects and subject collections are excluded structurally. Records, value objects, byte arrays, streams, and other complex types are not recorded yet; supporting them is a noted follow-up.

### Column dispatch

`ValueColumnFor` is the single source of truth used by both the write path (to route a value into the correct column) and the read path (to build the column-targeted SQL). Keeping write and read in agreement is the whole point of centralising it.

```csharp
public enum ValueColumn { Long, Double, Json }

public static class HistoryColumns
{
    public static ValueColumn ValueColumnFor(Type propertyType)
    {
        var t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (t == typeof(double) || t == typeof(float)) return ValueColumn.Double;
        if (IsBigIntCompatible(t)) return ValueColumn.Long;
        return ValueColumn.Json;  // decimal, string, enum
    }

    // For ulong properties only: a single row's storage depends on whether the
    // value exceeds long.MaxValue. Read paths must coalesce across both columns.
    public static bool IsUlongProperty(Type propertyType) =>
        (Nullable.GetUnderlyingType(propertyType) ?? propertyType) == typeof(ulong);
}
```

`ValueColumnFor(typeof(ulong))` returns `Long` (the primary column for ulong properties). On the write path, the store performs an additional per-value check: if `value > long.MaxValue`, the row goes to `value_json` instead. On the read path, when `IsUlongProperty(...)` is true, the SQL builder produces a COALESCE-aware variant (see Query Path) so aggregations include rows from both columns. This is the only type that needs the dual-column read path; all others have a static column for life.

## Recording Path

Each store is a `BackgroundService` subject with its own `ChangeQueueProcessor`. The change pipeline already provides bounded queue depth, oldest-dropped backpressure, and per-batch coalescing of repeated writes within the flush window. The store inherits all of that.

For each change in a batch:

```csharp
if (!change.Property.HasHistory()) continue;
Record(change);
```

`Record` routes the value:

| Value type | Column | Notes |
|---|---|---|
| `double` / `float` | `value_double` | `double precision` |
| `int` / `uint` / `short` / `ushort` / `byte` / `sbyte` / `long` / `bool` | `value_long` | `bigint`, `bool` as 0/1 |
| `ulong` if `<= long.MaxValue` | `value_long` | Range check at write time |
| `ulong` if `> long.MaxValue` | `value_json` | Rare overflow case |
| `decimal` | `value_json` | JSON number, lossless via jsonb's internal `numeric` |
| `string` | `value_json` | JSON string |
| `enum` | `value_json` | JSON string, enum name |
| `null` | all columns `NULL` | Explicit null is meaningful |

### Oversize and refused values

Within the allow-list, only `string` is unbounded in length. The store writes serialized JSON into a pooled buffer with a hard cap (default 8 KB, configurable per store as `MaxJsonSize`). On overflow the store writes a small placeholder row instead:

```json
{ "$oversize": true, "size": 73218 }
```

A placeholder preserves the timeline. `Last` still surfaces a marker with the original size. Numeric aggregations skip it because the numeric value columns are null. The store exposes an `OversizeCount` `[State]` property so operators can see it climb.

Numerics, bool, and enums are bounded by their type; they never trigger the cap. Types outside the allow-list are blocked by `HasHistory()` before reaching the store.

## Query Path

Two paths, dispatched on whether `Bucket` is null. In both, the Timescale store inspects the property's declared type via the registry and builds a single-column query targeting `value_long`, `value_double`, or `value_json`. No COALESCE in the aggregate expression; the planner sees direct column references.

The store uses `HistoryColumns.ValueColumnFor(propertyType)` (defined in the abstractions) to pick the column. For ulong properties, the read path additionally consults `IsUlongProperty` and emits the COALESCE variant described below.

### Raw path

Return individual samples within `[From, To]`. Example for a `long`-typed property:

```sql
select ts, value_long::double precision as number, null::jsonb as json
from property_history
where path = $1 and ts >= $2 and ts < $3
order by ts asc
limit $4 + 1;
```

For a `string`-typed property, `value_json` is selected and `number` is `NULL`. The `+1` lets the consumer detect overflow without a separate `count(*)`; if the result hits `MaxPoints + 1` rows, drop the last and set `Truncated = true`. The in-memory implementation does the same in a linear scan over the property's sorted sample list, bounded by binary search on the range endpoints.

### Bucketed path

One row per bucket, computed server-side via `time_bucket`. The aggregation function targets the property's column directly:

```sql
select time_bucket($1::interval, ts) as bucket,
       <agg>(<column>)               as number,
       <last_json_expr>              as json
from property_history
where path = $2 and ts >= $3 and ts < $4
group by bucket
order by bucket asc
limit $5 + 1;
```

`<column>` is the result of `HistoryColumns.ValueColumnFor(propertyType)`. `<agg>` switches per `HistoryAggregation`:

| Aggregation | SQL fragment |
|---|---|
| `Last` | `last(<column>, ts)` |
| `Average` | `avg(<column>)` |
| `Minimum` | `min(<column>)` |
| `Maximum` | `max(<column>)` |
| `Sum` | `sum(<column>)` |
| `Count` | `count(*)` returned as the `number` field |

`<last_json_expr>` is `last(value_json, ts)` when aggregation is `Last` and the column is `value_json`; otherwise `NULL`. Bigint aggregates cast to `double precision` on the way out so the C# `HistoryPoint.Number` field stays a unified `double?`. Buckets with no rows are absent. Bucket cap defaults to 1000, same `+1 / Truncated` pattern.

### `ulong` read path

For ulong properties whose values can straddle `long.MaxValue`, the read path uses a COALESCE-aware variant so aggregates see both columns. Example for bucketed `Average`:

```sql
select time_bucket($1::interval, ts) as bucket,
       avg(coalesce(value_long::numeric,
                    (value_json #>> '{}')::numeric))::double precision as number,
       null::jsonb as json
from property_history
where path = $2 and ts >= $3 and ts < $4
group by bucket
order by bucket asc
limit $5 + 1;
```

This branch only fires when `HistoryColumns.IsUlongProperty(propertyType)` is true. All other property types use the single-column SQL shown above with no COALESCE overhead.

### Aggregation support per column

| Column | `Last` | `Count` | `Average` | `Minimum` | `Maximum` | `Sum` |
|---|---|---|---|---|---|---|
| `value_long` | yes | yes | yes | yes | yes | yes |
| `value_double` | yes | yes | yes | yes | yes | yes |
| `value_json` | yes | yes | no | no | no | no |

Asking for `Average`/`Minimum`/`Maximum`/`Sum` on a `value_json`-stored property (decimal, string, enum) returns a typed error: `HistoryAggregationNotSupportedException` from the store, surfaced as a structured MCP error response and as a disabled option in the UI's aggregation dropdown. Casting `value_json` to `numeric` for decimal aggregation is a noted post-MVP improvement.

The in-memory implementation mirrors these semantics with a single linear pass grouping samples by the same epoch-anchored bucket-start formula used in the cross-store merge section (`floor((ts - epoch).Ticks / bucket.Ticks) * bucket.Ticks + epoch`). It does not maintain three internal storage slots; samples are kept as `(ts, object? value)` and the column-dispatch logic at query time inspects the value's runtime type.

## Cross-Store Merge

Timescale batches writes for efficiency (default `FlushInterval = 5 s`, configurable). Between flushes, the in-memory store already holds the very recent samples that Timescale has not yet committed. The merger uses each store's `Coverage` to dispatch queries efficiently and to fill the recent-data gap from in-memory.

### Raw merge

Split the query range across stores by coverage. Each store gets a sub-range it can fully serve. Higher-priority stores claim their range first; lower-priority stores fill what remains.

```csharp
var stores = registry.KnownSubjects.OfType<IHistoryStore>()
    .OrderByDescending(s => s.Priority).ToArray();

var gaps = new List<HistoryCoverage> { new(query.From, query.To) };
var merged = new SortedDictionary<DateTimeOffset, HistoryPoint>();
var truncated = false;

foreach (var store in stores)
{
    if (gaps.Count == 0) break;
    foreach (var sub in Intersect(gaps, store.Coverage))
    {
        var subSeries = await store.QueryAsync(
            query with { From = sub.From, To = sub.To }, ct);
        truncated |= subSeries.Truncated;
        foreach (var p in subSeries.Points)
            merged.TryAdd(p.Timestamp, p);
    }
    gaps = Subtract(gaps, store.Coverage);
}

return new HistorySeries(query.PropertyPath, merged.Values.ToImmutableArray(), truncated);
```

### Bucketed merge

**Per-bucket dispatch, no cross-store aggregation.** Each bucket in the query range is assigned to a single store; that store computes the bucket's aggregate using only its own samples. No data from two stores is ever combined inside one bucket's aggregate. This sidesteps the "average of averages" problem entirely: every aggregation (`Average`, `Minimum`, `Maximum`, `Sum`, `Count`, `Last`) works trivially per bucket because each bucket has one source of truth.

**Assignment rule per bucket:** the highest-priority store whose `Coverage` fully contains the bucket's *effective range*. If none fully contains, fall back to the lowest-priority store with overlap (typically Timescale).

The *effective range* clips the bucket's logical span to the query bounds:

```
effective = [max(bucket.From, query.From), min(bucket.To, query.To)]
```

This matters at the right edge of live queries: a bucket whose logical end extends past `query.To` (= "now") only needs to contain the truncated portion, so in-memory wins boundary buckets it otherwise would have lost to Timescale by a few seconds.

**SQL economy via grouping.** A naive implementation would issue one SQL per bucket. The merger groups consecutive buckets assigned to the same store into one ranged sub-query, so a typical chart is at most one or two round-trips:

```csharp
var buckets = EnumerateBucketRanges(query);
var assignments = buckets.Select(b => (Bucket: b, Store: AssignBucket(stores, b, query)));
var groups = GroupConsecutive(assignments, a => a.Store);

foreach (var group in groups)
{
    var subQuery = query with {
        From = group.First().Bucket.From,
        To   = group.Last().Bucket.To
    };
    var part = await group.Key.QueryAsync(subQuery, ct);
    truncated |= part.Truncated;
    foreach (var p in part.Points) allPoints[p.Timestamp] = p;
}
```

**Bucket alignment invariant.** The in-memory implementation must use the same bucket-boundary computation as Postgres `time_bucket` (epoch-anchored: `floor((ts - epoch).Ticks / bucket.Ticks) * bucket.Ticks + epoch`). If alignment ever drifts, two stores would produce buckets at different timestamps and concatenation would interleave them. This is enforced by a shared helper in `HomeBlaze.History.Abstractions`.

### Resulting behaviour

| Query | Routing |
|---|---|
| `last 30 s`, raw or bucketed | In-memory fully contains every bucket. Timescale never touched. |
| `last 1 hr`, raw | Split by coverage: in-memory serves the last 60 s, Timescale the rest. Two queries. |
| `last 5 min`, bucket=10s | Per-bucket dispatch: Timescale serves buckets 1-24 in one SQL, in-memory serves buckets 25-30. Latest data without buffer lag. |
| `last 1 hr`, bucket=1min | Most buckets don't fit in in-memory (60 s) → Timescale alone. One query. |
| `last 30 days`, bucket=1hr | Timescale alone. |
| Timescale offline, `last 30 s`, raw or bucketed | In-memory still answers. The DB outage is invisible for ranges it covers. |

A store that raises an exception during `QueryAsync` propagates that exception. The merger does not silently swallow errors. A misconfigured Timescale must not masquerade as "no data".

## TimescaleDB Store

### Schema

```sql
create table property_history (
  ts            timestamptz       not null,
  path          text              not null,
  value_long    bigint            null,
  value_double  double precision  null,
  value_json    jsonb             null
);

select create_hypertable('property_history', 'ts', if_not_exists => true);
create index if not exists ix_property_history_path_ts
  on property_history (path, ts desc);
```

Three nullable value columns, dispatched per property type at write and read time. NULL columns in Postgres cost roughly one bit per column per row in the row header; the only payload byte cost is the populated column. Snake_case columns; C# DTOs and parameters remain PascalCase. Schema bootstrap runs idempotently on store start.

### Driver choice

Npgsql native, no ORM. EF Core's migrations fight `create_hypertable`, and change tracking on the hot write path is exactly the overhead to avoid. Dapper offers nothing for bulk writes. Reads are two or three SQL strings and don't earn an abstraction. Writes use `NpgsqlBinaryImporter` (`COPY` binary protocol) per batch.

### Configuration

| Knob | Default | Purpose |
|---|---|---|
| `ConnectionString` | required | Npgsql connection string |
| `Retention` | 30 days | Timescale `add_retention_policy` chunk delete job |
| `BufferTime` | 250 ms | `ChangeQueueProcessor` coalesce window. Within one window only the last value per property is kept. Decides sample resolution. Min 50 ms. |
| `FlushInterval` | 5 s | How often the store actually issues a `COPY`. The store accumulates batches delivered every `BufferTime` and writes them all at once on each flush. Must be `>= BufferTime` (validated at startup; clamped if lower). Larger values reduce DB write frequency at the cost of staleness up to `FlushInterval`. Setting it equal to `BufferTime` means one COPY per delivered batch. |
| `MaxJsonSize` | 8 KB | Per-string cap; overflow becomes a placeholder row |

`[State]` properties for operator observability: `Status`, `StatusMessage`, `QueueDepth`, `DropCount`, `OversizeCount`, `RecordedCount`, `IncomingChangesPerSecond`, `RecordedChangesPerSecond`, `LastFlushUtc`, `LastError`. `IncomingChangesPerSecond` and `RecordedChangesPerSecond` are rolling 1-minute averages, same shape and naming as `OpcUaClient.IncomingChangesPerSecond`. The gap between the two surfaces filtering and backpressure.

**Shared rate counter.** Both rolling rates reuse the existing `ThroughputCounter` (lock-free 60-second sliding window) currently `internal sealed` in `Namotion.Interceptor.OpcUa`. As part of the MVP work, that file is promoted to `Namotion.Interceptor.Connectors` and made `public` so the OPC UA connectors, the history stores, and future change-pipeline consumers can share one implementation. `Namotion.Interceptor.Connectors` is the natural home: it already hosts `ChangeQueueProcessor` (which both OPC UA and the history stores already depend on), so no new dependency is introduced. The two existing OPC UA usages (`OpcUaSubjectClientSource`, `OpcUaSubjectServer`) update their `using` to the new namespace; the existing `ThroughputCounterTests` move to `Namotion.Interceptor.Connectors.Tests`.

### Coverage

`Coverage.From = DateTimeOffset.UtcNow - Retention`, `Coverage.To = DateTimeOffset.UtcNow`. Both derived without DB calls.

## InMemory Store

Top layer: `ConcurrentDictionary<string, PropertyBuffer>` keyed by property path. Inner layer: array-backed ring buffer per path with a per-buffer lock. Buffers are created on first write — properties that never change consume zero memory.

### Configuration

| Knob | Default | Purpose |
|---|---|---|
| `MaxAge` | 60 s | Drop samples older than this. Tuned to cover the Timescale `FlushInterval` plus a generous safety margin and to serve "last 1 min" charts entirely in-memory. |
| `MaxPointsPerProperty` | 1 000 | Per-property hard cap on the ring buffer. Sized to comfortably hold 16 Hz over a minute; protects against a single runaway property dominating memory. |
| `BufferTime` | 250 ms | `ChangeQueueProcessor` batch window. Coalesces repeated writes to the same property within the window down to one sample. Larger values dramatically reduce per-property memory at the cost of slightly less granular recent history; users with 100k+ properties may set this to 1 s to halve memory pressure. Min 50 ms. |
| `MaxJsonSize` | 8 KB | Same placeholder rule as Timescale |

**Realistic memory at scale:**

Memory tracks actual change rate per property, not the per-property cap. With the defaults and a 250ms coalesce window, ~25% of raw writes survive coalescing.

| Scenario | Memory |
|---|---|
| 1 000 properties at 1 Hz average | ~750 KB |
| 10 000 properties at 1 Hz average | ~7.5 MB |
| 100 000 properties at 1 Hz average | ~75 MB |
| 100 000 properties, 10% at 10 Hz (cap-hitting) and 90% at 0.1 Hz | ~500 MB |

Operators with extreme scale (well above 100k properties) tune `MaxAge` down (to 30 s or 10 s) or `BufferTime` up (to 1 s) to keep memory bounded.

`[State]` properties for operator observability mirror the Timescale store where applicable, plus memory metrics: `Status`, `StatusMessage`, `RecordedCount`, `OversizeCount`, `EvictedCount` (samples dropped by `MaxAge` or `MaxPointsPerProperty`), `IncomingChangesPerSecond`, `RecordedChangesPerSecond`, `TrackedPropertyCount`, `TotalSampleCount`, `EstimatedMemoryBytes`. `TotalSampleCount × 50` gives a rough byte estimate; the metric is exposed so operators can confirm tuning matches expectations. No queue or flush metrics since writes are synchronous.

### Coverage

`Coverage.From = max(StartTime, DateTimeOffset.UtcNow - MaxAge)`, `Coverage.To = DateTimeOffset.UtcNow`. The `max(StartTime, …)` clamp matters during the first `MaxAge` window after the store starts: the buffer doesn't actually have samples from before startup, so advertising coverage back to "now − 5 min" would cause the merger to route old-range queries here and get empty results while Timescale (which does have that data) gets skipped.

### Use cases

1. Hot buffer in front of Timescale, covering the batch-flush window.
2. Default store in unit tests and the dev sample, since it has no external dependency.
3. Local fallback during a brief Timescale outage for queries that fit its window.

It is documented as not a production substitute for the DB store.

## UI

### Edit components

One Blazor project per store, matching the `HomeBlaze.OpcUa.Blazor` convention.

`TimescaleDbHistoryStoreEditComponent.razor`: tabs General (name, enabled, connection string, retention) and Advanced (`BufferTime`, `FlushInterval`, `MaxJsonSize`). Status block at the bottom shows `Status` and `StatusMessage` from the subject, same pattern as `OpcUaServerEditComponent`.

`InMemoryHistoryStoreEditComponent.razor`: name, enabled, `MaxAge`, `MaxPointsPerProperty`, `BufferTime`, `MaxJsonSize`.

No setup component. Neither store has an out-of-band discovery step; the standard "create new subject" flow is sufficient. The browser already renders store `[State]` properties, so no custom status view is needed.

### Property history dialog

Reachable from any `[State]` property in the existing property view via a small clock icon. The icon renders only when `property.HasHistory()` is true and at least one `IHistoryStore` exists in the registry. Both conditions are checked once per property render.

Layout:

```
History: <property path>                                      [x]

Range:  [1h] [6h] [24h] [7d] [30d] [Custom...]
Bucket: [Auto v]   Aggregation: [Average v]

[ MudTimeSeriesChart, 400px high ]

3 042 points · Truncated: no · Source: TimescaleDB + InMem

                                                       [Close]
```

Defaults: 24h, Auto bucket, Average for numerics; 24h, raw, Last for non-numerics. Auto bucket picks `range / 200` rounded to a sane interval (1s, 5s, 30s, 1min, 5min, 15min, 1h, 6h, 1d), keeping the chart near 200 points across zoom levels.

Chart: MudBlazor 9.2's `MudTimeSeriesChart` (built in, no new dependency). Linear interpolation for numeric aggregations. Stepped interpolation when displaying `Last` of a non-numeric property, with hover tooltip showing the JSON value.

`Truncated` flag from the response surfaces as a small warning indicator. The "Source" line names the stores that contributed, for trust and debugging.

## MCP Tool

Tool `get_property_history` lives in `HomeBlaze.Mcp` (the enriched layer), since it depends on `HomeBlaze.History.Abstractions`. Base `Namotion.Interceptor.Mcp` stays free of history concerns.

| Parameter | Type | Required | Default | Notes |
|---|---|---|---|---|
| `path` | string | yes | | Subject property path |
| `from` | string | yes | | ISO 8601 timestamp. Parsed with `DateTimeStyles.AssumeUniversal`: an explicit offset (`Z`, `+02:00`) is honoured, a bare timestamp like `2026-05-19T00:00:00` is treated as UTC. |
| `to` | string | no | `now` (UTC) | ISO 8601, same parsing rule as `from`. |
| `bucket` | string | no | null (raw) | `TimeSpan`-parseable, e.g. `5m` |
| `aggregation` | string | no | `last` | One of `last`, `average`, `minimum`, `maximum`, `sum`, `count` (case insensitive) |

Response:

```json
{
  "path": "Devices/LivingRoomThermostat/Temperature",
  "from": "2026-05-19T00:00:00Z",
  "to":   "2026-05-20T00:00:00Z",
  "bucket": "5m",
  "aggregation": "average",
  "points": [
    { "ts": "2026-05-19T00:00:00Z", "value": 21.3 },
    { "ts": "2026-05-19T00:05:00Z", "value": 21.4 },
    { "ts": "2026-05-19T00:10:00Z", "value": null }
  ],
  "truncated": false
}
```

`value` is a polymorphic JSON value: number when populated via `value_long` or `value_double`, JSON literal when populated via `value_json`, `null` when no value column is populated (explicit-null sample). Empty `points` and missing paths are not errors; they return successfully with an empty array. Real errors (DB unreachable, malformed parameters) propagate as MCP error responses.

`MaxPoints` is intentionally not exposed as a tool parameter. Callers that need more detail narrow `from`/`to` instead; the cap (10 000 raw, 1 000 bucketed) keeps any single response bounded and the `truncated` flag signals when it was hit.

## Configuration Summary

Per store, expressed as `[Configuration]` properties so the registry, edit components, and config persistence all see the same surface.

| Knob | Timescale | InMemory |
|---|---|---|
| `ConnectionString` | required | n/a |
| `Retention` | 30 days | n/a |
| `MaxAge` | n/a | 60 s |
| `MaxPointsPerProperty` | n/a | 1 000 |
| `BufferTime` | 250 ms | 250 ms |
| `FlushInterval` | 5 s | n/a |
| `MaxJsonSize` | 8 KB | 8 KB |

## Tests

### Unit tests

- `HistoryEligibility.HasHistory` over every recordable and refused type.
- `InMemoryHistoryStore` recording, retention trimming, raw and bucketed queries, oversize placeholder for long strings, refused types blocked by `HasHistory()`.
- `HistoryQueryExtensions.QueryHistoryAsync` merge logic with fake `IHistoryStore` implementations covering: single store, two stores with disjoint coverage, two stores with overlapping coverage, store throwing, empty registry.
- MCP tool: parameter parsing, case-insensitive aggregation, empty result on unknown path.

No Docker required for any unit test.

### Integration tests

`HomeBlaze.History.TimescaleDb.Tests` with `[Trait("Category", "Integration")]`, excluded from the default `dotnet test` filter, matching the existing repo convention.

Container management via Testcontainers using the `timescale/timescaledb:latest-pg16` image. One container per test class via `IClassFixture<TimescaleDbFixture>`. Per-test schema (`test_<guid>`) for isolation without container churn.

| Category | Verifies |
|---|---|
| Bootstrap | Store starts against empty DB, creates hypertable and index. Second start is a no-op. |
| Recording (`value_long`) | Integer and bool `[State]` writes land with `value_long` populated. Within-batch coalesce collapses repeated writes. |
| Recording (`ulong` overflow) | A `ulong`-typed property is written values both below and above `long.MaxValue`. Sub-overflow values land in `value_long`; overflow values land in `value_json`. Bucketed `Average` for the property uses the COALESCE-aware SQL and the result equals the true mean across both columns. |
| Recording (`value_double`) | `double` and `float` `[State]` writes land with `value_double` populated. |
| Recording (`value_json`) | String, enum, and decimal `[State]` writes land with `value_json` populated. Enums as name strings, decimals as lossless JSON numbers. |
| Recording (null) | Null writes produce all-NULL row. Aggregates skip; `count` includes. |
| Recording ([Configuration] skip) | `[Configuration]` writes are not recorded. |
| Oversize | Long string value produces placeholder row; `OversizeCount` increments. |
| Refused types | `byte[]`, records, value objects are refused upstream by `HasHistory()`; verified explicitly. |
| Raw query | Ordered points within range, capped at `MaxPoints`, `Truncated` flag correct. |
| Aggregated query | `time_bucket` correctness for each aggregation including `Last`, dispatched to the correct value column per property type. |
| Unsupported aggregation | `Average` requested on a `value_json`-stored property (decimal, string, enum) raises `HistoryAggregationNotSupportedException`. |
| Raw coverage merge | In-memory + Timescale populated for the same path; recent samples served by in-memory only, older samples by Timescale only, in-memory wins on overlapping timestamps. |
| Per-bucket dispatch (small bucket) | Query `last 5min, bucket=10s` with in-memory `MaxAge=60s`: older buckets come from Timescale, newest 6 buckets from in-memory. Effective range clipping verified at the right edge. |
| Per-bucket dispatch (large bucket) | Query `last 1h, bucket=1min` with in-memory `MaxAge=60s`: only the latest bucket (or none, depending on alignment) fits in-memory; the rest assigned to Timescale. |
| Per-bucket dispatch (no overlap) | Query fully inside in-memory's coverage: zero Timescale queries. |
| Bucket alignment | In-memory and Timescale bucket boundaries are epoch-anchored and identical for the same bucket size. Concatenation never produces duplicate-timestamp buckets. |
| Retention | Synthetic old rows inserted; `add_retention_policy` job removes them. |
| Backpressure | Throttled DB; queue overflows; oldest dropped; `DropCount` increments. |
| Reconnect | Container restart mid-run; store reconnects and resumes. |

The test project's README must note the Docker requirement. The fixture skips with a clear message when the Docker daemon is unreachable, rather than hanging.

## Open Questions

- Recording complex types (records, value objects, dictionaries, lists of primitives): deferred. The MVP allow-list is numerics, bool, string, enum, decimal. Adding a structured-type path requires schema thought (one column or per-field expansion?) and an oversize policy that goes beyond a string length cap.
- Aggregation (`Average`/`Minimum`/`Maximum`/`Sum`) on `value_json`-stored properties: deferred. Decimal aggregation can be added by casting `value_json #>> '{}'` to `numeric` in the bucketed query; the column dispatch already isolates the change to one SQL path. String and enum aggregations stay limited to `Last` and `Count`.
- `StoreNonNumericValues` toggle to limit a store to numeric/bool history only: dropped from MVP because the tight allow-list makes the runaway-volume scenario it defended against impossible. Add back if a real "this DB is for charts only" use case appears.
- Per-property opt-in/out attribute (`[Historize]` / `[NoHistory]`): defer to a follow-up unless a concrete need surfaces during MVP use.
- Store-level include/exclude filter via glob patterns on property paths: deferred to a configuration follow-up.
- Multi-property compare in the dialog: deferred until the single-property dialog has shaken out.
- Streaming export of large raw ranges: a separate `ExportAsync` method on `IHistoryStore` is the natural seam, not the existing `QueryAsync`.
- Continuous aggregates inside Timescale for long-retention downsampling: post-MVP, internal to the Timescale store, transparent to consumers.
- Global `MaxTotalSamples` cap on the in-memory store with cross-property eviction: deferred. The MVP exposes `TotalSampleCount` and `EstimatedMemoryBytes` so operators can monitor and tune `MaxAge`/`MaxPointsPerProperty`/`BufferTime`. A global cap would need an eviction policy (drop from biggest buffer, oldest globally, LRU) and that decision is worth a separate think once we see real workloads.
