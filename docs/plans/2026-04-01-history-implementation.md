# History System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add time-series history recording and querying to HomeBlaze with a two-stage buffered write path, in-memory and SQLite storage, move tracking, periodic graph snapshots, and MCP tool integration.

**Architecture:** `HistoryService` (BackgroundService) subscribes to property changes via its own `ChangeQueueProcessor`, buffers changes, and fans out to sink subjects discovered via lifecycle events. Per-sink flush intervals via cursor-based shared buffer. `InMemoryHistorySink` for testing and fast lookups. `SqliteHistorySink` stores data in partitioned SQLite databases. MCP tools in `HomeBlaze.AI` query via `IHistoryReader` from `HomeBlaze.History.Abstractions`.

**Tech Stack:** .NET 10, C# 13, Microsoft.Data.Sqlite, System.Text.Json, xUnit

**Design doc:** `docs/plans/2026-04-01-history-design.md`

---

### Task 1: Move StateAttributePathProvider to HomeBlaze.Services

**Why:** `StateAttributePathProvider` lives in `HomeBlaze.AI.Mcp` but is needed by `HomeBlaze.History`. Moving it to `HomeBlaze.Services` avoids a circular dependency and properly locates a general-purpose path provider.

**Files:**
- Move: `src/HomeBlaze/HomeBlaze.AI/Mcp/StateAttributePathProvider.cs` → `src/HomeBlaze/HomeBlaze.Services/StateAttributePathProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/Mcp/` — update imports referencing the old location
- Modify: `src/HomeBlaze/HomeBlaze.AI.Tests/Mcp/StateAttributePathProviderTests.cs` — update namespace

**Step 1:** Move the file and update namespace from `HomeBlaze.AI.Mcp` to `HomeBlaze.Services`.

**Step 2:** Update all references in `HomeBlaze.AI` to use the new namespace. Search for `using HomeBlaze.AI.Mcp` where `StateAttributePathProvider` is used.

**Step 3:** Build and run existing tests:
```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/HomeBlaze/HomeBlaze.AI.Tests/ --no-restore
```
Expected: PASS (no behavior change)

**Step 4:** Commit.

---

### Task 2: Extract ISubjectPathResolver Interface

**Why:** `HistoryService` needs path resolution. Extracting an interface allows mock path resolvers in unit tests (no `RootManager` dependency).

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Services/ISubjectPathResolver.cs`
- Modify: `src/HomeBlaze/HomeBlaze.Services/SubjectPathResolver.cs` — implement interface

**Step 1:** Create interface:

```csharp
// src/HomeBlaze/HomeBlaze.Services/ISubjectPathResolver.cs
namespace HomeBlaze.Services;

public interface ISubjectPathResolver
{
    string? GetPath(IInterceptorSubject subject, PathStyle style);
    IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style);
    IInterceptorSubject? ResolveSubject(string path, PathStyle style, IInterceptorSubject? relativeTo = null);
}
```

**Step 2:** Add `: ISubjectPathResolver` to `SubjectPathResolver` class declaration.

**Step 3:** Register as interface in DI (check `ServiceCollectionExtensions` in `HomeBlaze.Services`).

**Step 4:** Build:
```bash
dotnet build src/Namotion.Interceptor.slnx
```
Expected: Build succeeded

**Step 5:** Commit.

---

### Task 3: Create HomeBlaze.History.Abstractions Project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/AggregationType.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryValueType.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryPartitionInterval.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryRecord.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQuery.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/AggregatedRecord.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/MoveRecord.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistorySnapshot.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistorySubjectSnapshot.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/ResolvedHistoryRecord.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/IHistoryWriter.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/IHistoryReader.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/IHistorySink.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1:** Create .csproj (lightweight, no dependencies):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

**Step 2:** Create enums:

```csharp
// AggregationType.cs
namespace HomeBlaze.History;

public enum AggregationType
{
    Average,      // time-weighted: sum(value_i * duration_i) / sum(duration_i)
    SampleMean,   // count-weighted: sum(value_i) / count
    Minimum, Maximum, Sum, Count, First, Last
}
```

```csharp
// HistoryValueType.cs
namespace HomeBlaze.History;

public enum HistoryValueType : byte
{
    Null, Double, Boolean, String, Complex
}
```

```csharp
// HistoryPartitionInterval.cs
namespace HomeBlaze.History;

public enum HistoryPartitionInterval
{
    Daily, Weekly, Monthly
}
```

**Step 3:** Create data model records:

```csharp
// HistoryRecord.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record HistoryRecord(
    string SubjectPath,
    string PropertyName,
    DateTimeOffset Timestamp,
    JsonElement Value);
```

```csharp
// HistoryQuery.cs
namespace HomeBlaze.History;

public record HistoryQuery(
    string SubjectPath,
    IReadOnlyList<string> PropertyNames,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? BucketSize = null,
    AggregationType? Aggregation = null);
```

```csharp
// AggregatedRecord.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record AggregatedRecord(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    double? Average,        // time-weighted
    double? SampleMean,     // count-weighted (raw sample mean)
    double? Minimum, double? Maximum,
    double? Sum, long Count,
    JsonElement? First, JsonElement? Last,
    // Internal fields used to merge buckets across partitions and the buffer/sink legs.
    // Populated when Aggregation == Average or SampleMean; null otherwise.
    double? WeightedSum = null,         // sum(value_i * duration_i)
    long? TotalDurationTicks = null);   // sum(duration_i) in ticks
```

```csharp
// MoveRecord.cs
namespace HomeBlaze.History;

public record MoveRecord(
    DateTimeOffset Timestamp,
    string FromPath,
    string ToPath);
```

```csharp
// HistorySnapshot.cs
namespace HomeBlaze.History;

public record HistorySnapshot(
    DateTimeOffset Timestamp,
    string BasePath,
    IReadOnlyDictionary<string, HistorySubjectSnapshot> Subjects);
```

```csharp
// HistorySubjectSnapshot.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record HistorySubjectSnapshot(
    IReadOnlyDictionary<string, JsonElement> Properties);
```

```csharp
// ResolvedHistoryRecord.cs
namespace HomeBlaze.History;

public readonly struct ResolvedHistoryRecord
{
    public readonly string SubjectPath;
    public readonly string PropertyName;
    public readonly long TimestampTicks;
    public readonly HistoryValueType ValueType;
    public readonly double NumericValue;
    public readonly ReadOnlyMemory<byte> RawValue;

    public ResolvedHistoryRecord(
        string subjectPath,
        string propertyName,
        long timestampTicks,
        HistoryValueType valueType,
        double numericValue,
        ReadOnlyMemory<byte> rawValue)
    {
        SubjectPath = subjectPath;
        PropertyName = propertyName;
        TimestampTicks = timestampTicks;
        ValueType = valueType;
        NumericValue = numericValue;
        RawValue = rawValue;
    }
}
```

**Step 4:** Create interfaces:

```csharp
// IHistoryWriter.cs
namespace HomeBlaze.History;

public interface IHistoryWriter
{
    Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    Task WriteSnapshotAsync(HistorySnapshot snapshot);
    Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
}
```

```csharp
// IHistoryReader.cs
namespace HomeBlaze.History;

public interface IHistoryReader
{
    int Priority { get; }
    DateTimeOffset? OldestRecord { get; }
    TimeSpan MinAge { get; }   // buffered-flush blind spot; routing uses [now - MaxAge, now - MinAge]
    TimeSpan MaxAge { get; }   // configured retention horizon
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

```csharp
// IHistorySink.cs
namespace HomeBlaze.History;

public interface IHistorySink : IHistoryWriter, IHistoryReader { }
```

**Step 5:** Add project to solution:
```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj --solution-folder /HomeBlaze/Abstractions
```

**Step 6:** Build:
```bash
dotnet build src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj
```
Expected: Build succeeded

**Step 7:** Commit.

---

### Task 4: Create HomeBlaze.History Project with HistorySinkBase, InMemoryHistorySink, and HistoryService Skeleton

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History/HistorySinkBase.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/InMemoryHistorySink.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/HistoryServiceExtensions.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/HistoryRecordExtensions.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1:** Create .csproj. Reference pattern from `HomeBlaze.Services.csproj`. HistorySinkBase is `[InterceptorSubject]`, needs source generator:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="HomeBlaze.History.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
    <ProjectReference Include="..\HomeBlaze.Services\HomeBlaze.Services.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Tracking\Namotion.Interceptor.Tracking.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Connectors\Namotion.Interceptor.Connectors.csproj" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
  </ItemGroup>
</Project>
```

**Step 2:** Create HistorySinkBase. Note: `RecordsWritten`, `Status` are plain properties (not `[State]`) to avoid self-recording feedback loop. `LastSnapshotTime` is replaced by `GetLatestSnapshotTimeAsync()` which queries the sink's storage:

```csharp
// src/HomeBlaze/HomeBlaze.History/HistorySinkBase.cs
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History;

[InterceptorSubject]
public abstract partial class HistorySinkBase : IHistorySink
{
    [Configuration]
    public partial TimeSpan FlushInterval { get; set; }  // default 10s

