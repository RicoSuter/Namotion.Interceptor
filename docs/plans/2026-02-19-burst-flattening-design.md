# Burst Flattening for ChangeQueueProcessor

## Problem

The `ChangeQueueProcessor` currently flushes all deduplicated changes every tick (default 8ms). During heavy spikes (e.g., bulk sensor updates, reconnection floods), this can overload CPU with too many writes, pushing the system past sustainable levels. The goal is to keep the system below ~90% CPU usage to maintain eventual consistency without falling behind.

Key constraint: legitimate bursts (e.g., loading a recipe with 100 property updates) should pass through unthrottled if the system can handle them. The mechanism must only intervene when the write handler is actually struggling, not when load merely increases.

## Solution

Add adaptive, write-duration-driven burst flattening to the `ChangeQueueProcessor`. The system learns the per-item write cost at runtime and uses it to predict whether a batch will exceed the time budget *before* writing. When a batch is too large, the excess is carried over to the next tick where it re-deduplicates with new arrivals, flattening the burst across multiple windows.

Three layered mechanisms, each a safety net for the one above:

1. **Predictive carry-over**: Learn per-item write cost via dual EMA. Before each flush, predict write duration from batch size. If it exceeds the time budget, cap the batch and carry excess to the next tick. Carried-over changes merge and re-deduplicate with new arrivals.
2. **Flush skipping**: If actual write duration still exceeds the budget despite carry-over (e.g., EMA underestimates cost), skip ticks to widen the effective buffer window. This gives the EMA time to correct.
3. **Emergency drain**: If carry-over grows beyond a threshold (system genuinely can't keep up), give up throttling and flush everything. Better to spike the CPU than to accumulate infinite backlog and fall behind.

All mechanisms are opt-in and default to off, preserving existing behavior.

## API

New options class replaces the current `TimeSpan? bufferTime` constructor parameter (clean break, no backward-compat overload — pre-1.0 library):

```csharp
public class ChangeQueueProcessorOptions
{
    /// <summary>
    /// Time interval between flush ticks. Each tick, queued changes are
    /// deduplicated and sent to the write handler.
    /// Default: 8ms.
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Controls adaptive burst flattening behavior.
    /// When set to <see cref="BurstFlatteningMode.Adaptive"/>, the processor
    /// learns the per-item write cost at runtime and predictively caps batches
    /// to keep write duration within the time budget. Excess changes carry over
    /// to the next tick and re-deduplicate with new arrivals, flattening bursts
    /// across multiple windows. Flush skipping and emergency drain provide
    /// additional safety layers.
    /// Default: <see cref="BurstFlatteningMode.None"/>.
    /// </summary>
    public BurstFlatteningMode BurstFlattening { get; set; }
        = BurstFlatteningMode.None;

    /// <summary>
    /// Fraction of <see cref="BufferTime"/> used as the write duration budget.
    /// The processor predicts write duration from batch size and learned per-item
    /// cost. When predicted duration exceeds BufferTime * WriteTimeBudgetRatio,
    /// the batch is capped and excess changes are carried over.
    /// Lower values leave more headroom but reduce throughput.
    /// Default: 0.8 (80% of BufferTime).
    /// </summary>
    public double WriteTimeBudgetRatio { get; set; } = 0.8;

    /// <summary>
    /// Maximum number of changes held in carry-over before emergency drain.
    /// When carry-over exceeds this threshold, the processor stops throttling
    /// and flushes everything — accepting a CPU spike rather than accumulating
    /// infinite backlog. A warning is logged when this occurs.
    /// Default: 10000.
    /// </summary>
    public int MaxCarryOverCount { get; set; } = 10_000;
}

/// <summary>
/// Controls how the <see cref="ChangeQueueProcessor"/> handles bursts
/// of property changes.
/// </summary>
public enum BurstFlatteningMode
{
    /// <summary>
    /// No burst flattening. Every flush tick sends all deduplicated changes
    /// to the write handler. This is the default and matches the original behavior.
    /// </summary>
    None,

    /// <summary>
    /// Enables the full adaptive burst flattening system:
    /// <list type="bullet">
    /// <item>Learns per-item write cost via dual EMA (stable across batch sizes)</item>
    /// <item>Predicts write duration before each flush and caps the batch to stay
    /// within the time budget (BufferTime * WriteTimeBudgetRatio)</item>
    /// <item>Excess changes carry over and re-deduplicate with next tick's arrivals</item>
    /// <item>Flush skipping activates if write duration still exceeds budget</item>
    /// <item>Emergency drain flushes everything if carry-over exceeds MaxCarryOverCount</item>
    /// </list>
    /// Legitimate bursts (e.g., recipe loads) pass through unthrottled as long as
    /// the write handler completes within the time budget.
    /// </summary>
    Adaptive
}
```

Constructor signature:

```csharp
public ChangeQueueProcessor(
    object? source,
    IInterceptorSubjectContext context,
    Func<RegisteredSubjectProperty, bool> propertyFilter,
    Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
    ChangeQueueProcessorOptions options,
    ILogger logger)
```

## Algorithm

### Per-Item Cost Learning (Dual EMA)

After each flush, update two separate EMAs for write duration and item count:

```
durationEma = α * actualWriteDuration + (1 - α) * durationEma
itemsEma    = α * itemsSent           + (1 - α) * itemsEma
perItemCost = durationEma / itemsEma
```

The alpha (α) is derived from a ~1 second smoothing window relative to `BufferTime`.

**Why dual EMA?** Tracking duration and items separately and dividing naturally weights larger batches more heavily. A 1000-item batch contributes 1000x more to `itemsEma` than a 1-item batch. The resulting ratio reflects throughput-weighted per-item cost — the correct metric for predicting large-batch performance. This makes the estimate stable across varying batch sizes.

**Initialization:** On the first flush, no prediction is available. All changes are sent (calibration tick). The first measurement seeds both EMAs.

**Idle behavior:** The EMA only updates after non-empty flushes (when items are actually sent to the write handler). Empty ticks are skipped. This means the ~1 second window refers to ~1 second worth of non-empty flushes, not wall-clock time. If the system is idle for 30 seconds, the EMA retains its last known value. This is correct because per-item cost is a property of the write handler's performance, not of idle time. Idle ticks diluting the EMA would destroy the learned cost and force recalibration after every idle period. If system performance changed during idle (e.g., other processes consuming CPU), the EMA corrects within a few non-empty flushes, and flush skipping (Layer 2) catches any misprediction from stale values.

**Guard:** `itemsEma` has a minimum floor (e.g., 1.0) to prevent division by zero during early ticks.

### Predictive Carry-Over (Layer 1)

Before writing, predict whether the batch fits within the time budget:

```
budget   = bufferTime * writeTimeBudgetRatio    // e.g., 8ms * 0.8 = 6.4ms
maxItems = budget / perItemCost                 // e.g., 6.4ms / 0.04ms = 160 items
```

**If `dedupedCount <= maxItems`:** Send all changes (including any carry-over merged this tick). System is healthy. No intervention.

**If `dedupedCount > maxItems`:** Send the first `maxItems` changes, carry over the rest. The "send first, carry rest" order preserves chronological ordering within each batch. Eventual consistency is guaranteed because carry-over re-deduplicates with new arrivals (latest value wins).

### Carry-Over Merge (Start of Each Tick)

When carry-over exists from a previous tick:

1. Drain `ConcurrentQueue` into scratch buffer (new arrivals)
2. Merge with carried-over changes: for each property, newer values overwrite carried-over values
3. Deduplicate the combined set
4. Apply the predictive cap to the merged result

This produces progressively more aggressive deduplication under sustained load. The longer changes sit in carry-over, the more same-property updates collapse into single writes.

### Flush Skipping (Layer 2)

After writing, measure the actual write duration. If carry-over is active and actual write duration *still* exceeds the budget (meaning the per-item cost EMA underestimated), adjust the skip counter:

- Actual write duration > budget with carry-over active: increment skip counter (max 4)
- Actual write duration <= budget or no carry-over: decrement skip counter toward 0

The skip counter determines flush frequency: `tickCount % (skipCounter + 1) == 0`. Skipping ticks widens the effective buffer window, allowing more changes to accumulate and deduplicate per cycle. It also gives the dual EMA time to converge to the correct per-item cost.

Maximum skip = 4 → flush every 5th tick (40ms at 8ms buffer). The counter is capped regardless of signal intensity.

### Emergency Drain (Layer 3)

If `carryOverCount > MaxCarryOverCount`:

1. Stop throttling — send all deduped changes including carry-over
2. Log a warning (the system is genuinely overloaded and needs tuning)
3. Reset carry-over to empty
4. Next tick re-evaluates from a clean state

This ensures:
- Memory stays bounded (no infinite backlog)
- Eventual consistency is maintained (latest values reach downstream)
- The system degrades to "current behavior" under extreme overload rather than falling behind
- The user gets a clear signal their system needs tuning

### Example Flow

System with 8ms buffer, 80% budget (6.4ms), learned per-item cost of 0.04ms:

| Tick | Arrivals | Carry-in | After merge+dedup | maxItems | Sent | Carry-out | Write duration |
|---|---|---|---|---|---|---|---|
| N | 50 | 0 | 50 | 160 | 50 | 0 | 2ms (healthy) |
| N+1 | 2000 (burst) | 0 | 2000 | 160 | 160 | 1840 | 6.2ms |
| N+2 | 50 | 1840 | ~1850 (dedup) | 160 | 160 | ~1690 | 6.1ms |
| N+3 | 30 | ~1690 | ~1650 (more dedup) | 160 | 160 | ~1490 | 6.0ms |
| ... | (burst subsides, same-property updates collapse in carry-over) | | | | | | |
| N+12 | 20 | ~100 | ~110 | 160 | 110 | 0 | 4.4ms (recovered) |

The burst is flattened across ~12 ticks at the system's proven sustainable rate. CPU never spikes because each flush stays within budget. If the 2000 changes are mostly to the same properties (common in sensor scenarios), dedup collapses them much faster.

## Diagnostics

Expose read-only diagnostic properties on `ChangeQueueProcessor`. Background services expose these through their own diagnostic properties.

### Context-Level Diagnostics Access

The `ChangeQueueProcessor` registers a read-only diagnostics interface on the `IInterceptorSubjectContext` when created, enabling any component with context access to read burst flattening metrics without a direct reference to the processor.

### PerformanceProfiler Integration

The `PerformanceProfiler` (in `Namotion.Interceptor.SamplesModel`) reads burst flattening diagnostics from the context and includes them in the periodic stats output. When burst flattening is enabled, the profiler prints an additional section:

```
Burst Flattening
  Per-item write cost:       0.04 ms
  Max items per flush:       160
  Carry-over count:          0
  Skip counter:              0
  Flushed throughput:        2500.00 changes/s
  Received throughput:       2500.00 changes/s
```

This enables hardware comparison: run the same sample app on different machines and compare the `Per-item write cost` to evaluate which CPU/hardware fits the application's throughput requirements. The `Max items per flush` derived from it shows the effective capacity of each system.

All sample apps (OPC UA, MQTT, WebSocket — both client and server) already create a `PerformanceProfiler` and will automatically show these metrics when burst flattening is enabled.

### Diagnostic Properties

```csharp
/// <summary>Total changes sent to the write handler (lifetime counter).</summary>
public long TotalFlushedChanges => Volatile.Read(ref _totalFlushedChanges);

/// <summary>Total changes received from the subscription (lifetime counter).</summary>
public long TotalReceivedChanges => Volatile.Read(ref _totalReceivedChanges);

/// <summary>
/// Smoothed rate of changes sent to the write handler, in changes per second.
/// Computed using an EMA over actual elapsed time between flushes,
/// so it remains accurate even when flush skipping widens the interval.
/// </summary>
public double FlushedThroughputPerSecond { get; }

/// <summary>
/// Smoothed rate of changes received from the subscription, in changes per second.
/// The difference between received and flushed throughput shows how much
/// deduplication and carry-over are reducing write volume in real time.
/// </summary>
public double ReceivedThroughputPerSecond { get; }

/// <summary>Current number of changes held in carry-over.</summary>
public int CurrentCarryOverCount { get; }

/// <summary>Current flush skip level (0 = every tick, 4 = every 5th tick).</summary>
public int CurrentSkipCounter { get; }

/// <summary>Current learned per-item write cost in milliseconds.</summary>
public double PerItemWriteCostMs { get; }

/// <summary>Current maximum items per flush (derived from per-item cost and budget).</summary>
public int CurrentMaxItemsPerFlush { get; }
```

### Throughput Calculation

After each flush, throughput is computed from actual elapsed time and smoothed with an EMA:

```csharp
var elapsed = Stopwatch.GetElapsedTime(_lastFlushTimestamp);
_lastFlushTimestamp = Stopwatch.GetTimestamp();

var instantFlushedRate = flushedCount / elapsed.TotalSeconds;
var instantReceivedRate = receivedCount / elapsed.TotalSeconds;

_flushedThroughput = alpha * instantFlushedRate + (1 - alpha) * _flushedThroughput;
_receivedThroughput = alpha * instantReceivedRate + (1 - alpha) * _receivedThroughput;
```

Using actual elapsed time (via `Stopwatch`) instead of assuming `BufferTime` ensures accuracy when flush skipping widens the interval. The EMA smoothing (alpha derived from a ~1 second window) filters out per-tick noise while remaining responsive to real trends.

Cost: one `Stopwatch.GetTimestamp()` call + two multiply-adds per flush. Effectively free. This throughput EMA is purely diagnostic — it is not used for throttling decisions. The per-item cost dual EMA (used for throttling) is a separate calculation.

## Performance

- **When disabled** (`None`): Zero overhead. Code path is identical to today via a simple `if` branch. Diagnostic throughput EMA is still updated (two multiply-adds per flush — negligible).
- **When enabled** (`Adaptive`): Allocation-free. Per-item cost learning is two multiply-adds per flush plus one `Stopwatch.GetElapsedTime` call. Predictive cap is one division per flush. Carry-over reuses the existing `ArrayPool`-rented buffer. Skip counter is an `int` field. Emergency drain threshold is one comparison. No LINQ, no closures, no boxing.

## Testability

Key internal state is exposed via `internal` properties with `[InternalsVisibleTo]` for the test project:

- Carry-over count and contents
- Skip counter
- Per-item cost EMA values (duration, items, derived cost)
- Last write duration measurement
- Max items per flush (derived)

This replaces the current reflection-based test pattern with proper internal test seams.

## Callers

Four production call sites need updating (clean break — old constructor removed):

1. `SubjectSourceBackgroundService` — accepts `ChangeQueueProcessorOptions` instead of `TimeSpan? bufferTime`
2. `MqttSubjectServerBackgroundService` — constructs options from `MqttServerConfiguration`
3. `OpcUaSubjectServerBackgroundService` — constructs options from `OpcUaServerConfiguration`
4. `WebSocketSubjectHandler.CreateChangeQueueProcessor` — constructs options from `WebSocketServerConfiguration`

Each connector's configuration class gains `BurstFlattening` (default `None`) plus optionally `WriteTimeBudgetRatio` and `MaxCarryOverCount` with defaults that preserve existing behavior.

## Testing

New tests for `ChangeQueueProcessorTests` (using internal test seams, not reflection):

1. No throttling on first flush (calibration tick sends everything)
2. Per-item cost EMA converges after multiple flushes of varying size
3. Predictive cap activates when predicted duration exceeds budget
4. Carry-over merges and re-deduplicates with new arrivals
5. Carry-over drains when batch fits within budget (burst recovery)
6. Legitimate burst passes through if write duration stays within budget
7. Flush skipping activates when actual write duration exceeds budget despite carry-over
8. Flush skipping recovers (counter decrements) when write duration improves
9. Emergency drain triggers when carry-over exceeds MaxCarryOverCount
10. Emergency drain logs warning and resets carry-over
11. Features disabled (`None`) = current behavior unchanged, zero overhead
12. Always sends at least some items (never fully stalls — min 10% or maxItems >= 1)
13. Diagnostic counters and per-item cost diagnostics are correct

## Baseline Benchmark (Pre-Implementation)

Run on 2026-02-19, master branch at commit `fd38d148`.

```
BenchmarkDotNet v0.15.5, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
11th Gen Intel Core i7-11700K 3.60GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 9.0.11 (9.0.11, 9.0.1125.51716), X64 RyuJIT x86-64-v4

| Method                  | Mean     | Error     | StdDev    | Allocated |
|------------------------ |---------:|----------:|----------:|----------:|
| WriteToRegistrySubjects | 2.328 ms | 0.0071 ms | 0.0063 ms |         - |
| WriteToSource           | 1.716 ms | 0.0116 ms | 0.0103 ms |         - |
```

- **WriteToRegistrySubjects**: 1M property writes through the full pipeline (queue -> dedup -> write handler). Buffer time: 1ms.
- **WriteToSource**: 5000 property writes through the source pipeline. Buffer time: 1ms.
- **Allocated: 0 bytes** — confirms the current implementation is allocation-free.

These numbers serve as the baseline to verify the implementation introduces no performance regression when burst flattening is disabled (`None`).

## Documentation

After implementation, update the project documentation (`docs/` folder) with:

- **Burst flattening configuration guide**: How to enable adaptive burst flattening, what each option does, recommended settings for different scenarios (high-throughput sensor data, low-latency UI updates, etc.)
- **Algorithm explanation**: How the per-item cost EMA works, the three-layer safety system (carry-over → flush skipping → emergency drain), and idle behavior (EMA retains last value during inactivity, corrects within a few non-empty flushes)
- **Diagnostics reference**: Available diagnostic properties (`PerItemWriteCostMs`, `CurrentMaxItemsPerFlush`, throughput counters, etc.) and how to use them for hardware comparison and capacity planning
- **PerformanceProfiler output**: What the burst flattening section in the profiler output means and how to interpret it for performance tuning
