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

- `IHistoryStore` abstraction with capability advertising, query types, eligibility predicate.
- `InMemoryHistoryStore` for tests, samples, and the hot buffer that fills the Timescale write-batch lag.
- `TimescaleDbHistoryStore` for production, with Npgsql binary `COPY` writes and hypertable storage.
- Store edit components in the Blazor UI.
- A property history dialog reachable from any historizable `[State]` property.
- `get_property_history` MCP tool in `HomeBlaze.AI`.
- `TimeWeightedAverage` as the default numeric aggregation, with `Last`, `First`, `Average`, `Minimum`, `Maximum`, `Sum`, `Count`, `StdDev` rounding out the closed MVP set.
- Server-side gap-handling: auto `Last` LOCF, `TimeWeightedAverage` integration includes look-back, `Count` returns 0 for empty buckets, other aggregations return null entries.
- Unified sequential-budget cross-store merger.
- Integration tests against TimescaleDB containers (toolkit-available and toolkit-absent fixtures).

**Out (deferred post-MVP):**

- Parameterized aggregations (`Percentile:p`, `CrossingsAbove:N`); decision on syntax (colon vs preset names like `Percentile95`) deferred until the first one lands.
- Additional aggregations: `Median`, `Percentile`, `Rate`, `Delta`, `StateDuration`, `LTTB`, `Mode`.
- Runtime cross-store config validation (UI helper text + design-doc constraint suffice for MVP).
- Live-edge bucket merge refinement for arbitrary bucket sizes (Path B-lite combinable partial aggregates).
- Server-side gap-fill with linear `Interpolate` for numeric signals.
- Additional stores (file-based, Influx, Kusto, S3 archive).
- Dashboard history tile.
- Multi-property compare in the chart.
- Per-property opt-in or opt-out attributes (`[Historize]` / `[NoHistory]`).
- Store-level include/exclude filter UI.
- CSV/JSON export from the dialog.
- Streaming `IAsyncEnumerable` query path.
- Cross-instance history sync beyond what the existing WebSocket topology already provides as a side effect.
- TimescaleDB compression policy automation (preparation only in MVP: daily chunks).
- Property path normalisation lookup table (observability only in MVP: `EstimatedStorageBytes`).

## Package Structure

| Package | Role |
|---|---|
| `HomeBlaze.History.Abstractions` | `IHistoryStore`, `HistoryQuery`, `HistoryPoint`, `HistorySeries`, `HistoryCoverage`, `HistoryAggregations` constants, `HistoryEligibility` extension methods, `HistoryQueryExtensions.QueryHistoryAsync` cross-store helper. No implementation. References `Namotion.Interceptor.Registry`. |
| `HomeBlaze.History.Abstractions.Tests` | Unit tests for eligibility, the merger (raw planner, bucketed planner, sequential-budget executor), bucket alignment, look-back contract. |
| `HomeBlaze.History.InMemory` | `InMemoryHistoryStore` subject. Ring buffer per property path. Priority 100. Hot buffer / dev / test store. |
| `HomeBlaze.History.InMemory.Tests` | Unit tests covering recording, retention, raw and bucketed queries (including TWA look-back), oversize placeholder, refused types, `Coverage` correctness. |
| `HomeBlaze.History.InMemory.Blazor` | Edit component for the in-memory store. |
| `HomeBlaze.History.TimescaleDb` | `TimescaleDbHistoryStore` subject. Npgsql client, hypertable bootstrap with daily chunks, batched binary `COPY` writes, toolkit probe, shutdown flush. Priority 10. |
| `HomeBlaze.History.TimescaleDb.Blazor` | Edit component for the Timescale store. |
| `HomeBlaze.History.TimescaleDb.Tests` | Integration tests using two Testcontainers fixtures: `timescale/timescaledb-ha:pg16-latest` (toolkit available) and `timescale/timescaledb:latest-pg16` (toolkit absent). |

Target framework: `net10.0` for everything, matching the rest of the HomeBlaze tree.

## Abstractions

```csharp
public interface IHistoryStore : IInterceptorSubject
{
    int Priority { get; }
    HistoryCoverage Coverage { get; }
    IReadOnlySet<string> SupportedAggregations { get; }

    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent sample at or before <paramref name="asOf"/> for the given
    /// property path, or null if no such sample exists. Used by TimeWeightedAverage
    /// integration and Last LOCF gap-fill in the merger and in per-store bucketed
    /// computations.
    /// </summary>
    ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken);
}

public readonly record struct HistoryCoverage(DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(HistoryCoverage other) => other.From >= From && other.To <= To;
    public bool Overlaps(HistoryCoverage other) => other.From < To && other.To > From;
}

public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    string Aggregation = HistoryAggregations.Last,
    int MaxPoints = 10_000);

public record HistoryPoint(
    DateTimeOffset Timestamp,
    double? Number,
    JsonElement? Json);

public record HistorySeries(
    string PropertyPath,
    ImmutableArray<HistoryPoint> Points,
    bool Truncated);
```

### Aggregation identifiers

Aggregations are PascalCase strings rather than a closed enum. This allows toolkit-conditional availability (`TimeWeightedAverage`), per-store specialization for future stores, and additive growth without abstraction churn.

```csharp
public static class HistoryAggregations
{
    public const string Last                = "Last";
    public const string First               = "First";
    public const string Average             = "Average";              // sample mean
    public const string TimeWeightedAverage = "TimeWeightedAverage";  // duration-weighted (UI default for numeric)
    public const string Minimum             = "Minimum";
    public const string Maximum             = "Maximum";
    public const string Sum                 = "Sum";
    public const string Count               = "Count";
    public const string StdDev              = "StdDev";

    /// <summary>
    /// Aggregations every store guarantees to support over any column type.
    /// </summary>
    public static readonly IReadOnlySet<string> Universal =
        new HashSet<string>(StringComparer.Ordinal) { Last, Count };
}
```

The constants prevent typos at hardcoded call sites. The MCP boundary accepts case-insensitive input and normalises to canonical PascalCase. Internal equality uses `StringComparer.Ordinal`.

Parameterized aggregations (`Percentile:p`, `CrossingsAbove:N`) are deferred from MVP. When the first one lands, the syntax (colon-suffix vs preset names) is decided then.

### Capability advertising

`SupportedAggregations` is a property (re-evaluated on each access), not a constructor-baked field. This lets `TimescaleDbHistoryStore` reflect `ToolkitAvailable` changes after the probe completes. `Last` and `Count` are present in every store's set, gated by the `Universal` shortcut so the merger can fast-path raw queries that don't depend on aggregation at all.