    [Configuration]
    public partial TimeSpan SnapshotInterval { get; set; }  // default 1 day

    [Configuration]
    public partial TimeSpan MaxAge { get; set; }  // default 365 days; TimeSpan.Zero = unlimited

    [Configuration]
    public partial int Priority { get; set; }  // default 100

    // Plain properties (NOT [State]) to avoid self-recording feedback loop
    public long RecordsWritten { get; set; }
    public string? Status { get; set; }

    int IHistoryReader.Priority => Priority;
    TimeSpan IHistoryReader.MaxAge => MaxAge;

    // MinAge defaults to FlushInterval for persistent sinks (the buffered-flush blind spot).
    // InMemoryHistorySink overrides to TimeSpan.Zero because it queries the buffer directly.
    public virtual TimeSpan MinAge => FlushInterval;

    public abstract DateTimeOffset? OldestRecord { get; }
    public abstract bool SupportsNativeAggregation { get; }

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

**Step 3:** Create InMemoryHistorySink (full implementation — used for testing and fast lookups):

```csharp
// src/HomeBlaze/HomeBlaze.History/InMemoryHistorySink.cs
using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History;

[InterceptorSubject]
public partial class InMemoryHistorySink : HistorySinkBase
{
    private readonly List<ResolvedHistoryRecord> _records = new();
    private readonly List<MoveRecord> _moves = new();
    private readonly List<HistorySnapshot> _snapshots = new();
    private readonly Lock _lock = new();

    public InMemoryHistorySink()
    {
        // Override base defaults for the hot-cache role.
        FlushInterval = TimeSpan.FromSeconds(1);
        MaxAge = TimeSpan.FromMinutes(10);
    }

    public override TimeSpan MinAge => TimeSpan.Zero;  // queries hit the in-memory list directly

    public override DateTimeOffset? OldestRecord
    {
        get
        {
            lock (_lock)
            {
                return _records.Count > 0
                    ? new DateTimeOffset(_records[0].TimestampTicks, TimeSpan.Zero)
                    : null;
            }
        }
    }

    public override bool SupportsNativeAggregation => true;

    // --- Write Side ---

    public override Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records)
    {
        lock (_lock)
        {
            for (var i = 0; i < records.Length; i++)
                _records.Add(records.Span[i]);

            Evict();
            RecordsWritten += records.Length;
            Status = "Connected";
        }
        return Task.CompletedTask;
    }

    public override Task WriteSnapshotAsync(HistorySnapshot snapshot)
    {
        lock (_lock)
        {
            _snapshots.Add(snapshot);
            // No LastSnapshotTime field — GetLatestSnapshotTimeAsync reads from _snapshots
        }
        return Task.CompletedTask;
    }

    public override Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves)
    {
        lock (_lock)
        {
            for (var i = 0; i < moves.Length; i++)
                _moves.Add(moves.Span[i]);
        }
        return Task.CompletedTask;
    }

    public override Task<DateTimeOffset?> GetLatestSnapshotTimeAsync()
    {
        lock (_lock)
        {
            var latest = _snapshots.Count > 0
                ? _snapshots[^1].Timestamp
                : (DateTimeOffset?)null;
            return Task.FromResult(latest);
        }
    }

    // --- Read Side ---

    public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
    {
        lock (_lock)
        {
            // Always follow moves — transparent path chain resolution
            var paths = ResolvePathChain(query.SubjectPath, query.From, query.To);

            var results = new List<HistoryRecord>();
            foreach (var (path, from, to) in paths)
            {
                results.AddRange(_records
                    .Where(r => r.SubjectPath == path
                        && query.PropertyNames.Contains(r.PropertyName)
                        && r.TimestampTicks >= from.Ticks
                        && r.TimestampTicks <= to.Ticks)
                    .Select(r => new HistoryRecord(
                        r.SubjectPath, r.PropertyName,
                        new DateTimeOffset(r.TimestampTicks, TimeSpan.Zero),
                        DeserializeValue(r))));
            }

            return Task.FromResult<IReadOnlyList<HistoryRecord>>(
                results.OrderBy(r => r.Timestamp).ToList());
        }
    }

    public override Task<IReadOnlyList<AggregatedRecord>> QueryAggregatedAsync(HistoryQuery query)
    {
        lock (_lock)
        {
            if (query.BucketSize is null || query.Aggregation is null)
                return Task.FromResult<IReadOnlyList<AggregatedRecord>>(Array.Empty<AggregatedRecord>());

            // Always follow moves: transparent path chain resolution
            var paths = ResolvePathChain(query.SubjectPath, query.From, query.To);

            var bucketTicks = query.BucketSize.Value.Ticks;
            var queryFromTicks = query.From.Ticks;
            var queryEndTicks = query.To.Ticks;

            // Step 1: collect in-range numeric samples per property, AND the carry sample
            // (the latest record with timestamp < query.From) so the first bucket has a
            // starting value rather than appearing empty until the next change arrives.
            var rowsByProperty = new Dictionary<string, List<(long timestamp, double value)>>();
            foreach (var propertyName in query.PropertyNames)
                rowsByProperty[propertyName] = new();

            foreach (var (path, fromInPath, toInPath) in paths)
            {
                var fromTicks = Math.Max(fromInPath.Ticks, queryFromTicks);
                var toTicks = Math.Min(toInPath.Ticks, queryEndTicks);

                // In-range rows
                foreach (var r in _records)
                {
                    if (r.SubjectPath != path) continue;
                    if (!query.PropertyNames.Contains(r.PropertyName)) continue;
                    if (r.ValueType is not (HistoryValueType.Double or HistoryValueType.Boolean)) continue;
                    if (r.TimestampTicks < fromTicks || r.TimestampTicks > toTicks) continue;
                    rowsByProperty[r.PropertyName].Add((r.TimestampTicks, r.NumericValue));
                }

                // Carry sample per property: latest numeric record with timestamp < fromTicks
                foreach (var propertyName in query.PropertyNames)
                {
                    var carry = _records
                        .Where(r => r.SubjectPath == path
                            && r.PropertyName == propertyName
                            && r.TimestampTicks < fromTicks
                            && r.ValueType is HistoryValueType.Double or HistoryValueType.Boolean)
                        .OrderByDescending(r => r.TimestampTicks)
                        .FirstOrDefault();
                    if (carry.PropertyName is not null)
                        rowsByProperty[propertyName].Add((carry.TimestampTicks, carry.NumericValue));
                }
            }

            // Sort each property's rows by timestamp ascending so LEAD-like next-timestamp works.
            foreach (var propertyName in rowsByProperty.Keys.ToList())
                rowsByProperty[propertyName] = rowsByProperty[propertyName]
                    .OrderBy(r => r.timestamp).ToList();

            // Step 2: emit one AggregatedRecord per (property, bucket) for buckets that overlap
            // the query range. Iterate buckets explicitly so a bucket inheriting a carry value
            // is emitted even if no change happens inside it.
            var results = new List<AggregatedRecord>();
            var firstBucketStart = (queryFromTicks / bucketTicks) * bucketTicks;
            var lastBucketStart  = ((queryEndTicks - 1) / bucketTicks) * bucketTicks;

            foreach (var (propertyName, rows) in rowsByProperty)
            {
                if (rows.Count == 0) continue;

                for (var bs = firstBucketStart; bs <= lastBucketStart; bs += bucketTicks)
                {
                    var be = bs + bucketTicks;
                    double weightedSum = 0.0;
                    long totalDuration = 0;
                    double sum = 0.0;
                    long count = 0;
                    double min = double.PositiveInfinity, max = double.NegativeInfinity;
                    double? firstValue = null, lastValue = null;

                    for (var i = 0; i < rows.Count; i++)
                    {
                        var (ts, value) = rows[i];
                        var nextTs = i + 1 < rows.Count ? rows[i + 1].timestamp : queryEndTicks;

                        // The sample's validity interval is [ts, nextTs). Clip to bucket [bs, be).
                        var effStart = Math.Max(ts, bs);
                        var effEnd = Math.Min(nextTs, be);
                        if (effEnd <= effStart) continue;

                        var duration = effEnd - effStart;
                        weightedSum += value * duration;
                        totalDuration += duration;

                        // Sample-mean / min / max / first / last only count samples whose own
                        // timestamp falls inside the bucket. Carry samples contribute to
                        // weighted-Average only.
                        if (ts >= bs && ts < be)
                        {
                            sum += value;
                            count++;
                            if (value < min) min = value;
                            if (value > max) max = value;
                            firstValue ??= value;
                            lastValue = value;
                        }
                    }

                    if (totalDuration <= 0) continue;  // bucket has no coverage at all

                    results.Add(new AggregatedRecord(
                        new DateTimeOffset(bs, TimeSpan.Zero),
                        new DateTimeOffset(be, TimeSpan.Zero),
                        Average: weightedSum / totalDuration,
                        SampleMean: count > 0 ? sum / count : null,
                        Minimum: count > 0 ? min : null,
                        Maximum: count > 0 ? max : null,
                        Sum: count > 0 ? sum : null,
                        Count: count,
                        First: firstValue is null ? null : JsonSerializer.SerializeToElement(firstValue.Value),
                        Last:  lastValue  is null ? null : JsonSerializer.SerializeToElement(lastValue.Value),
                        WeightedSum: weightedSum,
                        TotalDurationTicks: totalDuration));
                }
            }

            return Task.FromResult<IReadOnlyList<AggregatedRecord>>(
                results.OrderBy(r => r.BucketStart).ToList());
        }
    }

    public override Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time)
    {
        lock (_lock)
        {
            // Find nearest snapshot before time (scan backwards)
            HistorySnapshot? baseSnapshot = null;
            for (var i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (_snapshots[i].Timestamp <= time)
                {
                    baseSnapshot = _snapshots[i];
                    break;
                }
            }

            var mutableSubjects = baseSnapshot?.Subjects
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new Dictionary<string, JsonElement>(kvp.Value.Properties))
                ?? new Dictionary<string, Dictionary<string, JsonElement>>();

            var snapshotTime = baseSnapshot?.Timestamp ?? DateTimeOffset.MinValue;

            // Replay changes between snapshot and target time
            foreach (var record in _records
                .Where(r => r.TimestampTicks > snapshotTime.Ticks && r.TimestampTicks <= time.Ticks)
                .OrderBy(r => r.TimestampTicks))
            {
                if (path != "/" && !record.SubjectPath.StartsWith(path))
                    continue;

                if (!mutableSubjects.TryGetValue(record.SubjectPath, out var properties))
                {
                    properties = new Dictionary<string, JsonElement>();
                    mutableSubjects[record.SubjectPath] = properties;
                }
                properties[record.PropertyName] = DeserializeValue(record);
            }

            var filteredSubjects = mutableSubjects
                .Where(kvp => path == "/" || kvp.Key.StartsWith(path))
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new HistorySubjectSnapshot(kvp.Value));

            return Task.FromResult(new HistorySnapshot(time, path, filteredSubjects));
        }
    }

    public override async IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to, TimeSpan interval)
    {
        var current = from;
        while (current <= to)
        {
            yield return await GetSnapshotAsync(path, current);
            current = current.Add(interval);
        }
    }

    // --- Move Tracking ---

    private List<(string path, DateTimeOffset from, DateTimeOffset to)> ResolvePathChain(
        string currentPath, DateTimeOffset queryFrom, DateTimeOffset queryTo)
    {
        var result = new List<(string, DateTimeOffset, DateTimeOffset)>();
        var visited = new HashSet<string>();
        ResolvePathChainRecursive(currentPath, queryFrom, queryTo, result, visited);
        return result;
    }

    private void ResolvePathChainRecursive(
        string path, DateTimeOffset from, DateTimeOffset to,
        List<(string, DateTimeOffset, DateTimeOffset)> result,
        HashSet<string> visited)
    {
        if (!visited.Add(path))
            return;

        // Find when this path became active (latest move TO this path before 'to')
        var moveIn = _moves
            .Where(m => m.ToPath == path && m.Timestamp <= to)
            .OrderByDescending(m => m.Timestamp)
            .FirstOrDefault();

        var effectiveFrom = moveIn is not null && moveIn.Timestamp > from
            ? moveIn.Timestamp : from;

        // Find when this path became inactive (earliest move FROM this path after effectiveFrom)
        var moveOut = _moves
            .Where(m => m.FromPath == path && m.Timestamp >= effectiveFrom)
            .OrderBy(m => m.Timestamp)
            .FirstOrDefault();

        var effectiveTo = moveOut is not null && moveOut.Timestamp < to
            ? moveOut.Timestamp : to;

        result.Add((path, effectiveFrom, effectiveTo));

        // Follow backwards: if there was a move to this path, trace the previous path
        if (moveIn is not null && moveIn.Timestamp > from)
        {
            ResolvePathChainRecursive(moveIn.FromPath, from, moveIn.Timestamp, result, visited);
        }
    }

    // --- Helpers ---

    private void Evict()
    {
        if (MaxAge <= TimeSpan.Zero)
            return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(MaxAge).Ticks;
        _records.RemoveAll(r => r.TimestampTicks < cutoff);
        _snapshots.RemoveAll(s => s.Timestamp.Ticks < cutoff);
        _moves.RemoveAll(m => m.Timestamp.Ticks < cutoff);
    }

    private static JsonElement DeserializeValue(ResolvedHistoryRecord record)
    {
        return record.ValueType switch
        {
            HistoryValueType.Null => JsonSerializer.SerializeToElement<object?>(null),
            HistoryValueType.Double => JsonSerializer.SerializeToElement(record.NumericValue),
            HistoryValueType.Boolean => JsonSerializer.SerializeToElement(record.NumericValue != 0.0),
            HistoryValueType.String or HistoryValueType.Complex =>
                JsonSerializer.Deserialize<JsonElement>(record.RawValue.Span),
            _ => JsonSerializer.SerializeToElement<object?>(null)
        };
    }
}
```

**Step 4:** Create HistoryService skeleton:

```csharp
// src/HomeBlaze/HomeBlaze.History/HistoryService.cs
using System.Collections;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History;

public class HistoryService : BackgroundService
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ISubjectPathResolver _pathResolver;
    private readonly TimeSpan _deduplicationInterval;
    private readonly ILogger<HistoryService> _logger;

    // Shared buffer + per-sink cursors
    private readonly List<ResolvedHistoryRecord> _buffer = new();
    private readonly Dictionary<IHistorySink, int> _sinkCursors = new();
    private readonly Lock _sinksLock = new();

    // Move tracking
    private readonly Dictionary<IInterceptorSubject, string> _lastKnownPaths = new();
    private readonly List<MoveRecord> _pendingMoves = new();

    // Lifecycle subscription
    private readonly LifecycleInterceptor? _lifecycleInterceptor;

    public HistoryService(
        IInterceptorSubjectContext context,
        ISubjectPathResolver pathResolver,
        TimeSpan deduplicationInterval,
        ILogger<HistoryService> logger)
    {
        _context = context;
        _pathResolver = pathResolver;
        _deduplicationInterval = deduplicationInterval;
        _logger = logger;

        _lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectAttached += OnSubjectAttached;
            _lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
        }
    }

    internal IReadOnlyCollection<IHistorySink> Sinks
    {
        get
        {
            lock (_sinksLock)
            {
                return _sinkCursors.Keys.ToArray();
            }
        }
    }

    public IHistoryReader? GetBestReader(bool requireAggregation = false)
    {
        lock (_sinksLock)
        {
            var sinks = _sinkCursors.Keys.ToArray();
            if (sinks.Length == 0) return null;

            if (requireAggregation)
            {
                return sinks
                    .Where(s => s.SupportsNativeAggregation)
                    .OrderBy(s => s.Priority)
                    .FirstOrDefault() as IHistoryReader;
            }

            return sinks.OrderBy(s => s.Priority).First();
        }
    }

    private void OnSubjectAttached(SubjectLifecycleChange change)
    {
        if (!change.IsContextAttach) return;
        if (change.Subject is IHistorySink sink)
        {
            lock (_sinksLock)
            {
                _sinkCursors[sink] = _buffer.Count; // Start at current buffer end
            }
            _logger.LogInformation("History sink discovered: {Type}", sink.GetType().Name);
        }
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
    {
        if (!change.IsContextDetach) return;
        if (change.Subject is IHistorySink sink)
        {
            lock (_sinksLock)
            {
                _sinkCursors.Remove(sink);
            }
            _logger.LogInformation("History sink removed: {Type}", sink.GetType().Name);
        }

        // Clean up move tracking
        lock (_lastKnownPaths)
        {
            _lastKnownPaths.Remove(change.Subject);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Property filter: [State] properties only, excluding IHistorySink subjects
        bool PropertyFilter(PropertyReference propertyReference)
        {
            if (propertyReference.Subject is IHistorySink)
                return false;

            var registered = propertyReference.TryGetRegisteredProperty();
            return registered?.TryGetAttribute(KnownAttributes.State) is not null;
        }

        using var changeQueueProcessor = new ChangeQueueProcessor(
            source: this,
            _context,
            propertyFilter: PropertyFilter,
            writeHandler: HandleChangesAsync,
            _deduplicationInterval,
            _logger);

        // Start per-sink flush loop alongside CQP processing
        using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var flushTask = RunFlushLoopAsync(flushCts.Token);
        var processTask = changeQueueProcessor.ProcessAsync(stoppingToken);

        await Task.WhenAny(processTask, flushTask);
        await flushCts.CancelAsync();
    }

    private async ValueTask HandleChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Length == 0) return;

        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes.Span[i];
            var subject = change.Property.Subject;
            var path = _pathResolver.GetPath(subject, PathStyle.Canonical);
            if (path is null) continue;

            // Move tracking: detect path changes
            lock (_lastKnownPaths)
            {
                if (_lastKnownPaths.TryGetValue(subject, out var lastPath))
                {
                    if (lastPath != path)
                    {
                        _pendingMoves.Add(new MoveRecord(
                            change.ChangedTimestamp, lastPath, path));
                        _lastKnownPaths[subject] = path;
                    }
                }
                else
                {
                    _lastKnownPaths[subject] = path;
                }
            }

            var newValue = change.GetNewValue<object?>();
            var record = CreateRecord(path, change.Property, change.ChangedTimestamp, newValue);
            lock (_sinksLock)
            {
                _buffer.Add(record);
            }
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        // Tick at 1s — check each sink's individual FlushInterval
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastFlushTimes = new Dictionary<IHistorySink, DateTimeOffset>();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                KeyValuePair<IHistorySink, int>[] sinksSnapshot;
                int bufferCount;
                MoveRecord[] pendingMoves;

                lock (_sinksLock)
                {
                    sinksSnapshot = _sinkCursors.ToArray();
                    bufferCount = _buffer.Count;
                }

                lock (_lastKnownPaths)
                {
                    pendingMoves = _pendingMoves.ToArray();
                    _pendingMoves.Clear();
                }

                var now = DateTimeOffset.UtcNow;

                foreach (var (sink, cursor) in sinksSnapshot)
                {
                    var sinkBase = sink as HistorySinkBase;
                    var flushInterval = sinkBase?.FlushInterval ?? TimeSpan.FromSeconds(10);

                    if (!lastFlushTimes.TryGetValue(sink, out var lastFlush))
                        lastFlush = DateTimeOffset.MinValue;

                    if (now - lastFlush < flushInterval)
                        continue;

                    lastFlushTimes[sink] = now;

                    // Flush records
                    if (cursor < bufferCount)
                    {
                        ResolvedHistoryRecord[] batch;
                        lock (_sinksLock)
                        {
                            var count = _buffer.Count - cursor;
                            if (count <= 0) continue;
                            batch = new ResolvedHistoryRecord[count];
                            _buffer.CopyTo(cursor, batch, 0, count);
                            _sinkCursors[sink] = _buffer.Count;
                        }

                        try
                        {
                            await sink.WriteBatchAsync(batch);
                        }
                        catch (Exception ex)
                        {
                            if (sinkBase is not null)
                                sinkBase.Status = $"Error: {ex.Message}";
                            _logger.LogWarning(ex, "History sink {Type} flush failed", sink.GetType().Name);
                        }
                    }

                    // Flush moves
                    if (pendingMoves.Length > 0)
                    {
                        try
                        {
                            await sink.WriteMovesAsync(pendingMoves);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "History sink {Type} move write failed", sink.GetType().Name);
                        }
                    }
                }

                // Trim buffer up to minimum cursor
                TrimBuffer();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void TrimBuffer()
    {
        lock (_sinksLock)
        {
            if (_sinkCursors.Count == 0)
            {
                _buffer.Clear();
                return;
            }

            var minCursor = _sinkCursors.Values.Min();
            if (minCursor > 0)
            {
                _buffer.RemoveRange(0, minCursor);
                // Adjust all cursors
                var keys = _sinkCursors.Keys.ToArray();
                foreach (var key in keys)
                {
                    _sinkCursors[key] -= minCursor;
                }
            }
        }
    }

    private ResolvedHistoryRecord CreateRecord(
        string subjectPath, PropertyReference property,
        DateTimeOffset timestamp, object? value)
    {
        return value switch
        {
            // Structural properties: record as lightweight path references
            IInterceptorSubject subject => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.String, 0,
                JsonSerializer.SerializeToUtf8Bytes(
                    _pathResolver.GetPath(subject, PathStyle.Canonical))),

            IDictionary dictionary => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Complex, 0,
                JsonSerializer.SerializeToUtf8Bytes(
                    dictionary.Keys.Cast<object>().Select(k => k.ToString()).ToArray())),

            ICollection collection when property.TryGetRegisteredProperty() is { CanContainSubjects: true } =>
                new ResolvedHistoryRecord(
                    subjectPath, property.Name, timestamp.Ticks,
                    HistoryValueType.Complex, 0,
                    JsonSerializer.SerializeToUtf8Bytes(
                        collection.Cast<object>()
                            .OfType<IInterceptorSubject>()
                            .Select(s => _pathResolver.GetPath(s, PathStyle.Canonical))
                            .ToArray())),

            // Primitive types
            null => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Null, 0, default),

            double d => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Double, d, default),

            float f => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Double, f, default),

            int n => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Double, n, default),

            long n => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Double, n, default),

            decimal d => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Double, (double)d, default),

            bool b => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Boolean, b ? 1.0 : 0.0, default),

            string s => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.String, 0,
                JsonSerializer.SerializeToUtf8Bytes(s)),

            _ => new ResolvedHistoryRecord(
                subjectPath, property.Name, timestamp.Ticks,
                HistoryValueType.Complex, 0,
                JsonSerializer.SerializeToUtf8Bytes(value))
        };
    }

    public override void Dispose()
    {
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectAttached -= OnSubjectAttached;
            _lifecycleInterceptor.SubjectDetaching -= OnSubjectDetaching;
        }
        base.Dispose();
    }
}
```

**Step 5:** Create DI extensions:

```csharp
// src/HomeBlaze/HomeBlaze.History/HistoryServiceExtensions.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.History;

public static class HistoryServiceExtensions
{
    public static IServiceCollection AddHistoryService(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetService<IConfiguration>();
            var interval = TimeSpan.FromSeconds(1); // default
            var configValue = configuration?["History:DeduplicationInterval"];
            if (configValue is not null)
                interval = TimeSpan.Parse(configValue);
            return interval;
        });
        services.AddSingleton<HistoryService>();
        services.AddHostedService(sp => sp.GetRequiredService<HistoryService>());
        return services;
    }
}
```

**Step 6:** Create HistoryRecordExtensions:

```csharp
// src/HomeBlaze/HomeBlaze.History/HistoryRecordExtensions.cs
using System.Text.Json;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.History;

public static class HistoryRecordExtensions
{
    public static T? DeserializeValue<T>(this HistoryRecord record)
        => record.Value.Deserialize<T>();

    public static object? DeserializeValue(this HistoryRecord record, Type propertyType)
        => record.Value.Deserialize(propertyType);

    public static object? DeserializeValue(this HistoryRecord record, IInterceptorSubject subject)
    {
        var property = subject.TryGetRegisteredSubject()?.TryGetProperty(record.PropertyName);
        return property is null ? null : record.Value.Deserialize(property.Type);
    }
}
```

**Step 7:** Add to solution and build:
```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj --solution-folder /HomeBlaze
dotnet build src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj
```
Expected: Build succeeded

**Step 8:** Commit.

---

### Task 5: Create HomeBlaze.History.Tests with InMemoryHistorySink Tests

**Why:** The InMemoryHistorySink enables thorough testing of all history logic without file system dependencies. This task writes comprehensive tests covering: sink discovery, change recording, property filtering, move tracking (write + read), aggregation, snapshot reconstruction, and edge cases.

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/HomeBlaze.History.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/TestHelpers/TestPathResolver.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/TestHelpers/TestSubject.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/InMemoryHistorySinkTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/MoveTrackingTests.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1:** Create test project and helpers. `TestPathResolver` implements `ISubjectPathResolver` with a dictionary for controlled path mapping.

**Step 2:** Write InMemoryHistorySink tests covering:
- `WhenNumericRecordWritten_ThenQueryReturnsValue`
- `WhenBooleanRecordWritten_ThenRoundTripsAsBoolean`
- `WhenStringRecordWritten_ThenRoundTripsCorrectly`
- `WhenNullRecordWritten_ThenValueKindIsNull`
- `WhenQueryingOutsideRange_ThenReturnsEmpty`
- `WhenQueryingMultipleProperties_ThenReturnsBoth`
- `WhenAggregatingWithBuckets_ThenReturnsBucketedResults`
- `WhenAggregatingAcrossBuckets_ThenMergesCorrectly`
- `WhenSnapshotWrittenAndRead_ThenRoundTrips`
- `WhenSnapshotRequestedBetweenSnapshots_ThenReplaysChanges`
- `WhenSnapshotSeriesRequested_ThenReturnsAtIntervals`
- `WhenMaxAgeSet_ThenOldRecordsEvicted`
- `WhenAverageRequestedOverIrregularSamples_ThenReturnsTimeWeightedMean`
- `WhenSampleMeanRequested_ThenReturnsCountWeightedMean`
- `WhenQueryRangeExtendsIntoMinAge_ThenYoungTailServedFromBuffer`
- `WhenPartialSnapshotRequested_ThenFiltersByPath`

**Step 3:** Write move tracking tests covering:
- `WhenSubjectMoved_ThenQueryReturnsHistoryFromOldPath`
- `WhenMoveChain_ThenFollowsFullChain`
- `WhenMoveWithTimeRange_ThenScopesEachPathCorrectly`
- `WhenCyclicMoves_ThenDoesNotInfiniteLoop`
- `WhenSnapshotWithMoves_ThenResolvesAliases`
- `WhenAggregationWithMoves_ThenMergesAcrossRename`
- `WhenNoMovesExist_ThenQueryReturnsExactPathOnly`

**Step 4:** Write HistoryService tests covering:
- `WhenSinkAttachedToGraph_ThenServiceDiscoversSink`
- `WhenSinkDetachedFromGraph_ThenServiceRemovesSink`
- `WhenStatePropertyChanges_ThenSinkReceivesBatch`
- `WhenConfigurationPropertyChanges_ThenSinkDoesNotReceive`
- `WhenHistorySinkPropertyChanges_ThenNotRecorded` (self-recording prevention)
- `WhenStructuralPropertyChanges_ThenRecordedAsLightweightPaths`
- `WhenSinkThrows_ThenServiceContinuesRunning`
- `WhenMultipleSinks_ThenAllReceiveBatches`
- `WhenSubjectPathChanges_ThenMoveRecordEmitted`

**Step 5:** Add to solution, run tests:
```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Tests/HomeBlaze.History.Tests.csproj --solution-folder /HomeBlaze/Tests
dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore
```
Expected: FAIL (HistoryService.ExecuteAsync not fully wired yet for integration tests, but InMemoryHistorySink tests PASS)

**Step 6:** Commit.

---

### Task 6: Make HistoryService Tests Pass

**Why:** Fix any remaining issues in HistoryService to make all tests green.

Adjust test setup and HistoryService implementation as needed based on test failures. The main work is wiring up the test context with lifecycle tracking, registry, and a test root subject that can hold sink subjects.

**Step 1:** Run failing tests, diagnose, fix.

**Step 2:** Run all tests:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore
```
Expected: ALL PASS

**Step 3:** Commit.

---

### Task 7: Create HomeBlaze.History.Sqlite Project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqlitePartitionManager.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1:** Create .csproj with `Microsoft.Data.Sqlite` dependency.

**Step 2:** Create `SqlitePartitionManager` — handles partition-to-filename mapping, DB initialization with schema (no redundant index), and partition key enumeration.

Schema:
```sql
CREATE TABLE history (
    timestamp INTEGER NOT NULL,
    subject_path TEXT NOT NULL,
    property_name TEXT NOT NULL,
    value_type INTEGER NOT NULL,
    numeric_value REAL,
    raw_value TEXT,
    PRIMARY KEY (subject_path, property_name, timestamp)
) WITHOUT ROWID;

CREATE TABLE snapshots (
    timestamp INTEGER NOT NULL PRIMARY KEY,
    base_path TEXT NOT NULL,
    data BLOB NOT NULL
);
```

Separate moves DB (`history-moves.db`):
```sql
CREATE TABLE moves (
    timestamp INTEGER NOT NULL,
    from_path TEXT NOT NULL,
    to_path TEXT NOT NULL,
    PRIMARY KEY (timestamp, from_path)
) WITHOUT ROWID;

CREATE INDEX ix_moves_to ON moves (to_path, timestamp);
```

**Step 3:** Create `SqliteHistorySink` implementing all methods:
- `WriteBatchAsync`: group by partition, batch INSERT per transaction.
- `WriteMovesAsync`: write to moves DB.
- `QueryAsync`: query across partitions, follow moves via path chain resolution.
- `QueryAggregatedAsync`: see Step 3b below.
- `WriteSnapshotAsync`: gzip + store in partition's snapshots table.
- `GetLatestSnapshotTimeAsync`: `SELECT MAX(timestamp) FROM snapshots` across recent partitions.
- `GetSnapshotAsync`: scan partitions backwards for nearest snapshot, replay changes.
- `GetSnapshotsAsync`: iterate calling GetSnapshotAsync at intervals.
- `MinAge`: inherited default of `FlushInterval` (no override needed).
- `MaxAge`: inherited; the partition sweep deletes whole partition files when their `[partition_start, partition_end]` falls entirely before `now - MaxAge` (no per-row DELETE).

**Step 3b:** Implement time-weighted aggregation per partition. Per (property, partition):

```csharp
// Pseudocode for per-partition aggregation; see the design doc for the full CTE SQL.
async Task<List<AggregatedRecord>> AggregatePartitionAsync(
    SqliteConnection conn, string path, string propertyName,
    long fromTicks, long toTicks, long rangeEndTicks, long bucketTicks,
    AggregationType aggregation)
{
    // 1. SQL with carry-sample CTE (see design doc "Native Aggregation" SQL).
    //    Run the CTE once; SQL emits one row per bucket with:
    //    bucket_start, weighted_sum, total_duration, sample_mean, min, max, sum, count.
    //
    // 2. For First/Last (when requested), issue:
    //      SELECT numeric_value FROM history
    //      WHERE subject_path=@path AND property_name=@property
    //        AND timestamp >= @bucketStart AND timestamp < @bucketEnd
    //      ORDER BY timestamp [ASC|DESC] LIMIT 1
    //    per bucket. Gate on AggregationType so we don't pay for it otherwise.
    //
    // 3. Build AggregatedRecord with ALL nullable fields, including:
    //      WeightedSum = weighted_sum,
    //      TotalDurationTicks = total_duration
    //    so the cross-partition / cross-leg merge has its inputs.
}
```

**Step 3c:** Cross-partition merge. After running `AggregatePartitionAsync` for each partition that overlaps the query range, merge per `(propertyName, BucketStart)`:

```csharp
static AggregatedRecord MergeBuckets(AggregatedRecord a, AggregatedRecord b)
{
    // Both inputs are for the same bucket; merge using the cross-partition merge table
    // from the design doc.
    var weightedSum = (a.WeightedSum ?? 0) + (b.WeightedSum ?? 0);
    var totalDuration = (a.TotalDurationTicks ?? 0) + (b.TotalDurationTicks ?? 0);
    var sum = (a.Sum ?? 0) + (b.Sum ?? 0);
    var count = a.Count + b.Count;
    return a with
    {
        Average = totalDuration > 0 ? weightedSum / totalDuration : null,
        SampleMean = count > 0 ? sum / count : null,
        Minimum = NullableMin(a.Minimum, b.Minimum),
        Maximum = NullableMax(a.Maximum, b.Maximum),
        Sum = count > 0 ? sum : null,
        Count = count,
        First = a.BucketStart <= b.BucketStart ? a.First : b.First,  // both same bucket; First is by row timestamp inside it
        Last  = a.BucketStart >= b.BucketStart ? a.Last  : b.Last,
        WeightedSum = weightedSum,
        TotalDurationTicks = totalDuration,
    };
}
```

The same `MergeBuckets` helper is used by `HistoryService` when stitching the buffer leg onto a cold-sink leg (see Task 9b). Move it to `HomeBlaze.History.Abstractions` so both projects share one implementation.

Key implementation notes:
- `ReadJsonValue` uses `JsonSerializer.Deserialize<JsonElement>()` (not `JsonDocument.Parse()`) to avoid memory leaks.
- Boolean values read back via `JsonSerializer.SerializeToElement(reader.GetDouble() != 0.0)`.
- Null values produce `JsonSerializer.SerializeToElement<object?>(null)`.
- Snapshot search scans partitions backwards, stops at first found.
- For samples that span more than one bucket (a value held longer than `bucketSize`), expand via a recursive CTE generating bucket-boundary rows so each bucket the sample touches gets its proportional contribution. Tests must cover the long-hold case.

**Step 4:** Add to solution and build:
```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj --solution-folder /HomeBlaze
dotnet build src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj
```

**Step 5:** Commit.

---

### Task 8: Create SQLite Tests

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/HomeBlaze.History.Sqlite.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkWriteTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkReadTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkAggregationTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkSnapshotTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkMoveTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqlitePartitionManagerTests.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Test coverage:**

Write tests:
- `WhenNumericRecordWritten_ThenStoredWithNumericValue`
- `WhenStringRecordWritten_ThenStoredInRawValue`
- `WhenMultipleRecordsWritten_ThenAllStored`
- `WhenRecordsSpanMultipleDays_ThenStoredInSeparatePartitions`
- `WhenBooleanWrittenAndRead_ThenRoundTripsAsBoolean`

Read tests:
- `WhenQueryingSingleProperty_ThenReturnsRecordsInRange`
- `WhenQueryingMultipleProperties_ThenReturnsBoth`
- `WhenQueryingOutsideRange_ThenReturnsEmpty`
- `WhenQueryingAcrossPartitions_ThenReturnsAll`

Aggregation tests:
- `WhenAggregatingWithBuckets_ThenReturnsSqlGroupedResults`
- `WhenAggregatingAcrossPartitions_ThenMergesBuckets`
- `WhenAggregatingAverage_ThenReturnsTimeWeightedMean`
- `WhenAggregatingSampleMean_ThenReturnsCountWeightedMean`
- `WhenBucketHasOneSample_ThenAverageEqualsSampleValue`
- `WhenBucketInheritsValueFromCarrySample_ThenAverageEqualsCarriedValue`
- `WhenQueryCrossesPartitions_ThenWeightedSumsMergeCorrectly`
- `WhenAggregatingMinimum_ThenMinOfMins`
- `WhenAggregatingFirst_ThenEarliestByTimestamp`

Snapshot tests:
- `WhenSnapshotWrittenAndRead_ThenRoundTrips`
- `WhenSnapshotRequestedBetweenSnapshots_ThenReplaysChanges`
- `WhenSnapshotSearchScansBackward_ThenStopsAtFirstFound`

Move tests:
- `WhenSubjectMoved_ThenQueryReturnsHistoryFromOldPath`
- `WhenMoveChainInSqlite_ThenFollowsFullChain`

Partition tests:
- `WhenDailyPartition_ThenCorrectKey`
- `WhenWeeklyPartition_ThenCorrectKey`
- `WhenMonthlyPartition_ThenCorrectKey`
- `WhenRetentionExpires_ThenOldPartitionsDeleted`

```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/HomeBlaze.History.Sqlite.Tests.csproj --solution-folder /HomeBlaze/Tests
dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore
```
Expected: ALL PASS

Commit.

---

### Task 8b: Implement MinAge-aware Read Routing and Queryable Shared Buffer

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/BufferHistoryReader.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Abstractions/AggregatedRecord.cs` (already done, no-op)
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/AggregatedRecordMerger.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceRoutingTests.cs`

**Why:** The brainstorm specifies that the shared buffer covers `[now - bufferLength, now]` and that the service splits queries at `MinAge_min` so the young tail is always served. Without this task, `HistoryService` just delegates queries to one sink and gives up on the young tail when no sink covers it natively.

**Step 1:** Create `BufferHistoryReader : IHistoryReader` adapter. Constructor takes a `Func<ReadOnlySpan<ResolvedHistoryRecord>>` returning a snapshot of the shared buffer (HistoryService passes a delegate that locks and copies). Properties:

```csharp
public int Priority => -1;                     // highest priority for the recent tail
public DateTimeOffset? OldestRecord { get; }   // computed from buffer head timestamp
public TimeSpan MinAge => TimeSpan.Zero;       // by construction
public TimeSpan MaxAge { get; }                // = bufferLength
public bool SupportsNativeAggregation => true; // in-memory aggregation reuses Step 3
```

`QueryAsync` filters the buffer snapshot by path / property / time-range and maps `ResolvedHistoryRecord` to `HistoryRecord` (deserialize value via the same helper used in `InMemoryHistorySink`). `QueryAggregatedAsync` reuses the time-weighted aggregation code from `InMemoryHistorySink` (refactor it into a shared internal helper in `HomeBlaze.History` so both classes call it). `WriteBatchAsync` and friends throw `NotSupportedException` (the buffer is read-only from a sink's perspective).

**Step 2:** Add `AggregatedRecordMerger` to Abstractions with three pure helpers:

```csharp
public static class AggregatedRecordMerger
{
    // Combine two AggregatedRecord lists by (BucketStart) using the cross-partition merge.
    public static IReadOnlyList<AggregatedRecord> Merge(
        IReadOnlyList<AggregatedRecord> a, IReadOnlyList<AggregatedRecord> b);

