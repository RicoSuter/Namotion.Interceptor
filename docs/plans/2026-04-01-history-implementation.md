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
    Average, Minimum, Maximum, Sum, Count, First, Last
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
    AggregationType? Aggregation = null,
    bool FollowMoves = false);
```

```csharp
// AggregatedRecord.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record AggregatedRecord(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    double? Average, double? Minimum, double? Maximum,
    double? Sum, long Count,
    JsonElement? First, JsonElement? Last);
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
    bool SupportsNativeAggregation { get; }

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

**Step 2:** Create HistorySinkBase. Note: `RecordsWritten`, `Status`, `LastSnapshotTime` are plain properties (not `[State]`) to avoid self-recording feedback loop:

```csharp
// src/HomeBlaze/HomeBlaze.History/HistorySinkBase.cs
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History;

[InterceptorSubject]
public abstract partial class HistorySinkBase : IHistorySink
{
    [Configuration]
    public partial TimeSpan FlushInterval { get; set; }

    [Configuration]
    public partial TimeSpan SnapshotInterval { get; set; }

    [Configuration]
    public partial int RetentionDays { get; set; }

    [Configuration]
    public partial int Priority { get; set; }

    // Plain properties — NOT [State] to avoid self-recording feedback loop
    public DateTimeOffset? LastSnapshotTime { get; set; }
    public long RecordsWritten { get; set; }
    public string? Status { get; set; }

    int IHistoryReader.Priority => Priority;

    public abstract DateTimeOffset? OldestRecord { get; }
    public abstract bool SupportsNativeAggregation { get; }

    public abstract Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    public abstract Task WriteSnapshotAsync(HistorySnapshot snapshot);
    public abstract Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
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

    [Configuration]
    public partial TimeSpan MaxRetention { get; set; }

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
            LastSnapshotTime = snapshot.Timestamp;
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

    // --- Read Side ---

    public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
    {
        lock (_lock)
        {
            var paths = query.FollowMoves
                ? ResolvePathChain(query.SubjectPath, query.From, query.To)
                : [(query.SubjectPath, query.From, query.To)];

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

            var paths = query.FollowMoves
                ? ResolvePathChain(query.SubjectPath, query.From, query.To)
                : [(query.SubjectPath, query.From, query.To)];

            var bucketTicks = query.BucketSize.Value.Ticks;
            var allRows = new List<(long timestamp, string propertyName, double value)>();

            foreach (var (path, from, to) in paths)
            {
                allRows.AddRange(_records
                    .Where(r => r.SubjectPath == path
                        && query.PropertyNames.Contains(r.PropertyName)
                        && r.TimestampTicks >= from.Ticks
                        && r.TimestampTicks <= to.Ticks
                        && r.ValueType is HistoryValueType.Double or HistoryValueType.Boolean)
                    .Select(r => (r.TimestampTicks, r.PropertyName, r.NumericValue)));
            }

            var results = allRows
                .GroupBy(r => (r.propertyName, bucket: (r.timestamp / bucketTicks) * bucketTicks))
                .OrderBy(g => g.Key.bucket)
                .Select(g =>
                {
                    var values = g.Select(r => r.value).ToList();
                    var bucketStart = new DateTimeOffset(g.Key.bucket, TimeSpan.Zero);
                    var bucketEnd = bucketStart.Add(query.BucketSize.Value);
                    return new AggregatedRecord(
                        bucketStart, bucketEnd,
                        Average: values.Average(),
                        Minimum: values.Min(),
                        Maximum: values.Max(),
                        Sum: values.Sum(),
                        Count: values.Count,
                        First: JsonSerializer.SerializeToElement(values.First()),
                        Last: JsonSerializer.SerializeToElement(values.Last()));
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<AggregatedRecord>>(results);
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
        if (MaxRetention <= TimeSpan.Zero)
            return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(MaxRetention).Ticks;
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
- `WhenMaxRetentionSet_ThenOldRecordsEvicted`
- `WhenPartialSnapshotRequested_ThenFiltersByPath`

**Step 3:** Write move tracking tests covering:
- `WhenFollowMovesTrue_ThenQueriesOldPath`
- `WhenMoveChain_ThenFollowsFullChain`
- `WhenFollowMovesFalse_ThenOnlyQueriesExactPath`
- `WhenMoveWithTimeRange_ThenScopesEachPathCorrectly`
- `WhenCyclicMoves_ThenDoesNotInfiniteLoop`
- `WhenSnapshotWithMoves_ThenResolvesAliases`
- `WhenAggregationWithMoves_ThenMergesAcrossRename`
- `WhenNoMoves_ThenFollowMovesBehavesLikeExactPath`

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
- `WriteBatchAsync` — group by partition, batch INSERT per transaction
- `WriteMovesAsync` — write to moves DB
- `QueryAsync` — query across partitions, follow moves via path chain resolution
- `QueryAggregatedAsync` — SQL GROUP BY per partition, merge cross-partition buckets
- `WriteSnapshotAsync` — gzip + store in partition's snapshots table
- `GetSnapshotAsync` — scan partitions backwards for nearest snapshot, replay changes
- `GetSnapshotsAsync` — iterate calling GetSnapshotAsync at intervals

Key implementation notes:
- `ReadJsonValue` uses `JsonSerializer.Deserialize<JsonElement>()` (not `JsonDocument.Parse()`) to avoid memory leaks
- Boolean values read back via `JsonSerializer.SerializeToElement(reader.GetDouble() != 0.0)`
- Null values produce `JsonSerializer.SerializeToElement<object?>(null)`
- Snapshot search scans partitions backwards, stops at first found
- Aggregation uses real SQL GROUP BY, mergeable across partitions

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
- `WhenAggregatingAverage_ThenWeightedByCount`
- `WhenAggregatingMinimum_ThenMinOfMins`
- `WhenAggregatingFirst_ThenEarliestByTimestamp`

Snapshot tests:
- `WhenSnapshotWrittenAndRead_ThenRoundTrips`
- `WhenSnapshotRequestedBetweenSnapshots_ThenReplaysChanges`
- `WhenSnapshotSearchScansBackward_ThenStopsAtFirstFound`

Move tests:
- `WhenFollowMovesTrue_ThenQueriesOldPath`
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

### Task 9: Implement Snapshot Scheduling in HistoryService

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`

**Step 1:** Add snapshot creation method that walks all registered subjects via registry, reads current `[State]` property values, builds `HistorySnapshot`, calls `sink.WriteSnapshotAsync()`.

**Step 2:** Add snapshot scheduling to the flush loop. Check each sink's `SnapshotInterval` against `LastSnapshotTime`. On tick: create snapshot from live graph, write to sink.

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
  "RetentionDays": 365,
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

**Step 3:** Verify no leftover TODOs — search for `throw new NotImplementedException()` in history projects. All should be resolved.

**Step 4:** Commit.