### Eligibility predicate

`HasHistory()` is the single source of truth for whether a property is recorded and whether the UI offers the history action. Both stores and the UI gate on it. Eligibility is type-based; the `IsCumulative` and `IsDiscrete` flags on `StateAttribute` govern *display* concerns (UI aggregation dropdown filtering, future `Rate`/`Delta`/`StateDuration` enablement) but do not affect recording.

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

`ValueColumnFor` is the single source of truth used by both the write path (to route a value into the correct column) and the read path (to build column-targeted SQL).

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

`ValueColumnFor(typeof(ulong))` returns `Long` (primary column). On the write path, if a ulong value exceeds `long.MaxValue`, the row goes to `value_json` instead. On the read path, `IsUlongProperty(...) == true` triggers a COALESCE-aware SQL variant.

### Look-back primitive

Both stores expose `GetSampleAtOrBeforeAsync(propertyPath, asOf, ct)` returning the most recent sample at or before `asOf`. This single primitive serves multiple needs:

| Need | When | Why look-back matters |
|---|---|---|
| `TimeWeightedAverage` integration (MVP) | Computing TWA over a bucket | Need the value held entering the bucket. |
| `Last` LOCF gap-fill (MVP) | Empty buckets in bucketed `Last` queries | Carry forward the most recent value, including from before `query.From`. |
| `Delta` / `Rate` on counters (Phase 2) | Sparse counters that haven't been written in a long time | Need `value(bucket.To) - value(bucket.From)`, both via look-back. |
| `Last` raw queries with no in-range samples (corner case) | Coverage extends back further than the first sample in range | Optional: return the look-back value as a single point. |

InMemory implements look-back via binary search to the floor of `asOf` in the property buffer. Timescale uses `SELECT * FROM property_history WHERE path = $1 AND ts <= $2 ORDER BY ts DESC LIMIT 1`, which is a single-row index lookup on `(path, ts DESC)`.

Look-back returning null is honest: the system can't invent history that was never recorded. Pre-first-known-sample buckets stay null in `Last` LOCF; `Delta` over a never-recorded-before range returns null.

### Bucket alignment

In-memory and Timescale must produce buckets at identical timestamps for the same `(bucket size, sample timestamps)` so the sequential-budget merger never produces interleaved duplicates. Both use the epoch-anchored formula matching Postgres `time_bucket`:

```csharp
public static class BucketAlignment
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    public static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan bucket)
    {
        var ticksFromEpoch = (ts - Epoch).Ticks;
        var bucketIndex = ticksFromEpoch / bucket.Ticks;
        return Epoch.AddTicks(bucketIndex * bucket.Ticks);
    }
}
```

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

Within the allow-list, only `string` is unbounded in length. Each store writes serialized JSON into a pooled buffer with a hard cap (default 8 KB, configurable per store as `MaxJsonSize`). On overflow the store writes a small placeholder row:

```json
{ "$oversize": true, "size": 73218 }
```

A placeholder preserves the timeline. `Last` still surfaces a marker with the original size. Numeric aggregations skip it because the numeric value columns are null. The store exposes an `OversizeCount` `[State]` property so operators can see it climb.

Numerics, bool, and enums are bounded by their type; they never trigger the cap. Types outside the allow-list are blocked by `HasHistory()` before reaching the store.

## Query Path

Two query types, dispatched on whether `Bucket` is null. Both share the merger's sequential-budget executor and the contract that sub-queries return the **newest N samples (or buckets)** in their requested sub-range.

### Sub-query "newest N" contract

Every store's `QueryAsync` returns, for a given sub-range, at most `MaxPoints` results representing the newest samples (raw) or newest buckets (bucketed) in that sub-range. The output is sorted ascending by timestamp.

For Timescale raw:

```sql
SELECT ts, <column> AS number, <json> AS json
FROM (
  SELECT ts, <column>, <json>
  FROM property_history
  WHERE path = $1 AND ts >= $2 AND ts < $3
  ORDER BY ts DESC
  LIMIT $4 + 1
) t
ORDER BY ts ASC;
```

For Timescale bucketed:

```sql
SELECT bucket, number, json
FROM (
  SELECT time_bucket($1::interval, ts)         AS bucket,
         <aggregate>(<column>)                 AS number,
         <last_json_or_null>                   AS json
  FROM property_history
  WHERE path = $2 AND ts >= $3 AND ts < $4
  GROUP BY bucket
  ORDER BY bucket DESC
  LIMIT $5 + 1
) t
ORDER BY bucket ASC;
```

`<column>` is the column for the property's declared type. `<aggregate>` switches per aggregation (see below). Bigint aggregates cast to `double precision` on the way out so `HistoryPoint.Number` stays a unified `double?`.

The `+ 1` on the LIMIT lets the store detect overflow without a separate `count(*)`: if the result hits `MaxPoints + 1`, drop the last and set `Truncated = true` in the response.

In-memory implements the same semantics via a binary-search slice over the property's sorted samples, taking the last N from the slice instead of the first N.

### Aggregate fragments per aggregation

| Aggregation | Timescale fragment | Notes |
|---|---|---|
| `Last` | `locf(last(<column>, ts))` over `time_bucket_gapfill` | Server-side LOCF for empty buckets (see below). |
| `First` | `first(<column>, ts)` | Empty buckets stay null. |
| `Average` | `avg(<column>)` | Sample mean. Empty buckets null. |
| `TimeWeightedAverage` | `average(time_weight('locf', ts, <column>))` (toolkit) | Look-back semantics; empty buckets receive the held value. |
| `Minimum` | `min(<column>)` | Empty buckets null. |
| `Maximum` | `max(<column>)` | Empty buckets null. |
| `Sum` | `sum(<column>)` | Empty buckets null. |
| `Count` | `count(*)` returned as `number` | Empty buckets return 0, not null. |
| `StdDev` | `stddev_samp(<column>)` | Empty buckets null. |

### Empty-bucket / gap semantics

The wire format encodes gaps as **`null` entries**, not absent entries:

```json
"points": [
  { "ts": "2026-05-23T00:05:00Z", "value": 21.3 },
  { "ts": "2026-05-23T00:10:00Z", "value": null },
  { "ts": "2026-05-23T00:15:00Z", "value": 22.1 }
]
```

Empty buckets within the query range produce one entry each with `Number` and `Json` both null (except where the aggregation provides a value). This makes the response self-describing for programmatic consumers without their having to compute expected bucket boundaries.