    // Used by per-partition merge (SqliteHistorySink) and per-leg merge (HistoryService).
    public static AggregatedRecord MergeBuckets(AggregatedRecord x, AggregatedRecord y);

    private static double? NullableMin(double? x, double? y);
    private static double? NullableMax(double? x, double? y);
}
```

**Step 3:** In `HistoryService`, replace the existing `GetBestReader` shortcut with explicit routing:

```csharp
// Pseudocode
public async Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
{
    var bufferReader = new BufferHistoryReader(/* delegate */);
    var minAgeCutoff = DateTimeOffset.UtcNow - MinMinAgeOfAttachedSinks();
    var youngFrom = Max(query.From, minAgeCutoff);
    var youngTo = query.To;
    var coldFrom = query.From;
    var coldTo = Min(query.To, minAgeCutoff);

    var legs = new List<IReadOnlyList<HistoryRecord>>();
    if (youngFrom < youngTo)
        legs.Add(await bufferReader.QueryAsync(query with { From = youngFrom, To = youngTo }));
    if (coldFrom < coldTo)
    {
        var coldReader = PickReader(coldFrom, coldTo, query.Aggregation is not null);
        if (coldReader is not null)
            legs.Add(await coldReader.QueryAsync(query with { From = coldFrom, To = coldTo }));
    }
    return legs.SelectMany(x => x).OrderBy(r => r.Timestamp).ToList();
}

public async Task<IReadOnlyList<AggregatedRecord>> QueryAggregatedAsync(HistoryQuery query)
{
    // Same split, but each leg returns AggregatedRecord with WeightedSum/TotalDurationTicks
    // populated. Use AggregatedRecordMerger.Merge to combine legs by BucketStart.
}
```

`PickReader(from, to, requireNativeAgg)` walks attached sinks in ascending `Priority` order, returning the first whose coverage window `[now - MaxAge, now - MinAge]` contains `[from, to]` and whose `SupportsNativeAggregation` is satisfied (for aggregation queries).

**Step 4:** Snapshot routing. `HistoryService.GetSnapshotAsync(path, time)` picks the lowest-priority sink whose `OldestRecord <= time` (or falls back to the buffer if the time is within `MaxAge` of `bufferReader`). For times within `MinAge` of every persistent sink, only the buffer + base-snapshot replay can answer; document the trade-off in code comments.

**Step 5:** Tests in `HistoryServiceRoutingTests`:
- `WhenRangeFullyInsidePersistentSink_ThenBufferLegIsEmpty`
- `WhenRangeFullyInsideBuffer_ThenColdLegIsEmpty`
- `WhenRangeCrossesMinAgeBoundary_ThenStitchesBufferAndSink`
- `WhenAggregationCrossesBoundary_ThenMergesWeightedSums`
- `WhenSampleSpansMinAgeBoundary_ThenWeightedAverageRemainsExact` (regression for the joint-leg edge case)
- `WhenNoPersistentSinkAttached_ThenBufferAnswersWithinBufferLength`
- `WhenTwoSinksCoverSameRange_ThenLowestPriorityWins`

**Step 6:** Build and run unit tests:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore
```
Expected: ALL PASS

**Step 7:** Commit.

---

### Task 9: Implement Snapshot Scheduling in HistoryService

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`

**Step 1:** Add snapshot creation method that walks all registered subjects via registry, reads current `[State]` property values, builds `HistorySnapshot`, calls `sink.WriteSnapshotAsync()`.

**Step 2:** Add snapshot scheduling to the flush loop. On sink attach, call `sink.GetLatestSnapshotTimeAsync()` and cache the result. On each tick: check `now - cachedSnapshotTime >= sink.SnapshotInterval`. After writing a snapshot, update the cached time directly (no re-query needed).

**Step 3:** Add retention housekeeping — delegate to sinks (SQLite deletes old partition files, in-memory evicts by time).

**Step 4:** Write and run test:
- `WhenSnapshotIntervalElapses_ThenSnapshotWrittenToSink`

**Step 5:** Commit.

---

### Task 10: Add MCP Tools to HomeBlaze.AI

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.AI/Mcp/HistoryMcpToolProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/McpBuilderExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/HomeBlaze.AI.csproj`

**Step 1:** Add `HomeBlaze.History.Abstractions` reference to `HomeBlaze.AI.csproj`.

**Step 2:** Create `HistoryMcpToolProvider` implementing `IMcpToolProvider` with three tools:
- `get_property_history` — raw or aggregated queries, returns grouped by property name
- `get_snapshot` — reconstruct graph state at time T
- `get_snapshots` — snapshot series over time range

Use `HistoryService.GetBestReader()` to find the appropriate sink for queries. Aggregated queries use `QueryAggregatedAsync` returning full `AggregatedRecord` data.

**Step 3:** Register in `McpBuilderExtensions.WithHomeBlazeMcpTools()`.

**Step 4:** Build:
```bash
dotnet build src/HomeBlaze/HomeBlaze.AI/HomeBlaze.AI.csproj
```

**Step 5:** Commit.

---

### Task 11: Wire Up in Program.cs

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs`
- Modify: `src/HomeBlaze/HomeBlaze/HomeBlaze.csproj`

**Step 1:** Add project references to `HomeBlaze.History` and `HomeBlaze.History.Sqlite`.

**Step 2:** Add `builder.Services.AddHistoryService()` after `AddHomeBlazeHost()`.

**Step 3:** Register `SqliteHistorySink` type in `SubjectTypeRegistry`:
```csharp
typeProvider.AddAssembly(typeof(SqliteHistorySink).Assembly);
```

**Step 4:** Create sample config file `src/HomeBlaze/HomeBlaze/Data/history-sqlite.json`:
```json
{
  "$type": "HomeBlaze.History.Sqlite.SqliteHistorySink",
  "DatabasePath": "./Data/history",
  "FlushInterval": "00:00:10",
  "PartitionInterval": "Weekly",
  "SnapshotInterval": "1.00:00:00",
  "MaxAge": "365.00:00:00",
  "Priority": 100
}
```

**Step 5:** Build and run all tests:
```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```
Expected: ALL PASS

**Step 6:** Commit.

---

### Task 12: Final Verification

**Step 1:** Run full solution build:
```bash
dotnet build src/Namotion.Interceptor.slnx
```
Expected: Build succeeded with zero warnings

**Step 2:** Run all unit tests:
```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```
Expected: All PASS

**Step 3:** Verify no leftover TODOs. Search for `throw new NotImplementedException()` in history projects. All should be resolved.

**Step 4:** Commit.

---

### Task 13: Create HomeBlaze.History.TimescaleDb Project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb/HomeBlaze.History.TimescaleDb.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb/TimescaleDbHistorySink.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb/TimescaleDbSchema.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1:** Create .csproj. References `HomeBlaze.History` and adds `Npgsql` (9.x).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History\HomeBlaze.History.csproj" />
    <PackageReference Include="Npgsql" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2:** Create `TimescaleDbSchema` with idempotent bootstrap. Runs once on first connection per sink instance, guarded by a flag.

```sql
CREATE TABLE IF NOT EXISTS property_history (
    timestamp     TIMESTAMPTZ NOT NULL,
    subject_path  TEXT        NOT NULL,
    property_name TEXT        NOT NULL,
    value_type    SMALLINT    NOT NULL,
    numeric_value DOUBLE PRECISION,
    raw_value     TEXT,
    PRIMARY KEY (subject_path, property_name, timestamp)
);
SELECT create_hypertable('property_history', 'timestamp',
    chunk_time_interval => INTERVAL '1 day',
    if_not_exists       => TRUE);

CREATE TABLE IF NOT EXISTS property_moves (
    timestamp TIMESTAMPTZ NOT NULL,
    from_path TEXT        NOT NULL,
    to_path   TEXT        NOT NULL,
    PRIMARY KEY (timestamp, from_path)
);
CREATE INDEX IF NOT EXISTS ix_moves_to ON property_moves (to_path, timestamp);

CREATE TABLE IF NOT EXISTS property_snapshots (
    timestamp  TIMESTAMPTZ NOT NULL PRIMARY KEY,
    base_path  TEXT        NOT NULL,
    data       BYTEA       NOT NULL
);
```

**Step 3:** Implement `TimescaleDbHistorySink : HistorySinkBase`. Skeleton:

```csharp
[InterceptorSubject]
public partial class TimescaleDbHistorySink : HistorySinkBase
{
    [Configuration]
    public partial string ConnectionString { get; set; } = "";

    public TimescaleDbHistorySink()
    {
        FlushInterval = TimeSpan.FromSeconds(10);
        MaxAge = TimeSpan.FromDays(365);
    }

    public override TimeSpan MinAge => FlushInterval;
    public override bool SupportsNativeAggregation => true;
    // OldestRecord: SELECT MIN(timestamp) FROM property_history (cached, refreshed on retention sweep)
}
```

**Step 4:** Write path. `WriteBatchAsync` uses a TEMP table + `INSERT ... ON CONFLICT DO NOTHING` from it. Direct `COPY FROM STDIN BINARY` does NOT support `ON CONFLICT`, so the temp-table indirection is required.

```csharp
public override async Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records)
{
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using (var cmd = new NpgsqlCommand("""
        CREATE TEMP TABLE IF NOT EXISTS property_history_staging
            (LIKE property_history INCLUDING DEFAULTS)
        ON COMMIT DROP;
        """, conn, tx))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var importer = await conn.BeginBinaryImportAsync("""
        COPY property_history_staging
            (timestamp, subject_path, property_name, value_type, numeric_value, raw_value)
        FROM STDIN (FORMAT BINARY)
        """))
    {
        for (var i = 0; i < records.Length; i++)
        {
            var r = records.Span[i];
            await importer.StartRowAsync();
            await importer.WriteAsync(new DateTime(r.TimestampTicks, DateTimeKind.Utc), NpgsqlDbType.TimestampTz);
            await importer.WriteAsync(r.SubjectPath, NpgsqlDbType.Text);
            await importer.WriteAsync(r.PropertyName, NpgsqlDbType.Text);
            await importer.WriteAsync((short)r.ValueType, NpgsqlDbType.Smallint);
            if (r.ValueType is HistoryValueType.Double or HistoryValueType.Boolean)
                await importer.WriteAsync(r.NumericValue, NpgsqlDbType.Double);
            else
                await importer.WriteNullAsync();
            if (r.ValueType is HistoryValueType.String or HistoryValueType.Complex)
                await importer.WriteAsync(System.Text.Encoding.UTF8.GetString(r.RawValue.Span), NpgsqlDbType.Text);
            else
                await importer.WriteNullAsync();
        }
        await importer.CompleteAsync();
    }