Timescale uses `time_bucket_gapfill` (TimescaleDB core, no toolkit required) to emit the empty buckets in the result set. In-memory mirrors this in the bucketed scan, walking expected bucket boundaries and emitting a `HistoryPoint(ts, null, null)` for empty buckets.

Per-aggregation behavior in empty buckets:

| Aggregation | Empty bucket in response |
|---|---|
| `Last` | Server-applied LOCF: most recent value before the bucket. Pre-first-sample buckets stay null. |
| `TimeWeightedAverage` | Held value computed via integration with look-back. Pre-first-sample buckets stay null. |
| `Count` | `0` (a count of zero is a real fact, not a gap). |
| All others (`First`, `Average`, `Sum`, `Min`, `Max`, `StdDev`) | `null`. The chart leaves a visual gap. |

No new `GapFill` query parameter; the right behavior is automatic per aggregation.

### Aggregation support per column

| Column | `Last` | `First` | `Count` | `Average` | `TimeWeightedAverage` | `Minimum` | `Maximum` | `Sum` | `StdDev` |
|---|---|---|---|---|---|---|---|---|---|
| `value_long` | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| `value_double` | yes | yes | yes | yes | yes | yes | yes | yes | yes |
| `value_json` | yes | yes | yes | no | no | no | no | no | no |

Asking for a numeric aggregation on a `value_json`-stored property (decimal, string, enum) raises `HistoryAggregationNotSupportedException` from the store, surfaced as a structured MCP error and as a hidden option in the UI dropdown.

### `ulong` read path

For ulong properties whose values can straddle `long.MaxValue`, the read path uses a COALESCE-aware variant so aggregates see both columns. Example for bucketed `Average`:

```sql
SELECT bucket, number, NULL::jsonb AS json FROM (
  SELECT time_bucket($1::interval, ts) AS bucket,
         avg(coalesce(value_long::numeric,
                      (value_json #>> '{}')::numeric))::double precision AS number
  FROM property_history
  WHERE path = $2 AND ts >= $3 AND ts < $4
  GROUP BY bucket
  ORDER BY bucket DESC
  LIMIT $5 + 1
) t ORDER BY bucket ASC;
```

Fired only when `HistoryColumns.IsUlongProperty(propertyType)` is true. All other property types use the single-column SQL with no COALESCE overhead.

## Cross-Store Merge

The merger is structured in two phases: a **planner** that differs by query type, and a shared **executor** that consumes the plan.

```csharp
public record StoreDispatch(IHistoryStore Store, IReadOnlyList<HistoryRange> Ranges);

public static async Task<HistorySeries> QueryHistoryAsync(
    this SubjectRegistry registry, HistoryQuery query, CancellationToken ct)
{
    var stores = registry.KnownSubjects.OfType<IHistoryStore>()
        .OrderByDescending(s => s.Priority).ToArray();

    CheckEligibility(stores, query);

    var plan = query.Bucket is null
        ? PlanRawDispatch(stores, query)
        : PlanBucketedDispatch(stores, query);

    return await ExecuteWithBudget(plan, query, ct);
}
```

### Eligibility check

For a query to succeed, every part of `[From, To]` must be servable by some store that supports the requested aggregation. The universal aggregations (`Last`, `Count`) are always servable and skip this check.

```csharp
static void CheckEligibility(IHistoryStore[] stores, HistoryQuery query)
{
    if (HistoryAggregations.Universal.Contains(query.Aggregation)) return;

    var eligible = stores.Where(s => s.SupportedAggregations.Contains(query.Aggregation));
    var union = ComputeCoverageUnion(eligible.Select(s => s.Coverage));
    if (!union.Contains(query.From, query.To))
    {
        throw new HistoryAggregationNotSupportedException(
            query.Aggregation, query.From, query.To,
            available: stores.SelectMany(s => s.SupportedAggregations).Distinct().ToArray());
    }
}
```

The exception carries the `available` list so MCP errors and UI tooltips can advertise what *would* work for the requested range.

### Raw planner: coverage subtraction

Each store gets a non-overlapping sub-range. Higher-priority stores claim their range first; lower-priority stores fill what remains.

```csharp
static IReadOnlyList<StoreDispatch> PlanRawDispatch(IHistoryStore[] stores, HistoryQuery query)
{
    var gaps = new List<HistoryCoverage> { new(query.From, query.To) };
    var plan = new List<StoreDispatch>();
    foreach (var store in stores.Where(s => Supports(s, query.Aggregation)))
    {
        var ranges = Intersect(gaps, store.Coverage).ToList();
        if (ranges.Count > 0) plan.Add(new StoreDispatch(store, ranges));
        gaps = Subtract(gaps, store.Coverage).ToList();
        if (gaps.Count == 0) break;
    }
    return plan;
}
```

### Bucketed planner: per-bucket dispatch

Each bucket in the range is assigned to a single eligible store: the highest-priority store that supports the aggregation and whose `Coverage` fully contains the bucket's *effective range*:

```
effective = [max(bucket.From, query.From), min(bucket.To, query.To)]
```

Consecutive buckets assigned to the same store are grouped into one ranged sub-query. If no eligible store fully contains a bucket but at least one overlaps, fall back to the lowest-priority eligible overlapping store. Buckets entirely outside every eligible store's coverage are skipped (they appear as gaps in the final response — but eligibility check above ensures at least one store covers each bucket if we got this far).

No data from two stores is ever combined inside one bucket's aggregate. This sidesteps the "average of averages" problem entirely; every aggregation works trivially per bucket because each bucket has one source of truth.

### Shared executor: sequential budget

```csharp
static async Task<HistorySeries> ExecuteWithBudget(
    IReadOnlyList<StoreDispatch> plan, HistoryQuery query, CancellationToken ct)
{
    var remaining = query.MaxPoints;
    var truncated = false;
    var collected = new SortedDictionary<DateTimeOffset, HistoryPoint>();

    foreach (var (store, ranges) in plan)            // priority order
    {
        if (remaining <= 0) { truncated = true; break; }

        // Newest-first within store so we keep the freshest under budget.
        foreach (var range in ranges.OrderByDescending(r => r.To))
        {
            if (remaining <= 0) break;
            var sub = await store.QueryAsync(
                query with { From = range.From, To = range.To, MaxPoints = remaining }, ct);
            truncated |= sub.Truncated;
            foreach (var point in sub.Points)
                if (collected.TryAdd(point.Timestamp, point))
                    remaining--;
        }
    }

    return new HistorySeries(query.PropertyPath, collected.Values.ToImmutableArray(), truncated);
}
```

Properties of the executor:

- **No over-fetching.** Each store receives the *remaining* budget, not the full `MaxPoints`. Higher-priority stores can short-circuit lower-priority queries entirely when they have enough data.
- **Newest-first.** Sub-queries return the newest N (see "Sub-query 'newest N' contract" above). Combined with priority ordering, this naturally produces "newest MaxPoints overall" across stores.
- **Truncated honestly.** Set when either a sub-query truncated *or* the budget ran out mid-iteration.
- **Dedup via TryAdd.** Higher-priority store's value wins for overlapping timestamps (rare but possible for raw queries).

### Live-edge bucket accuracy

The bucketed planner assigns buckets based on `Coverage`. For a bucket larger than `InMemory.MaxAge`, the live-edge bucket (the rightmost bucket whose `To` reaches `query.To`) is never fully contained by InMemory and falls to Timescale. Timescale's `Coverage.To = _lastFlushHighWaterMark` (see TimescaleDB Store below) trails "now" by 0 to `FlushInterval` in steady state, so the live-edge bucket's aggregate omits the last `FlushInterval` of samples.

Relative error is bounded by `FlushInterval / bucket_size`:

| Bucket size (`FlushInterval = 5s`) | Live-edge error |
|---|---|
| 30s | covered by InMemory if `MaxAge >= 60s` (default) — perfect |
| 5min | 1.7% |
| 10min | 0.83% |
| 1h | 0.14% |
| 24h | 0.006% |

For workloads that need pixel-perfect aggregation in the live-edge bucket at chart-typical sizes (5–15 min), increase `MaxAge` to match the smallest bucket size you care about. Most operators won't need to. A combinable-partial-aggregate path (Timescale returns aggregate state, InMemory contributes raw samples past `Coverage.To`, merger combines in-process) is a noted post-MVP refinement when real workloads expose the medium-bucket wobble.

### Cross-store sizing constraint

`InMemory.MaxAge >= 2 * TimescaleDb.FlushInterval`.

Without this, samples can be invisible to queries during the window between InMemory eviction and Timescale commit. The default configs (`MaxAge = 60s`, `FlushInterval = 5s`) satisfy this comfortably. The constraint is documented in the `MaxAge` `[Configuration]` doc comment and surfaced as helper text in the edit component. Runtime validation is a noted fast-follow.

### Resulting behaviour

| Query | Routing |
|---|---|
| `last 30 s`, raw or bucketed | InMemory fully contains every bucket; budget exhausts before Timescale is queried. Timescale never touched. |
| `last 1 hr`, raw | Split by coverage: InMemory serves the last 60s, Timescale the rest with the remaining budget. Two queries (one per store). |
| `last 5 min, bucket=10s` | Per-bucket dispatch: Timescale serves buckets 1-24 in one grouped sub-query, InMemory serves buckets 25-30. Latest data without buffer lag. |
| `last 1 hr, bucket=1min` | Most buckets don't fit in InMemory (60s) → Timescale alone. One query. Live-edge bucket error: ~8.3%. |
| `last 1 hr, bucket=5min` | Same as above. Live-edge bucket error: ~1.7%. |
| `last 30 days, bucket=1hr` | Timescale alone. Live-edge bucket error: 0.14%. |
| Timescale offline, `last 30 s`, raw | InMemory still answers. The DB outage is invisible for ranges it covers. |
| Timescale offline, `last 1 hr`, raw | InMemory answers the last 60s; Timescale returns from its `Coverage.To` (frozen at last successful flush) for the older sub-range. Gaps between freeze point and `now - 60s` are honest empty buckets/missing samples. |
| `TimeWeightedAverage` requested, toolkit unavailable | If only InMemory supports TWA and the range exceeds InMemory's coverage: `HistoryAggregationNotSupportedException` with `available` list. UI never showed TWA in the dropdown for this configuration. |

A store that raises an exception during `QueryAsync` propagates that exception. The merger does not silently swallow errors. A misconfigured Timescale must not masquerade as "no data".

## TimescaleDB Store

### Schema

```sql
CREATE TABLE IF NOT EXISTS property_history (
  ts            timestamptz       NOT NULL,
  path          text              NOT NULL,
  value_long    bigint            NULL,
  value_double  double precision  NULL,
  value_json    jsonb             NULL
);

SELECT create_hypertable('property_history', 'ts',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists => true);

CREATE INDEX IF NOT EXISTS ix_property_history_path_ts
    ON property_history (path, ts DESC);

CREATE TABLE IF NOT EXISTS history_schema_version (
    version       integer PRIMARY KEY,
    applied_at    timestamptz NOT NULL DEFAULT now(),
    description   text NOT NULL
);
INSERT INTO history_schema_version (version, description)
VALUES (1, 'Initial MVP schema')
ON CONFLICT (version) DO NOTHING;
```

Three nullable value columns, dispatched per property type at write and read time. Daily chunks make future compression (`add_compression_policy`) more granular and queries against compressed ranges more efficient. Snake_case columns; C# DTOs and parameters remain PascalCase. Schema bootstrap runs idempotently on store start.

### Driver choice

Npgsql native, no ORM. EF Core's migrations fight `create_hypertable`, and change tracking on the hot write path is exactly the overhead to avoid. Dapper offers nothing for bulk writes. Reads are a handful of SQL strings and don't earn an abstraction. Writes use `NpgsqlBinaryImporter` (`COPY` binary protocol) per batch.

### Configuration

| Knob | Default | Purpose |
|---|---|---|
| `ConnectionString` | required | Npgsql connection string |
| `Retention` | 30 days | Timescale `add_retention_policy` chunk delete job |
| `BufferTime` | 250 ms | `ChangeQueueProcessor` coalesce window. Min 50 ms. |
| `FlushInterval` | 5 s | How often the store issues a `COPY`. Must be `>= BufferTime` (validated at startup; clamped if lower). |
| `ShutdownFlushTimeout` | 10 s | Maximum time to wait for the final synchronous COPY during graceful shutdown before giving up. |
| `MaxJsonSize` | 8 KB | Per-string cap; overflow becomes a placeholder row |

### State properties for operator observability

`Status`, `StatusMessage`, `QueueDepth`, `DropCount`, `OversizeCount`, `RecordedCount`, `IncomingChangesPerSecond`, `RecordedChangesPerSecond`, `LastFlushUtc`, `LastError`, plus:

- `ToolkitAvailable` (bool?) — null until first probe, then true/false.
- `ToolkitStatus` (string?) — human-readable detail (e.g., `"Available (version 1.18.0)"`, `"Not installed at the cluster level"`, `"Probe failed: connection refused"`).
- `EstimatedStorageBytes` (long?) — refreshed periodically via `SELECT pg_total_relation_size('property_history')`. Trigger metric for the deferred path-normalisation optimization.