    await using (var cmd = new NpgsqlCommand("""
        INSERT INTO property_history
        SELECT * FROM property_history_staging
        ON CONFLICT DO NOTHING;
        """, conn, tx))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    RecordsWritten += records.Length;
    Status = "Connected";
}
```

`WriteMovesAsync` and `WriteSnapshotAsync` are plain parameterized INSERTs in their own short transactions.

**Step 5:** Read path. `QueryAsync` mirrors SQLite (path-chain resolution + a single `SELECT ... WHERE subject_path=ANY(@paths) AND property_name=ANY(@properties) AND timestamp BETWEEN @from AND @to ORDER BY timestamp`). Hypertable chunk pruning happens automatically.

`QueryAggregatedAsync` reuses the same carry-sample CTE pattern as the SQLite sink, swapping integer-tick math for `time_bucket(@bucket, timestamp)`:

```sql
WITH carry AS (
    SELECT timestamp, numeric_value
    FROM property_history
    WHERE subject_path = @path AND property_name = @property
      AND value_type IN (1, 2)
      AND timestamp < @from
    ORDER BY timestamp DESC
    LIMIT 1
),
in_range AS (
    SELECT timestamp, numeric_value
    FROM property_history
    WHERE subject_path = @path AND property_name = @property
      AND value_type IN (1, 2)
      AND timestamp BETWEEN @from AND @to
),
samples AS (
    SELECT * FROM carry UNION ALL SELECT * FROM in_range
),
samples_with_next AS (
    SELECT timestamp, numeric_value,
           LEAD(timestamp, 1, @rangeEnd) OVER (ORDER BY timestamp) AS next_timestamp
    FROM samples
),
clipped AS (
    SELECT
        time_bucket(@bucket, timestamp) AS bucket_start,
        numeric_value,
        GREATEST(timestamp, time_bucket(@bucket, timestamp))                     AS effective_start,
        LEAST(next_timestamp, time_bucket(@bucket, timestamp) + @bucket)          AS effective_end
    FROM samples_with_next
)
SELECT
    bucket_start,
    SUM(numeric_value * EXTRACT(EPOCH FROM (effective_end - effective_start))) /
        NULLIF(SUM(EXTRACT(EPOCH FROM (effective_end - effective_start))), 0)  AS average,
    AVG(numeric_value)                                                          AS sample_mean,
    MIN(numeric_value)                                                          AS minimum,
    MAX(numeric_value)                                                          AS maximum,
    SUM(numeric_value)                                                          AS sum,
    COUNT(*)                                                                    AS count,
    SUM(numeric_value * EXTRACT(EPOCH FROM (effective_end - effective_start)))  AS weighted_sum,
    SUM(EXTRACT(EPOCH FROM (effective_end - effective_start)) * 1e7)            AS total_duration_ticks
FROM clipped
GROUP BY bucket_start
ORDER BY bucket_start;
```

Populate `AggregatedRecord.WeightedSum` and `TotalDurationTicks` from the last two columns so `HistoryService` cross-leg merging works (see Task 8b).

**Step 6:** Retention. `HostedService` timer (every `RetentionSweepInterval`, default 1 hour) issues:

```sql
SELECT drop_chunks('property_history', older_than => NOW() - @maxAge);
```

Chunks fully older than `now - MaxAge` drop atomically. Moves and snapshots tables are not swept in v1.

**Step 7:** Add to solution and build:

```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.TimescaleDb/HomeBlaze.History.TimescaleDb.csproj --solution-folder /HomeBlaze
dotnet build src/HomeBlaze/HomeBlaze.History.TimescaleDb/HomeBlaze.History.TimescaleDb.csproj
```

**Step 8:** Commit.

---

### Task 14: Create TimescaleDB Tests

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/HomeBlaze.History.TimescaleDb.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/TimescaleDbFixture.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/TimescaleDbHistorySinkTests.cs`

**Step 1:** Project references `HomeBlaze.History.TimescaleDb`, `xunit`, `Testcontainers.PostgreSql` (3.x).

**Step 2:** `TimescaleDbFixture` is an `IAsyncLifetime` that starts `timescale/timescaledb-ha:pg17` (or pg16-all if pg17 image is not pinned in your CI), exposes a connection string, and disposes the container at end of class.