**Shared rate counter.** Both rolling rates reuse the existing `ThroughputCounter` (lock-free 60-second sliding window) currently `internal sealed` in `Namotion.Interceptor.OpcUa`. As part of the MVP work, that file is promoted to `Namotion.Interceptor.Connectors` and made `public` so the OPC UA connectors, the history stores, and future change-pipeline consumers share one implementation. `Namotion.Interceptor.Connectors` already hosts `ChangeQueueProcessor` (referenced by both OPC UA and the history stores), so no new dependency is introduced.

### Coverage

`Coverage.From = DateTimeOffset.UtcNow - Retention`. `Coverage.To = _lastFlushHighWaterMark`.

The high-water-mark is the timestamp of the newest sample known to be committed to the DB:

- Pre-first-flush (and pre-bootstrap): `Coverage = (StartTime, StartTime)` — empty range. The merger never assigns work to Timescale until it has actually committed something.
- Seeded at bootstrap via `SELECT MAX(ts) FROM property_history`. This makes Coverage honest immediately after restart instead of advertising empty coverage until the first post-restart flush.
- Clamped at startup to `now` if the seed is in the future (clock skew with a previous writer).
- Advanced after each successful `COPY` to `max(_lastFlushHighWaterMark, batch.Max(s => s.Timestamp))`. Empty flushes don't advance it (sparse ranges between samples are routed to InMemory, which correctly returns empty).
- Frozen during DB outages: the merger sees the gap and routes that range to InMemory instead of getting silent holes.

If `_lastFlushHighWaterMark` ages past `now - Retention` (e.g., DB has been failing flushes for so long that the high-water-mark predates retention), the Coverage getter returns an empty `(to, to)` range instead of an inverted one.

### Toolkit dependency

Probe via `CREATE EXTENSION IF NOT EXISTS timescaledb_toolkit` during schema bootstrap:

- If toolkit is installed and not yet enabled: enables it. Bootstrap proceeds normally.
- If toolkit is installed and already enabled: no-op.
- If toolkit is not installed at the cluster level: catch the exception, set `ToolkitAvailable = false`, log a warning, continue startup.

Re-probe on every successful reconnect (in case the operator installed toolkit during the outage). No periodic polling.

`SupportedAggregations` reflects the current value of `ToolkitAvailable`:

```csharp
public IReadOnlySet<string> SupportedAggregations
{
    get
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            HistoryAggregations.Last, HistoryAggregations.First,
            HistoryAggregations.Average, HistoryAggregations.Minimum,
            HistoryAggregations.Maximum, HistoryAggregations.Sum,
            HistoryAggregations.Count, HistoryAggregations.StdDev,
        };
        if (ToolkitAvailable == true)
            set.Add(HistoryAggregations.TimeWeightedAverage);
        return set;
    }
}
```

### Recommended Docker image

For production deployments, the `timescale/timescaledb-ha:pg16-latest` image bundles `timescaledb_toolkit` along with TimescaleDB core, so all aggregations including `TimeWeightedAverage` work out of the box. The smaller `timescale/timescaledb` image works too; install the toolkit manually (`CREATE EXTENSION timescaledb_toolkit;`) or accept the degraded set. `time_bucket`, `time_bucket_gapfill`, and basic aggregations are in TimescaleDB core and don't require the toolkit.

### Shutdown flush

On `StopAsync`, the store performs a final synchronous `COPY` of any pending batch. Bounded by `ShutdownFlushTimeout` (default 10 s). If the timeout is reached, the pending batch is dropped: `Status = Failed`, `StatusMessage = "Shutdown flush timed out; <N> samples lost"`, and the log records the loss. The store accepts no new writes during the drain.

On process crash (SIGKILL, OOM, etc.), the pending batch is lost regardless. Up to `FlushInterval` of samples can be lost on crash — documented limitation of batched writes.

## InMemory Store

Top layer: `ConcurrentDictionary<string, PropertyBuffer>` keyed by property path. Inner layer: array-backed ring buffer per path with a per-buffer lock. Buffers are created on first write; properties that never change consume zero memory.

### Configuration

| Knob | Default | Purpose |
|---|---|---|
| `MaxAge` | 60 s | Drop samples older than this. Must be at least `2 * FlushInterval` of any companion persistent store to guarantee samples remain in-buffer until they're committed. Default satisfies the constraint for the default 5s Timescale flush. |
| `MaxPointsPerProperty` | 1 000 | Per-property hard cap on the ring buffer. Sized to comfortably hold 16 Hz over a minute; protects against a single runaway property dominating memory. |
| `BufferTime` | 250 ms | `ChangeQueueProcessor` batch window. Min 50 ms. |
| `MaxJsonSize` | 8 KB | Same placeholder rule as Timescale |

**Realistic memory at scale:**

Memory tracks actual change rate per property, not the per-property cap. With the defaults and a 250 ms coalesce window, ~25% of raw writes survive coalescing.

| Scenario | Memory |
|---|---|
| 1 000 properties at 1 Hz average | ~750 KB |
| 10 000 properties at 1 Hz average | ~7.5 MB |
| 100 000 properties at 1 Hz average | ~75 MB |
| 100 000 properties, 10% at 10 Hz (cap-hitting) and 90% at 0.1 Hz | ~500 MB |

Operators with extreme scale (well above 100k properties) tune `MaxAge` down (to 30 s or 10 s) or `BufferTime` up (to 1 s) to keep memory bounded.

### State properties for operator observability

`Status`, `StatusMessage`, `RecordedCount`, `OversizeCount`, `EvictedCount` (samples dropped by `MaxAge` or `MaxPointsPerProperty`), `IncomingChangesPerSecond`, `RecordedChangesPerSecond`, `TrackedPropertyCount`, `TotalSampleCount`, `EstimatedMemoryBytes`. `TotalSampleCount × 50` gives a rough byte estimate.

### Coverage

`Coverage.From = max(StartTime, DateTimeOffset.UtcNow - MaxAge)`, `Coverage.To = DateTimeOffset.UtcNow`. The `max(StartTime, …)` clamp matters during the first `MaxAge` window after the store starts: the buffer doesn't actually have samples from before startup, so advertising coverage back to "now − MaxAge" would route old-range queries here for empty results while Timescale (which does have that data) gets skipped.

### TimeWeightedAverage with look-back

InMemory implements TWA via trapezoidal integration with look-back semantics matching toolkit's `time_weight('locf', ...)`:

1. For a bucket `[bucket.From, bucket.To]`, query the buffer for in-bucket samples plus call `GetSampleAtOrBeforeAsync(path, bucket.From)` to find the value held entering the bucket.
2. Integrate value × duration across each segment (look-back start → first in-bucket sample, then each adjacent pair, then last sample → bucket.To).
3. Divide by total duration to produce TWA.

Empty bucket (no in-bucket samples): TWA = look-back value (held throughout the bucket). If look-back returns null: TWA = null.

This implementation must produce identical numbers to Timescale's `average(time_weight('locf', ts, value))` for the same input data. **An integration test that writes identical samples to both stores and asserts TWA equality across a battery of edge cases (empty bucket, single-sample bucket, boundary-sample bucket, sparse vs dense, look-back-only bucket where no in-bucket samples exist) is load-bearing for the unified-merger design.**

### Shutdown semantics

InMemory has no shutdown flush. Its data is process-local and lost on every restart. This is intentional: InMemory is documented as a hot buffer / dev / test store, not a production substitute. Configurations using only InMemory will lose history on every restart.

### Use cases

1. Hot buffer in front of Timescale, covering the batch-flush window.
2. Default store in unit tests and the dev sample, since it has no external dependency.
3. Local fallback during a brief Timescale outage for queries that fit its window.

## UI

### Edit components

One Blazor project per store, matching the `HomeBlaze.OpcUa.Blazor` convention.

`TimescaleDbHistoryStoreEditComponent.razor`: tabs General (name, enabled, connection string, retention) and Advanced (`BufferTime`, `FlushInterval`, `ShutdownFlushTimeout`, `MaxJsonSize`). Status block at the bottom shows `Status`, `StatusMessage`, `ToolkitStatus`, and `EstimatedStorageBytes`.

`InMemoryHistoryStoreEditComponent.razor`: name, enabled, `MaxAge`, `MaxPointsPerProperty`, `BufferTime`, `MaxJsonSize`. The `MaxAge` field has helper text noting the `>= 2 * FlushInterval` constraint.

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

Defaults: 24h, Auto bucket. The default aggregation for numeric properties is `TimeWeightedAverage` if supported by all registered stores (labelled "Average"), falling through to `Average` (sample mean) if not. For non-numeric properties, default is `Last`. Auto bucket picks `range / 200` rounded to a sane interval (1s, 5s, 30s, 1min, 5min, 15min, 1h, 6h, 1d), keeping the chart near 200 points across zoom levels.

### Aggregation dropdown gating

The dropdown shows only aggregations available *for the current property*:

- For properties with `IsCumulative = true` (counters): show only `Last`, `First`, `Minimum`, `Maximum`, `Count`. Default: `Last`. Other aggregations are mathematically meaningless for counters (averaging or summing meter readings produces nonsense).
- For all other properties: filter by `ValueColumnFor(propertyType)` (numeric vs JSON) and intersect with the union of `SupportedAggregations` across all registered stores. If `TimeWeightedAverage` isn't supported by every store with non-empty coverage, hide it entirely ("don't offer what can't be delivered").

When `TimeWeightedAverage` is shown, it is labelled "Average" with a tooltip explaining time-weighted semantics. The sample mean (`Average`) appears as "Sample Average" lower in the dropdown for users who explicitly want it.

Per-range filter refinement (re-evaluating availability as the user changes the time range) is a noted fast-follow.

### Chart rendering

MudBlazor 9.2's `MudTimeSeriesChart` (built in, no new dependency). `ChartPoint<T>` requires non-nullable numeric `Y`; null entries from the response can't be passed directly. The chart layer converts each `HistorySeries` into one or more `ChartSeries`:

- For `Last`-aggregation responses (server-applied LOCF means no nulls in the typical case): one `ChartSeries`, stepped-line interpolation.
- For other aggregations (gaps present as nulls): split the series at null entries into multiple `ChartSeries`, all with the same color/style, only the first carrying a legend name. Each contiguous run renders as its own line; the breaks between them appear as visual gaps.

Tooltips show the raw value and timestamp.

### Gap-rendering convention (chart layer)

The store's `QueryAsync` returns explicit null entries for empty buckets in the wire format. The convention below describes the chart's visual treatment after the chart-layer split:

| Aggregation | Chart treatment for null/empty buckets |
|---|---|
| `Last`, non-numeric | (Server-applied LOCF: no nulls in response.) Stepped line through the carried values. |
| `TimeWeightedAverage` | (Server-applied integration: no nulls in response unless pre-first-sample.) Smooth line. |
| `Count` | Server returns 0 for empty buckets. Bar/line shows zero. |
| `First`, `Average`, `Min`, `Max`, `Sum`, `StdDev` | Nulls in response. Chart splits into sub-series; visual gap between runs. |

The chart never invents data the server didn't return. Server-side gap-fill (LOCF / interpolate) for *other* aggregations beyond what's described here is a Phase 2 opt-in.

## MCP Tool

Tool `get_property_history` lives in `HomeBlaze.AI` (the enriched layer), since it depends on `HomeBlaze.History.Abstractions`. Base `Namotion.Interceptor.Mcp` stays free of history concerns.

| Parameter | Type | Required | Default | Notes |
|---|---|---|---|---|
| `path` | string | yes | | Subject property path |
| `from` | string | yes | | ISO 8601 timestamp. Parsed with `DateTimeStyles.AssumeUniversal`: explicit offset (`Z`, `+02:00`) honoured; bare timestamps treated as UTC. |
| `to` | string | no | `now` (UTC) | ISO 8601, same parsing rule. |
| `bucket` | string | no | null (raw) | `TimeSpan`-parseable, e.g. `5m` |
| `aggregation` | string | no | `Last` | Case-insensitive match against `HistoryAggregations` constants. |

Response:

```json
{
  "path": "Devices/LivingRoomThermostat/Temperature",
  "from": "2026-05-23T00:00:00Z",
  "to":   "2026-05-24T00:00:00Z",
  "bucket": "5m",
  "aggregation": "TimeWeightedAverage",
  "value_type": "number",
  "points": [
    { "ts": "2026-05-23T00:00:00Z", "value": 21.3 },
    { "ts": "2026-05-23T00:05:00Z", "value": 21.4 },
    { "ts": "2026-05-23T00:10:00Z", "value": null }
  ],
  "truncated": false
}
```

### `value_type` field

Reflects the **response value's type** (what consumers see in `value`), not the property's underlying type. Values:

- `"number"` — `Number` field populated. Includes `Count` regardless of property type.
- `"string"` — string-valued non-numeric property via `Last`/`First`.
- `"boolean"` — bool property via `Last`/`First`.
- `"enum"` — enum property via `Last`/`First` (enum name as JSON string).