**Step 3:** Tests are tagged `[Trait("Category", "Integration")]` so they don't run in the default unit suite. Each test:
- Boots the fixture, creates a sink instance, calls `BootstrapAsync` (schema migration).
- Writes a known batch, queries it back, asserts.
- Test list:
  - `WhenBatchWritten_ThenQueryableByPath`
  - `WhenSamePrimaryKeyWrittenTwice_ThenSecondIsDroppedSilently` (ON CONFLICT DO NOTHING)
  - `WhenAggregatingAverage_ThenReturnsTimeWeightedMean`
  - `WhenAggregatingSampleMean_ThenReturnsCountWeightedMean`
  - `WhenBucketInheritsValueFromCarrySample_ThenAverageEqualsCarriedValue`
  - `WhenSampleSpansMultipleBuckets_ThenEachBucketReceivesProportionalContribution`
  - `WhenDropChunksCalled_ThenChunksOlderThanMaxAgeAreRemoved`
  - `WhenMovesWritten_ThenPathChainResolves`

**Step 4:** Add CI gate: integration job runs on PR and main only when the `homeblaze-history-timescaledb` label is present, OR on push to `feature/homeblaze-history` and `master`. Avoids paying TimescaleDB image pull on every PR.

**Step 5:** Sample config `src/HomeBlaze/HomeBlaze/Data/history-timescaledb.json`:

```json
{
  "$type": "HomeBlaze.History.TimescaleDb.TimescaleDbHistorySink",
  "ConnectionString": "Host=localhost;Database=homeblaze;Username=homeblaze;Password=...",
  "FlushInterval": "00:00:10",
  "SnapshotInterval": "1.00:00:00",
  "MaxAge": "365.00:00:00",
  "Priority": 100
}
```

**Step 6:** Build and run integration tests locally:

```bash
dotnet test src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/
```
Expected: ALL PASS

**Step 7:** Commit.

**Explicit non-goals for v1:**

- TimescaleDB continuous aggregates (require pinning bucket sizes).
- Native compression (`add_compression_policy`).
- Plain PostgreSQL / QuestDB fallback (no hypertables, would re-implement retention manually).