The hint helps programmatic consumers parse without inspecting each point's type.

### Error responses

Unknown aggregation, missing path, or aggregation not supported across the requested range:

```json
{
  "error": "HistoryAggregationNotSupported",
  "message": "Aggregation 'TimeWeightedAverage' is not supported. Available: Last, First, Average, Minimum, Maximum, Sum, Count, StdDev.",
  "requested": "TimeWeightedAverage",
  "available": ["Last", "First", "Average", "Minimum", "Maximum", "Sum", "Count", "StdDev"]
}
```

No silent fallback to a different aggregation. The caller (LLM or otherwise) sees the failure and the available set, and can retry with a supported aggregation.

Empty `points` and missing paths are not errors; they return successfully with an empty array (and `value_type` reflects the property's type if it exists).

`MaxPoints` is not exposed as a tool parameter. Callers that need more detail narrow `from`/`to` instead; the cap (10 000 raw, 1 000 bucketed via the auto-bucket math) keeps any single response bounded and the `truncated` flag signals when it was hit.

## Configuration Summary

Per store, expressed as `[Configuration]` properties.

| Knob | Timescale | InMemory |
|---|---|---|
| `ConnectionString` | required | n/a |
| `Retention` | 30 days | n/a |
| `MaxAge` | n/a | 60 s |
| `MaxPointsPerProperty` | n/a | 1 000 |
| `BufferTime` | 250 ms | 250 ms |
| `FlushInterval` | 5 s | n/a |
| `ShutdownFlushTimeout` | 10 s | n/a |
| `MaxJsonSize` | 8 KB | 8 KB |

## Tests

### Unit tests

- `HistoryEligibility.HasHistory` over every recordable and refused type.
- `HistoryColumns.ValueColumnFor` and `IsUlongProperty` over every supported type.
- `BucketAlignment.BucketStart` correctness; epoch-anchored parity with Postgres `time_bucket`.
- `InMemoryHistoryStore`: recording, retention trimming, raw and bucketed queries, oversize placeholder for long strings, refused types blocked by `HasHistory()`, TWA computation with look-back.
- `InMemoryHistoryStore.GetSampleAtOrBeforeAsync` correctness with empty buffer, exact-match, before-buffer-start, after-buffer-end.
- `HistoryQueryExtensions.QueryHistoryAsync` raw planner: single store, two disjoint, two overlapping, store throwing, empty registry.
- `HistoryQueryExtensions.QueryHistoryAsync` bucketed planner: per-bucket dispatch, consecutive grouping, effective-range clipping at the right edge, single bucket spanning both stores.
- `HistoryQueryExtensions.ExecuteWithBudget`: sequential budget exhaustion, newest-first sub-range ordering, dedup via TryAdd, truncated propagation.
- `HistoryQueryExtensions.CheckEligibility`: store-without-aggregation skipped, throws when no eligible store covers, universal aggregations bypass check.
- MCP tool: parameter parsing, case-insensitive aggregation, empty result on unknown path, error response shape, `value_type` correctness across response types.

No Docker required for any unit test.

### Integration tests

`HomeBlaze.History.TimescaleDb.Tests` with `[Trait("Category", "Integration")]`, excluded from the default `dotnet test` filter.

Two Testcontainers fixtures:

- `TimescaleDbHaFixture` uses `timescale/timescaledb-ha:pg16-latest` (toolkit available). Default for happy-path tests.
- `TimescaleDbBaseFixture` uses `timescale/timescaledb:latest-pg16` (toolkit absent). For the toolkit-absent test class.

Per-test schema (`test_<guid>`) for isolation without container churn.

| Category | Verifies | Fixture |
|---|---|---|
| Bootstrap | Schema created idempotently. Second start is a no-op. `history_schema_version` populated. | HA |
| Toolkit probe (available) | `ToolkitAvailable = true`, version reported in `ToolkitStatus`, `TimeWeightedAverage` in `SupportedAggregations`. | HA |
| Toolkit probe (absent) | `ToolkitAvailable = false`, message in `ToolkitStatus`, `TimeWeightedAverage` excluded from `SupportedAggregations`. Querying TWA over range with only Timescale coverage raises `HistoryAggregationNotSupportedException`. | Base |
| Coverage high-water-mark | After flush, `Coverage.To` matches max sample timestamp. Empty flush doesn't advance. DB outage freezes Coverage.To. Restart re-seeds from `MAX(ts)`. | HA |
| Recording (`value_long`) | Integer and bool `[State]` writes land with `value_long` populated. Within-batch coalesce collapses repeated writes. | HA |
| Recording (`ulong` overflow) | Sub-overflow values land in `value_long`; overflow values land in `value_json`. Bucketed `Average` for the property uses COALESCE-aware SQL. | HA |
| Recording (`value_double`) | `double` and `float` `[State]` writes land with `value_double` populated. | HA |
| Recording (`value_json`) | String, enum, and decimal `[State]` writes land with `value_json` populated. | HA |
| Recording (null) | Null writes produce all-NULL row. Aggregates skip; `count` includes. | HA |
| Recording (`[Configuration]` skip) | `[Configuration]` writes are not recorded. | HA |
| Oversize | Long string value produces placeholder row; `OversizeCount` increments. | HA |
| Refused types | `byte[]`, records, value objects refused upstream by `HasHistory()`. | HA |
| Raw query (newest-N semantics) | When samples exceed `MaxPoints`, the newest N are returned in chronological order. `Truncated = true`. | HA |
| Bucketed query | `time_bucket` correctness for each aggregation, dispatched to the correct value column per property type. | HA |
| TWA parity | Identical samples written to both stores produce identical TWA values across edge cases (empty bucket, single-sample bucket, boundary-sample bucket, look-back-only bucket). | HA |
| `Last` LOCF | Empty buckets in `Last`-aggregation responses contain the carried value. Pre-first-sample buckets stay null. | HA |
| `Count` empty bucket | Returns 0, not null. | HA |
| `time_bucket_gapfill` integration | Bucketed queries emit explicit entries for empty buckets within the range (null values for gap-leaving aggregations). | HA |
| Look-back primitive | `GetSampleAtOrBeforeAsync` returns most recent sample at or before timestamp; null when none exists. | Both |
| Unsupported aggregation | `Average` requested on a `value_json`-stored property raises `HistoryAggregationNotSupportedException`. | HA |
| Raw coverage merge | InMemory + Timescale populated for the same path; sequential-budget executor produces newest MaxPoints overall. | HA |
| Per-bucket dispatch (small bucket) | Older buckets from Timescale, newest buckets from InMemory. Effective-range clipping verified at the right edge. | HA |
| Per-bucket dispatch (large bucket) | `last 1h, bucket=1min` with `MaxAge=60s`: live-edge bucket served by Timescale with bounded error. | HA |
| Per-bucket dispatch (no overlap) | Query fully inside InMemory's coverage: zero Timescale queries. | HA |
| Bucket alignment | InMemory and Timescale produce identical bucket timestamps. Concatenation never produces duplicate-timestamp buckets. | HA |
| Multi-store dedup | Two FakeHistoryStore returning samples at identical timestamps: merge dedups via TryAdd, higher-priority value wins. | Unit |
| Retention | Synthetic old rows inserted; `add_retention_policy` job removes them. | HA |
| Backpressure | Throttled DB; queue overflows; oldest dropped; `DropCount` increments. | HA |
| Reconnect | Container restart mid-run; store reconnects and resumes. Toolkit re-probed. | HA |
| Shutdown flush | Pending batch flushed on `StopAsync`. Timeout path: pending dropped, status reflects loss. | HA |

The test project's README must note the Docker requirement. The fixtures skip with a clear message when the Docker daemon is unreachable, rather than hanging.

## Known Limitations

- **Property renames orphan prior history.** A property's `path` is its identity in the history store. Renaming a subject or property creates a new identity; samples recorded under the old path become unreachable through normal queries (they remain in the database under the old path until retention expires).
- **Type changes hide prior data.** Changing a property's declared type (e.g., `int` to `double`) shifts the read query to a different value column; samples recorded under the old type become invisible to the new query path. Avoid in production deployments.
- **Crash data loss.** On process crash (SIGKILL, OOM, etc.), the Timescale store's pending batch is lost. Up to `FlushInterval` of samples can be lost. Graceful shutdown via the configured flush timeout drains the batch normally.
- **InMemory-only data loss on restart.** Configurations using only `InMemoryHistoryStore` lose all history on every restart. InMemory is documented as a hot buffer / dev / test store, not a production substitute.
- **Live-edge bucket inaccuracy for large buckets.** When `bucket_size > MaxAge`, the rightmost bucket of a bucketed query may be missing up to `FlushInterval` of samples. Relative error is `FlushInterval / bucket_size` (≤1.7% for 5-min buckets, ≤0.14% for 1-h buckets, ≤0.006% for 24-h buckets). Operators who need pixel-perfect live-edge precision raise `MaxAge` accordingly. Post-MVP combinable-partial-aggregate refinement is noted.
- **Multi-instance store semantics.** Multiple `TimescaleDbHistoryStore` instances may be registered, but each records independently — multiple stores against the same database produce duplicate rows. HA / read-replica failover should be handled at the Npgsql connection layer (host list + failover mode), not by registering multiple store instances.
- **Cross-instance history sync.** No explicit history sync mechanism beyond what the existing WebSocket topology provides as a side effect.

## Open Questions / Future Work

### Fast follow

- **Runtime cross-store config validation.** Per-store check at `ExecuteAsync` startup that warns when `MaxAge < 2 * FlushInterval` of sibling Timescale stores. Adds a `ConfigurationWarning` `[State]` property.
- **Median / Percentile aggregations.** Format (`Percentile:p` colon syntax vs preset names like `Percentile95`) decided when implementing. Toolkit-backed `approx_percentile` for scale; native `percentile_cont` for exact.
- **TimescaleDB compression policy.** `add_compression_policy` typically yields 10× storage reduction on chunks older than the threshold. Requires `[Configuration] EnableCompression` and `CompressionAge` knobs plus bootstrap policy management. MVP already uses daily chunks to make this granular when it lands.
- **LTTB downsampling.** Toolkit-backed `lttb(value, ts, N)` for visual-shape-preserving sample reduction. Conceptually distinct from statistical aggregation (returns a subset of raw samples, not bucket aggregates). API shape decided at implementation time.
- **Per-range UI filter for aggregation dropdown.** Re-evaluate `TimeWeightedAverage` availability as the user changes the time range, so it shows for InMemory-only-coverage ranges even when Timescale doesn't support it.
- **Multi-property compare in the dialog.**
- **CSV/JSON export from the dialog.**

### Phase 2

- **`Rate` / `Delta` aggregations for cumulative properties.** Uses existing `StateAttribute.IsCumulative`. Toolkit's `counter_agg` for Timescale (handles counter resets); in-process equivalent for InMemory. Look-back infrastructure already in MVP.
- **`StateDuration` aggregation for discrete properties.** Uses existing `StateAttribute.IsDiscrete`. Open question: result shape (extend `HistoryPoint`, parallel `StructuredHistorySeries` type, or repurpose `HistoryPoint.Json` for per-state durations).
- **Server-side gap-fill with `Interpolate` mode.** Opt-in via new query field for linearly-varying numeric signals (temperature smoothing). MVP already does LOCF for `Last` automatically.
- **Recording complex types.** Records, value objects, dictionaries, lists of primitives. Schema thought required (one column or per-field expansion).
- **Numeric aggregation on `value_json`-stored properties.** Casting `value_json #>> '{}'` to `numeric` would let `Average`/etc. apply to decimal columns.
- **Streaming export of large raw ranges.** A separate `ExportAsync` method on `IHistoryStore` as the seam.
- **Continuous aggregates inside Timescale.** For long-retention downsampling, internal to the Timescale store, transparent to consumers.
- **Global `MaxTotalSamples` cap on InMemory** with cross-property eviction. Needs an eviction policy decision (drop from biggest buffer, oldest globally, LRU).

### Deferred

- **Property path normalisation.** `properties(id, path)` lookup table with smallint/int foreign key. Trigger: when `EstimatedStorageBytes` alarms an operator.
- **Property rename audit log + on-read column-fallback** to address rename and type-change limitations.
- **Schema migration framework.** MVP uses idempotent SQL (`CREATE ... IF NOT EXISTS` patterns) plus a seeded `history_schema_version` table. Engine deferred until the first migration requiring data movement or conditional logic.
- **`Mode`** for discrete properties (PG built-in `mode() within group`); **threshold-crossings** (`CrossingsAbove:N` etc.). Both useful but no concrete demand yet; threshold-crossings additionally blocked on parameterized-aggregation syntax decision.
- **Per-property opt-in/out attributes (`[Historize]` / `[NoHistory]`).**
- **Store-level include/exclude filter via glob patterns on property paths.**
- **`StoreNonNumericValues` toggle** to limit a store to numeric/bool history only.
