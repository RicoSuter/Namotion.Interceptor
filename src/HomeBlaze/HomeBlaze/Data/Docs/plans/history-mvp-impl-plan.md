# History MVP Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Ship the History MVP end-to-end: abstractions, in-memory store, Timescale store, edit components, MCP tool, and property history dialog, with unit + integration tests proving every layer.

**Architecture:** Three new packages under `HomeBlaze.History.*` (Abstractions, InMemory, TimescaleDb) plus Blazor edit components. Each store is a `[InterceptorSubject] BackgroundService` that subscribes to the change pipeline via its own `ChangeQueueProcessor`. Writes route values into typed Postgres columns (`value_long`/`value_double`/`value_json`) or per-property ring buffers. Reads dispatch a single-column SQL or per-property buffer scan. A `SubjectRegistry`-level `QueryHistoryAsync` extension merges results across stores (raw split by coverage; bucketed dispatch per-bucket).

**Tech Stack:** .NET 10, C# 13 partial properties (`[InterceptorSubject]`), Npgsql (binary `COPY`), TimescaleDB hypertable + `time_bucket`, MudBlazor 9.2 (`MudTimeSeriesChart`), Testcontainers PostgreSQL, xUnit.

**Companion design doc:** [history-mvp.md](history-mvp.md). Read it before starting; this plan implements it task-by-task.

---

## Phase 0: Prerequisite — Promote `ThroughputCounter`

`ThroughputCounter` currently lives `internal sealed` in `Namotion.Interceptor.OpcUa`. Both history stores need it. Move it to `Namotion.Interceptor.Connectors` (already the home of `ChangeQueueProcessor`, already referenced by both OPC UA and the future history stores) and make it `public`.

### Task 1: Move `ThroughputCounter` to `Namotion.Interceptor.Connectors`

**Files:**
- Move: `src/Namotion.Interceptor.OpcUa/ThroughputCounter.cs` → `src/Namotion.Interceptor.Connectors/ThroughputCounter.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs` (update `using`)
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs` (update `using`)
- Move test: `src/Namotion.Interceptor.OpcUa.Tests/Client/ThroughputCounterTests.cs` → `src/Namotion.Interceptor.Connectors.Tests/ThroughputCounterTests.cs`

**Step 1:** `git mv src/Namotion.Interceptor.OpcUa/ThroughputCounter.cs src/Namotion.Interceptor.Connectors/ThroughputCounter.cs`

**Step 2:** Edit the moved file. Change `namespace Namotion.Interceptor.OpcUa;` to `namespace Namotion.Interceptor.Connectors;`. Change `internal sealed class ThroughputCounter` to `public sealed class ThroughputCounter`.

**Step 3:** `git mv src/Namotion.Interceptor.OpcUa.Tests/Client/ThroughputCounterTests.cs src/Namotion.Interceptor.Connectors.Tests/ThroughputCounterTests.cs`

**Step 4:** Update the test file's namespace from `Namotion.Interceptor.OpcUa.Tests.Client` to `Namotion.Interceptor.Connectors.Tests`. Update `using` directives.

**Step 5:** Update the two OpcUa source files to use the new namespace. In `OpcUaSubjectClientSource.cs` and `OpcUaSubjectServer.cs`, add `using Namotion.Interceptor.Connectors;` and remove any `ThroughputCounter` reference that resolved via the old namespace.

**Step 6:** Build and run existing tests:
```
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category!=Integration"
```
Expected: both green.

**Step 7:** Commit:
```bash
git add -A
git commit -m "refactor: promote ThroughputCounter to Namotion.Interceptor.Connectors

ThroughputCounter is a general change-pipeline utility, not OPC UA specific.
Moving it next to ChangeQueueProcessor lets the upcoming history stores
share one implementation without adding a new dependency."
```

---

## Phase 1: Abstractions package

Define the interface and shared types every consumer (stores, UI, MCP) talks to.

### Task 2: Create `HomeBlaze.History.Abstractions` project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj`

**Step 1:** Create the csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Namotion.Interceptor.Registry\Namotion.Interceptor.Registry.csproj" />
  </ItemGroup>
</Project>
```

**Step 2:** Add the project to the solution:
```bash
dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj
```

**Step 3:** Build:
```bash
dotnet build src/HomeBlaze/HomeBlaze.History.Abstractions
```
Expected: succeeds with no source files (empty assembly).

**Step 4:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/ src/Namotion.Interceptor.slnx
git commit -m "feat: scaffold HomeBlaze.History.Abstractions package"
```

### Task 3: Define query types and `IHistoryStore` interface

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/IHistoryStore.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQuery.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryCoverage.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryAggregation.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryPoint.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistorySeries.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryAggregationNotSupportedException.cs`

**Step 1:** Write `HistoryCoverage.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Range a history store guarantees it can answer queries over.
/// Returning a tighter coverage than physically held is safe.
/// Returning a wider coverage is a bug.
/// </summary>
public readonly record struct HistoryCoverage(DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(HistoryCoverage other) =>
        other.From >= From && other.To <= To;

    public bool Overlaps(HistoryCoverage other) =>
        other.From < To && other.To > From;
}
```

**Step 2:** Write `HistoryAggregation.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

public enum HistoryAggregation { Last, Average, Minimum, Maximum, Sum, Count }
```

**Step 3:** Write `HistoryQuery.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    HistoryAggregation Aggregation = HistoryAggregation.Last,
    int MaxPoints = 10_000);
```

**Step 4:** Write `HistoryPoint.cs`:

```csharp
using System.Text.Json;

namespace HomeBlaze.History.Abstractions;

public record HistoryPoint(
    DateTimeOffset Timestamp,
    double? Number,
    JsonElement? Json);
```

**Step 5:** Write `HistorySeries.cs`:

```csharp
using System.Collections.Immutable;

namespace HomeBlaze.History.Abstractions;

public record HistorySeries(
    string PropertyPath,
    ImmutableArray<HistoryPoint> Points,
    bool Truncated);
```

**Step 6:** Write `HistoryAggregationNotSupportedException.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

public class HistoryAggregationNotSupportedException : InvalidOperationException
{
    public HistoryAggregationNotSupportedException(
        HistoryAggregation aggregation, string propertyPath, string column)
        : base($"Aggregation '{aggregation}' is not supported for property '{propertyPath}' (storage column: {column}).")
    {
        Aggregation = aggregation;
        PropertyPath = propertyPath;
        Column = column;
    }

    public HistoryAggregation Aggregation { get; }
    public string PropertyPath { get; }
    public string Column { get; }
}
```

**Step 7:** Write `IHistoryStore.cs`:

```csharp
using Namotion.Interceptor;

namespace HomeBlaze.History.Abstractions;

public interface IHistoryStore : IInterceptorSubject
{
    int Priority { get; }
    HistoryCoverage Coverage { get; }
    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);
}
```

**Step 8:** Build:
```bash
dotnet build src/HomeBlaze/HomeBlaze.History.Abstractions
```
Expected: succeeds.

**Step 9:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/
git commit -m "feat: add IHistoryStore interface and query types"
```

### Task 4: Implement `HistoryEligibility.HasHistory` (TDD)

**Files:**
- Create test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/HomeBlaze.History.Abstractions.Tests.csproj`
- Create test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/HistoryEligibilityTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryEligibility.cs`

**Step 1:** Create the test csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

Add to solution: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/HomeBlaze.History.Abstractions.Tests.csproj`

**Step 2:** Write the failing tests in `HistoryEligibilityTests.cs`:

```csharp
using System.IO;
using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryEligibilityTests
{
    public enum Color { Red, Green, Blue }

    [Theory]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(short))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(string))]
    [InlineData(typeof(Color))]
    [InlineData(typeof(double?))]
    [InlineData(typeof(int?))]
    public void WhenTypeIsAllowed_ThenIsRecordableTypeIsTrue(Type type)
    {
        // Act & Assert
        Assert.True(HistoryEligibility.IsRecordableType(type));
    }

    [Theory]
    [InlineData(typeof(byte[]))]
    [InlineData(typeof(Stream))]
    [InlineData(typeof(object))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(List<int>))]
    public void WhenTypeIsNotAllowed_ThenIsRecordableTypeIsFalse(Type type)
    {
        // Act & Assert
        Assert.False(HistoryEligibility.IsRecordableType(type));
    }
}
```

**Step 3:** Run tests, verify they fail:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Abstractions.Tests
```
Expected: FAIL with "HistoryEligibility does not contain a definition for IsRecordableType."

**Step 4:** Implement `HistoryEligibility.cs`:

```csharp
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Abstractions;

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
        if (t == typeof(double) || t == typeof(float)) return true;
        if (IsBigIntCompatible(t)) return true;
        if (t == typeof(decimal)) return true;
        if (t == typeof(string)) return true;
        if (t.IsEnum) return true;
        return false;
    }

    internal static bool IsBigIntCompatible(Type t) =>
        t == typeof(long)   || t == typeof(int)   || t == typeof(short) ||
        t == typeof(sbyte)  || t == typeof(byte)  ||
        t == typeof(ushort) || t == typeof(uint)  || t == typeof(ulong) ||
        t == typeof(bool);
}
```

Note: `IsState` and `HasChildren` are extension methods or properties on `RegisteredSubjectProperty`. If they don't compile, look for equivalent names in `Namotion.Interceptor.Registry.Abstractions` (the registry exposes `[State]` detection via attribute metadata; you may need `property.HasAttribute<StateAttribute>()` or similar). Adjust to the actual API.

**Step 5:** Run tests, verify they pass:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Abstractions.Tests
```
Expected: PASS for all type-routing tests.

**Step 6:** Add a `HasHistory` test that uses a real registered subject (separate test class once you confirm the registry API; for now the `IsRecordableType` coverage proves the type rules).

**Step 7:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryEligibility.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/
git commit -m "feat: add HistoryEligibility predicate for recordable types"
```

### Task 5: Implement `HistoryColumns` dispatch (TDD)

**Files:**
- Modify test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/HistoryColumnsTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryColumns.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/ValueColumn.cs`

**Step 1:** Add `ValueColumn.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

public enum ValueColumn { Long, Double, Json }
```

**Step 2:** Write the failing test `HistoryColumnsTests.cs`:

```csharp
using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryColumnsTests
{
    public enum Color { Red, Green }

    [Theory]
    [InlineData(typeof(double),  ValueColumn.Double)]
    [InlineData(typeof(float),   ValueColumn.Double)]
    [InlineData(typeof(double?), ValueColumn.Double)]
    [InlineData(typeof(int),     ValueColumn.Long)]
    [InlineData(typeof(long),    ValueColumn.Long)]
    [InlineData(typeof(short),   ValueColumn.Long)]
    [InlineData(typeof(byte),    ValueColumn.Long)]
    [InlineData(typeof(sbyte),   ValueColumn.Long)]
    [InlineData(typeof(uint),    ValueColumn.Long)]
    [InlineData(typeof(ulong),   ValueColumn.Long)]
    [InlineData(typeof(ushort),  ValueColumn.Long)]
    [InlineData(typeof(bool),    ValueColumn.Long)]
    [InlineData(typeof(decimal), ValueColumn.Json)]
    [InlineData(typeof(string),  ValueColumn.Json)]
    [InlineData(typeof(Color),   ValueColumn.Json)]
    public void WhenTypeIsGiven_ThenValueColumnForReturnsExpected(Type t, ValueColumn expected)
    {
        // Act & Assert
        Assert.Equal(expected, HistoryColumns.ValueColumnFor(t));
    }

    [Fact]
    public void WhenTypeIsUlong_ThenIsUlongPropertyIsTrue()
    {
        // Act & Assert
        Assert.True(HistoryColumns.IsUlongProperty(typeof(ulong)));
        Assert.True(HistoryColumns.IsUlongProperty(typeof(ulong?)));
    }

    [Theory]
    [InlineData(typeof(long))]
    [InlineData(typeof(int))]
    [InlineData(typeof(double))]
    [InlineData(typeof(string))]
    public void WhenTypeIsNotUlong_ThenIsUlongPropertyIsFalse(Type t)
    {
        // Act & Assert
        Assert.False(HistoryColumns.IsUlongProperty(t));
    }
}
```

**Step 3:** Run tests, verify they fail:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Abstractions.Tests --filter HistoryColumnsTests
```
Expected: FAIL with "HistoryColumns does not exist."

**Step 4:** Implement `HistoryColumns.cs`:

```csharp
namespace HomeBlaze.History.Abstractions;

public static class HistoryColumns
{
    public static ValueColumn ValueColumnFor(Type propertyType)
    {
        var t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (t == typeof(double) || t == typeof(float)) return ValueColumn.Double;
        if (HistoryEligibility.IsBigIntCompatible(t)) return ValueColumn.Long;
        return ValueColumn.Json;
    }

    public static bool IsUlongProperty(Type propertyType) =>
        (Nullable.GetUnderlyingType(propertyType) ?? propertyType) == typeof(ulong);
}
```

**Step 5:** Run tests, verify pass:
```bash
dotnet test src/HomeBlaze/HomeBlaze.History.Abstractions.Tests --filter HistoryColumnsTests
```
Expected: PASS.

**Step 6:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/ValueColumn.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryColumns.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/HistoryColumnsTests.cs
git commit -m "feat: add HistoryColumns dispatch helper"
```

### Task 6: Implement bucket alignment helper (TDD)

**Files:**
- Create test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/BucketAlignmentTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/BucketAlignment.cs`

**Step 1:** Failing test:

```csharp
using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class BucketAlignmentTests
{
    [Fact]
    public void WhenTimestampIsAtBucketBoundary_ThenBucketStartIsThatTimestamp()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 5, 20, 12, 30, 0, TimeSpan.Zero);
        var bucket = TimeSpan.FromMinutes(1);

        // Act
        var start = BucketAlignment.BucketStart(ts, bucket);

        // Assert
        Assert.Equal(ts, start);
    }

    [Fact]
    public void WhenTimestampIsInsideBucket_ThenBucketStartIsBoundary()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 5, 20, 12, 30, 37, TimeSpan.Zero);
        var bucket = TimeSpan.FromMinutes(1);

        // Act
        var start = BucketAlignment.BucketStart(ts, bucket);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 5, 20, 12, 30, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void WhenBucketIsTenMinutes_ThenBucketStartsAtMultipleOfTen()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 5, 20, 12, 37, 23, TimeSpan.Zero);
        var bucket = TimeSpan.FromMinutes(10);

        // Act
        var start = BucketAlignment.BucketStart(ts, bucket);

        // Assert
        Assert.Equal(new DateTimeOffset(2026, 5, 20, 12, 30, 0, TimeSpan.Zero), start);
    }

    [Fact]
    public void WhenSameTimestampAndBucket_ThenSqlTimeBucketAndBucketAlignmentMatch()
    {
        // This test exists to lock the formula. PG's time_bucket('1min', ts) for
        // ts='2026-05-20T12:30:37Z' returns '2026-05-20T12:30:00Z'. Our formula
        // must match for cross-store concatenation to work without interleaving.
        // (See integration test 'Bucket alignment' for the real cross-check.)
        var ts = new DateTimeOffset(2026, 5, 20, 12, 30, 37, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 5, 20, 12, 30, 0, TimeSpan.Zero),
            BucketAlignment.BucketStart(ts, TimeSpan.FromMinutes(1)));
    }
}
```

**Step 2:** Run, verify fail.

**Step 3:** Implement:

```csharp
namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Epoch-anchored bucket-start math. Must match Postgres time_bucket(interval, ts)
/// so in-memory and Timescale buckets concatenate cleanly without interleaving.
/// </summary>
public static class BucketAlignment
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    public static DateTimeOffset BucketStart(DateTimeOffset timestamp, TimeSpan bucket)
    {
        var ticksFromEpoch = (timestamp - Epoch).Ticks;
        var bucketTicks = bucket.Ticks;
        var bucketIndex = ticksFromEpoch / bucketTicks;
        return Epoch.AddTicks(bucketIndex * bucketTicks);
    }
}
```

**Step 4:** Run, verify pass.

**Step 5:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/BucketAlignment.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/BucketAlignmentTests.cs
git commit -m "feat: add epoch-anchored BucketAlignment helper"
```

### Task 7: Cross-store merge — raw path (TDD)

**Files:**
- Create test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/QueryHistoryAsyncRawTests.cs`
- Create test fake: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/FakeHistoryStore.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQueryExtensions.cs`

**Step 1:** Write the fake store helper:

```csharp
using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Abstractions.Tests;

[InterceptorSubject]
public partial class FakeHistoryStore : IHistoryStore
{
    private readonly List<HistoryPoint> _samples;

    public FakeHistoryStore(int priority, HistoryCoverage coverage, IEnumerable<HistoryPoint>? samples = null)
    {
        Priority = priority;
        Coverage = coverage;
        _samples = samples?.ToList() ?? new List<HistoryPoint>();
    }

    public int Priority { get; }
    public HistoryCoverage Coverage { get; }
    public bool ShouldThrow { get; set; }
    public int QueryCount { get; private set; }
    public List<HistoryQuery> Queries { get; } = new();

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken ct)
    {
        QueryCount++;
        Queries.Add(query);
        if (ShouldThrow) throw new InvalidOperationException("fake store error");

        var filtered = _samples
            .Where(p => p.Timestamp >= query.From && p.Timestamp < query.To)
            .OrderBy(p => p.Timestamp)
            .Take(query.MaxPoints + 1)
            .ToList();
        var truncated = filtered.Count > query.MaxPoints;
        if (truncated) filtered.RemoveAt(filtered.Count - 1);
        return Task.FromResult(new HistorySeries(query.PropertyPath, filtered.ToImmutableArray(), truncated));
    }
}
```

(The `[InterceptorSubject]` attribute is required so the fake satisfies `IInterceptorSubject`. Source generation provides the rest.)

**Step 2:** Failing test `QueryHistoryAsyncRawTests.cs`:

```csharp
using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class QueryHistoryAsyncRawTests
{
    private static SubjectRegistry BuildRegistryWith(params IHistoryStore[] stores)
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        foreach (var store in stores) context.AddSubject(store);
        return context.GetService<SubjectRegistry>();
    }

    [Fact]
    public async Task WhenSingleStoreCoversRange_ThenAllPointsComeFromIt()
    {
        // Arrange
        var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new FakeHistoryStore(
            priority: 10,
            coverage: new HistoryCoverage(t0, t0.AddMinutes(30)),
            samples: new[]
            {
                new HistoryPoint(t0.AddMinutes(1), 1.0, null),
                new HistoryPoint(t0.AddMinutes(2), 2.0, null),
            });
        var registry = BuildRegistryWith(store);

        // Act
        var result = await registry.QueryHistoryAsync(
            new HistoryQuery("Devices/X/Temperature", t0, t0.AddMinutes(30)),
            CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Points.Length);
        Assert.Equal(1, store.QueryCount);
    }

    [Fact]
    public async Task WhenTwoStoresHaveDisjointCoverage_ThenEachServesItsRange()
    {
        // Arrange
        var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var inMem = new FakeHistoryStore(
            priority: 100,
            coverage: new HistoryCoverage(t0.AddMinutes(25), t0.AddMinutes(30)),
            samples: new[] { new HistoryPoint(t0.AddMinutes(28), 28.0, null) });
        var timescale = new FakeHistoryStore(
            priority: 10,
            coverage: new HistoryCoverage(t0, t0.AddMinutes(25)),
            samples: new[] { new HistoryPoint(t0.AddMinutes(5), 5.0, null) });
        var registry = BuildRegistryWith(inMem, timescale);

        // Act
        var result = await registry.QueryHistoryAsync(
            new HistoryQuery("Devices/X/T", t0, t0.AddMinutes(30)),
            CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Points.Length);
        Assert.Equal(5.0, result.Points[0].Number);
        Assert.Equal(28.0, result.Points[1].Number);
        Assert.Equal(1, inMem.QueryCount);
        Assert.Equal(1, timescale.QueryCount);
    }

    [Fact]
    public async Task WhenTwoStoresOverlap_ThenHigherPriorityWinsTimestamp()
    {
        // Arrange
        var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var sampleTs = t0.AddMinutes(20);
        var inMem = new FakeHistoryStore(
            priority: 100,
            coverage: new HistoryCoverage(t0.AddMinutes(15), t0.AddMinutes(30)),
            samples: new[] { new HistoryPoint(sampleTs, 100.0, null) });
        var timescale = new FakeHistoryStore(
            priority: 10,
            coverage: new HistoryCoverage(t0, t0.AddMinutes(30)),
            samples: new[] { new HistoryPoint(sampleTs, 10.0, null) });
        var registry = BuildRegistryWith(inMem, timescale);

        // Act
        var result = await registry.QueryHistoryAsync(
            new HistoryQuery("Devices/X/T", t0, t0.AddMinutes(30)),
            CancellationToken.None);

        // Assert: in-memory's value wins for the overlapping timestamp.
        var match = result.Points.Single(p => p.Timestamp == sampleTs);
        Assert.Equal(100.0, match.Number);
    }

    [Fact]
    public async Task WhenStoreThrows_ThenExceptionPropagates()
    {
        // Arrange
        var store = new FakeHistoryStore(
            priority: 10,
            coverage: new HistoryCoverage(DateTimeOffset.MinValue, DateTimeOffset.MaxValue))
        { ShouldThrow = true };
        var registry = BuildRegistryWith(store);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.QueryHistoryAsync(
                new HistoryQuery("p", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public async Task WhenRegistryHasNoStores_ThenResultIsEmpty()
    {
        // Arrange
        var registry = BuildRegistryWith();

        // Act
        var result = await registry.QueryHistoryAsync(
            new HistoryQuery("p", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow),
            CancellationToken.None);

        // Assert
        Assert.Empty(result.Points);
    }
}
```

**Step 3:** Run, verify fail.

**Step 4:** Implement `HistoryQueryExtensions.cs` (raw path only for now):

```csharp
using System.Collections.Immutable;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.History.Abstractions;

public static class HistoryQueryExtensions
{
    public static async Task<HistorySeries> QueryHistoryAsync(
        this SubjectRegistry registry,
        HistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var stores = registry.KnownSubjects
            .OfType<IHistoryStore>()
            .OrderByDescending(s => s.Priority)
            .ToArray();

        if (stores.Length == 0)
            return new HistorySeries(query.PropertyPath, ImmutableArray<HistoryPoint>.Empty, false);

        if (query.Bucket is null)
            return await RawMergeAsync(stores, query, cancellationToken);

        // Bucketed path: implemented in Task 8
        throw new NotImplementedException("Bucketed merge implemented in Task 8.");
    }

    private static async Task<HistorySeries> RawMergeAsync(
        IHistoryStore[] stores, HistoryQuery query, CancellationToken ct)
    {
        var gaps = new List<HistoryCoverage> { new(query.From, query.To) };
        var merged = new SortedDictionary<DateTimeOffset, HistoryPoint>();
        var truncated = false;

        foreach (var store in stores)
        {
            if (gaps.Count == 0) break;
            var subRanges = IntersectAll(gaps, store.Coverage).ToList();
            foreach (var sub in subRanges)
            {
                var subSeries = await store.QueryAsync(
                    query with { From = sub.From, To = sub.To }, ct);
                truncated |= subSeries.Truncated;
                foreach (var point in subSeries.Points)
                    merged.TryAdd(point.Timestamp, point);
            }
            gaps = SubtractAll(gaps, store.Coverage).ToList();
        }

        return new HistorySeries(
            query.PropertyPath,
            merged.Values.ToImmutableArray(),
            truncated);
    }

    private static IEnumerable<HistoryCoverage> IntersectAll(
        IEnumerable<HistoryCoverage> ranges, HistoryCoverage other)
    {
        foreach (var r in ranges)
        {
            var from = r.From > other.From ? r.From : other.From;
            var to   = r.To   < other.To   ? r.To   : other.To;
            if (from < to) yield return new HistoryCoverage(from, to);
        }
    }

    private static IEnumerable<HistoryCoverage> SubtractAll(
        IEnumerable<HistoryCoverage> ranges, HistoryCoverage other)
    {
        foreach (var r in ranges)
        {
            if (r.To <= other.From || r.From >= other.To) { yield return r; continue; }
            if (r.From < other.From) yield return new HistoryCoverage(r.From, other.From);
            if (r.To > other.To)     yield return new HistoryCoverage(other.To, r.To);
        }
    }
}
```

**Step 5:** Run, verify pass for all 5 tests.

**Step 6:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQueryExtensions.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/QueryHistoryAsyncRawTests.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/FakeHistoryStore.cs
git commit -m "feat: QueryHistoryAsync raw merge with coverage-based dispatch"
```

### Task 8: Cross-store merge — bucketed per-bucket dispatch (TDD)

**Files:**
- Create test: `src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/QueryHistoryAsyncBucketedTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQueryExtensions.cs`

**Step 1:** Add bucketed tests. The fake store needs to honor bucket queries — extend `FakeHistoryStore.QueryAsync` to compute per-bucket aggregates over its samples when `query.Bucket` is non-null. Use `BucketAlignment.BucketStart` and group by bucket. For `Average`, average the `Number` values; for `Count`, count non-null entries; etc.

Sample failing test:

```csharp
[Fact]
public async Task WhenBucketFullyContainedByHigherPriority_ThenOnlyHigherPriorityQueried()
{
    // Arrange
    var t0 = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
    var inMem = new FakeHistoryStore(
        priority: 100,
        coverage: new HistoryCoverage(t0.AddMinutes(28), t0.AddMinutes(30)),
        samples: new[] { new HistoryPoint(t0.AddMinutes(29), 29.0, null) });
    var timescale = new FakeHistoryStore(
        priority: 10,
        coverage: new HistoryCoverage(t0, t0.AddMinutes(30)),
        samples: Enumerable.Range(0, 30).Select(i => new HistoryPoint(t0.AddMinutes(i), i, null)));
    var registry = BuildRegistryWith(inMem, timescale);

    // Act: 30-min range, 1-min buckets. Last 2 buckets fit in-memory.
    var result = await registry.QueryHistoryAsync(
        new HistoryQuery("p", t0, t0.AddMinutes(30),
            Bucket: TimeSpan.FromMinutes(1),
            Aggregation: HistoryAggregation.Average),
        CancellationToken.None);

    // Assert
    Assert.Equal(30, result.Points.Length);
    // Buckets 28 and 29 (the last two) came from in-memory (only one query against the in-mem range).
    Assert.Equal(1, inMem.QueryCount);
    Assert.Equal(1, timescale.QueryCount);
}
```

Add more tests for:
- Effective-range clipping at the right edge
- Single bucket spanning both stores → falls back to the wider-coverage store
- Buckets entirely outside any store coverage → not present in output

**Step 2:** Run, verify fail (NotImplementedException).

**Step 3:** Implement the bucketed merge in `HistoryQueryExtensions.cs`:

```csharp
private static async Task<HistorySeries> BucketedMergeAsync(
    IHistoryStore[] stores, HistoryQuery query, CancellationToken ct)
{
    var bucket = query.Bucket!.Value;
    var buckets = EnumerateBuckets(query.From, query.To, bucket).ToList();

    var assignments = buckets
        .Select(b => (Bucket: b, Store: AssignBucket(stores, b, query)))
        .Where(a => a.Store is not null)
        .ToList();

    var merged = new SortedDictionary<DateTimeOffset, HistoryPoint>();
    var truncated = false;

    // Group consecutive buckets assigned to the same store into one sub-query.
    var groupedRanges = GroupConsecutive(assignments);
    foreach (var group in groupedRanges)
    {
        var subQuery = query with
        {
            From = group.First().Bucket.From,
            To   = group.Last().Bucket.To
        };
        var part = await group.Key!.QueryAsync(subQuery, ct);
        truncated |= part.Truncated;
        foreach (var point in part.Points)
            merged[point.Timestamp] = point;
    }

    return new HistorySeries(query.PropertyPath, merged.Values.ToImmutableArray(), truncated);
}

private static IEnumerable<HistoryCoverage> EnumerateBuckets(
    DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
{
    var start = BucketAlignment.BucketStart(from, bucket);
    for (var t = start; t < to; t = t + bucket)
        yield return new HistoryCoverage(t, t + bucket);
}

private static IHistoryStore? AssignBucket(IHistoryStore[] stores, HistoryCoverage bucket, HistoryQuery query)
{
    var effective = new HistoryCoverage(
        From: bucket.From > query.From ? bucket.From : query.From,
        To:   bucket.To   < query.To   ? bucket.To   : query.To);

    // Highest-priority store whose coverage fully contains the effective range.
    foreach (var s in stores)
        if (s.Coverage.Contains(effective)) return s;

    // Fall back to lowest-priority store with overlap.
    for (var i = stores.Length - 1; i >= 0; i--)
        if (stores[i].Coverage.Overlaps(effective)) return stores[i];

    return null;
}

private static IEnumerable<IGrouping<IHistoryStore, (HistoryCoverage Bucket, IHistoryStore? Store)>>
    GroupConsecutive(List<(HistoryCoverage Bucket, IHistoryStore? Store)> assignments)
{
    return assignments
        .Select((a, idx) => (a.Bucket, a.Store, Group: idx == 0 || assignments[idx - 1].Store != a.Store ? idx : -1))
        // Easier: use a running counter.
        .GroupBy(x => x.Store!);
    // NOTE: A cleaner implementation walks the list once, accumulating runs.
    // Replace this LINQ with a manual loop if you need strict consecutiveness.
}
```

(The `GroupConsecutive` implementation above is a placeholder; in code, walk the list and emit runs. The tests will catch a wrong implementation. Replace with: scan assignments, when store changes, close the current group.)

**Step 4:** Update the entry point to call `BucketedMergeAsync` when `query.Bucket is not null`.

**Step 5:** Run all tests, verify pass.

**Step 6:** Commit:
```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQueryExtensions.cs \
        src/HomeBlaze/HomeBlaze.History.Abstractions.Tests/QueryHistoryAsyncBucketedTests.cs
git commit -m "feat: QueryHistoryAsync bucketed merge with per-bucket dispatch"
```

---

## Phase 2: In-memory store

### Task 9: Create `HomeBlaze.History.InMemory` project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory/HomeBlaze.History.InMemory.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory.Tests/HomeBlaze.History.InMemory.Tests.csproj`

**Step 1:** Create the source csproj. Target `net10.0`, reference `HomeBlaze.History.Abstractions`, `Namotion.Interceptor.Connectors`, `HomeBlaze.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`.

**Step 2:** Create the test csproj. Reference xUnit, the source project, `HomeBlaze.History.Abstractions.Tests` (for the fake store, or duplicate it locally).

**Step 3:** Add both to solution.

**Step 4:** Build, verify both empty assemblies succeed.

**Step 5:** Commit.

### Task 10: Define `Sample` record struct

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory/Sample.cs`

**Step 1:** Define:

```csharp
namespace HomeBlaze.History.InMemory;

internal readonly record struct Sample(DateTimeOffset Timestamp, object? Value);
```

**Step 2:** Commit.

### Task 11: `PropertyBuffer.Add` with ring buffer (TDD)

**Files:**
- Create test: `src/HomeBlaze/HomeBlaze.History.InMemory.Tests/PropertyBufferAddTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory/PropertyBuffer.cs`

**Step 1:** Failing test:

```csharp
using HomeBlaze.History.InMemory;
using Xunit;

namespace HomeBlaze.History.InMemory.Tests;

public class PropertyBufferAddTests
{
    [Fact]
    public void WhenSampleAddedAndCapacityNotExceeded_ThenCountIncrements()
    {
        // Arrange
        var buffer = new PropertyBuffer(maxPoints: 10, maxAge: TimeSpan.FromMinutes(5));

        // Act
        buffer.Add(new Sample(DateTimeOffset.UtcNow, 42.0));

        // Assert
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void WhenCapacityExceeded_ThenOldestEvictedAndEvictedCountIncrements()
    {
        // Arrange
        var buffer = new PropertyBuffer(maxPoints: 3, maxAge: TimeSpan.FromMinutes(5));
        var t0 = DateTimeOffset.UtcNow;

        // Act
        for (var i = 0; i < 5; i++) buffer.Add(new Sample(t0.AddSeconds(i), i));

        // Assert
        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.EvictedCount);
        Assert.Equal(2, buffer.OldestTimestamp.Subtract(t0).TotalSeconds, 1);
    }
}
```

**Step 2:** Run, verify fail.

**Step 3:** Implement `PropertyBuffer.cs`:

```csharp
namespace HomeBlaze.History.InMemory;

internal sealed class PropertyBuffer
{
    private readonly object _lock = new();
    private readonly Sample[] _ring;
    private readonly TimeSpan _maxAge;
    private int _head;
    private int _count;

    public PropertyBuffer(int maxPoints, TimeSpan maxAge)
    {
        _ring = new Sample[maxPoints];
        _maxAge = maxAge;
    }

    public int Count { get { lock (_lock) return _count; } }
    public long EvictedCount { get; private set; }

    public DateTimeOffset OldestTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _count == 0 ? DateTimeOffset.MinValue : _ring[_head].Timestamp;
            }
        }
    }

    public void Add(Sample sample)
    {
        lock (_lock)
        {
            if (_count < _ring.Length)
            {
                _ring[(_head + _count) % _ring.Length] = sample;
                _count++;
            }
            else
            {
                // Full: overwrite the oldest.
                _ring[_head] = sample;
                _head = (_head + 1) % _ring.Length;
                EvictedCount++;
            }
        }
    }
}
```

**Step 4:** Run, verify pass.

**Step 5:** Commit.

### Task 12: `PropertyBuffer.Range` with binary search (TDD)

**Step 1:** Failing test for range query that:
- Returns samples in `[from, to)` ordered by ts.
- Respects `maxPoints` and sets `truncated`.
- Trims by age first (samples older than `maxAge` excluded).

**Step 2:** Implement:

```csharp
public ImmutableArray<Sample> Range(DateTimeOffset from, DateTimeOffset to, int maxPoints, out bool truncated)
{
    lock (_lock)
    {
        TrimExpired();
        if (_count == 0) { truncated = false; return ImmutableArray<Sample>.Empty; }

        // Binary search in the logical view.
        var startIdx = LowerBound(from);
        var endIdx = LowerBound(to);
        var available = endIdx - startIdx;
        truncated = available > maxPoints;
        var taken = truncated ? maxPoints : available;
        var builder = ImmutableArray.CreateBuilder<Sample>(taken);
        for (var i = 0; i < taken; i++)
            builder.Add(_ring[(_head + startIdx + i) % _ring.Length]);
        return builder.ToImmutable();
    }
}

private int LowerBound(DateTimeOffset ts)
{
    int lo = 0, hi = _count;
    while (lo < hi)
    {
        var mid = (lo + hi) / 2;
        if (_ring[(_head + mid) % _ring.Length].Timestamp < ts) lo = mid + 1;
        else hi = mid;
    }
    return lo;
}

private void TrimExpired()
{
    var cutoff = DateTimeOffset.UtcNow - _maxAge;
    while (_count > 0 && _ring[_head].Timestamp < cutoff)
    {
        _head = (_head + 1) % _ring.Length;
        _count--;
        EvictedCount++;
    }
}
```

**Step 3:** Verify with edge-case tests (empty buffer, all-expired, partial expire, range entirely outside data, truncation).

**Step 4:** Commit.

### Task 13: `PropertyBuffer.Bucket` with aggregations (TDD)

**Step 1:** Failing tests, one per `HistoryAggregation`:
- `Last`: returns the last sample's value per bucket.
- `Average`: arithmetic mean of `Number`.
- `Minimum` / `Maximum`: min/max over `Number`.
- `Sum`: sum of `Number`.
- `Count`: count of samples regardless of value type.

**Step 2:** Implement `Bucket(...)` as a single linear pass over the logical view. Use `BucketAlignment.BucketStart`. Group samples by bucket start; for each bucket compute the aggregate. Output `ImmutableArray<HistoryPoint>`. For aggregations not supported on `value_json` (i.e., when the property's `ValueColumn` is `Json`), throw `HistoryAggregationNotSupportedException`. Note: the buffer doesn't know the property type — the store passes a `ValueColumn` hint into `Bucket`.

```csharp
public ImmutableArray<HistoryPoint> Bucket(
    DateTimeOffset from, DateTimeOffset to, TimeSpan bucket,
    HistoryAggregation aggregation, ValueColumn column, int maxBuckets, out bool truncated)
{
    // Validate
    if (column == ValueColumn.Json &&
        aggregation is not (HistoryAggregation.Last or HistoryAggregation.Count))
    {
        throw new HistoryAggregationNotSupportedException(aggregation, "(in-memory)", "value_json");
    }
    // ... linear scan, group by BucketAlignment.BucketStart, compute per-bucket aggregate.
}
```

**Step 3:** Run tests, verify pass.

**Step 4:** Commit.

### Task 14: `InMemoryHistoryStore` skeleton

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory/InMemoryHistoryStore.cs`

**Step 1:** Define the subject. Look at `OpcUaServer.cs` as a template. Required pieces:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;
using System.Collections.Concurrent;

namespace HomeBlaze.History.InMemory;

[Category("History")]
[Description("In-memory history store (hot buffer; dev/test).")]
[InterceptorSubject]
public partial class InMemoryHistoryStore : BackgroundService, IHistoryStore, IConfigurable, ITitleProvider, IIconProvider
{
    private readonly ConcurrentDictionary<string, PropertyBuffer> _buffers = new();
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
    private readonly ILogger<InMemoryHistoryStore> _logger;

    // Configuration
    [Configuration] public partial string Name { get; set; }
    [Configuration] public partial bool IsEnabled { get; set; }
    [Configuration] public partial TimeSpan? MaxAge { get; set; }
    [Configuration] public partial int? MaxPointsPerProperty { get; set; }
    [Configuration] public partial TimeSpan? BufferTime { get; set; }
    [Configuration] public partial int? MaxJsonSize { get; set; }

    // State
    [State] public partial ServiceStatus Status { get; set; }
    [State] public partial string? StatusMessage { get; set; }
    [State] public partial long RecordedCount { get; set; }
    [State] public partial long OversizeCount { get; set; }
    [State] public partial long EvictedCount { get; set; }
    [State] public partial int TrackedPropertyCount { get; set; }
    [State] public partial long TotalSampleCount { get; set; }
    [State] public partial long EstimatedMemoryBytes { get; set; }
    [State] public partial double? IncomingChangesPerSecond { get; set; }
    [State] public partial double? RecordedChangesPerSecond { get; set; }

    // IHistoryStore
    public int Priority => 100;
    public HistoryCoverage Coverage
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var maxAge = MaxAge ?? TimeSpan.FromSeconds(60);
            var from = _startTime > now - maxAge ? _startTime : now - maxAge;
            // Also clamp to actual oldest sample, since cold properties may not reach maxAge.
            return new HistoryCoverage(from, now);
        }
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken ct) => /* Task 17 */;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) => /* Task 16 */;
}
```

Adjust to actual `IConfigurable` / `ITitleProvider` / `IIconProvider` interfaces in `HomeBlaze.Abstractions`; copy the patterns from `OpcUaServer.cs`.

**Step 2:** Build. Source generator should produce backing fields for partial properties. Fix any compile errors (likely around the HomeBlaze interfaces).

**Step 3:** Commit.

### Task 15: Wire ChangeQueueProcessor + HasHistory gate + Record dispatch (TDD)

**Step 1:** Tests:
- `WhenStateChangesArrive_ThenSamplesAreAdded`
- `WhenConfigurationChangesArrive_ThenNothingAdded`
- `WhenRefusedTypeChanges_ThenNothingAdded` (e.g., a property whose declared type is `byte[]`)

Use a minimal interceptor-subject set up in the test that registers the store, then triggers property changes and waits for them to be recorded.

**Step 2:** Implement `ExecuteAsync`:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var processor = new ChangeQueueProcessor(
        context: this.GetContext(),
        bufferTime: BufferTime ?? TimeSpan.FromMilliseconds(250),
        handler: HandleBatch,
        logger: _logger);

    await processor.RunAsync(stoppingToken);
}

private Task HandleBatch(IReadOnlyList<PropertyChangedContext> changes)
{
    foreach (var change in changes)
    {
        _incomingThroughput.Add(1);
        var registered = change.Property.TryGetRegisteredProperty();
        if (registered is null || !registered.HasHistory()) continue;

        var path = /* resolve path via PathProvider */;
        var value = change.NewValue;
        var sample = SerializeOrPlaceholder(value);
        var buffer = _buffers.GetOrAdd(path,
            _ => new PropertyBuffer(MaxPointsPerProperty ?? 1000, MaxAge ?? TimeSpan.FromSeconds(60)));
        buffer.Add(sample);
        _recordedThroughput.Add(1);
        RecordedCount++;
    }
    UpdateMetrics();
    return Task.CompletedTask;
}
```

The exact `ChangeQueueProcessor` constructor and `PropertyChangedContext` may differ; cross-check against `Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` and how `OpcUaServer` wires it up.

**Step 3:** Implement throughput metrics:
- `_incomingThroughput = new ThroughputCounter();`
- `_recordedThroughput = new ThroughputCounter();`
- Update `IncomingChangesPerSecond = _incomingThroughput.CurrentRate;` on a timer or per batch.

**Step 4:** Run tests, verify pass.

**Step 5:** Commit.

### Task 16: Oversize placeholder for long strings (TDD)

**Step 1:** Test:
- Write a string longer than `MaxJsonSize` to a recordable property.
- Verify the sample value is a `JsonElement` matching `{"$oversize": true, "size": N}`.
- Verify `OversizeCount` incremented.

**Step 2:** Implement `SerializeOrPlaceholder`:

```csharp
private Sample SerializeOrPlaceholder(object? value)
{
    if (value is null) return new Sample(DateTimeOffset.UtcNow, null);

    // Numerics/bool: store as the value object directly.
    if (value is double or float or int or long or short or sbyte or byte or
        ushort or uint or ulong or bool or decimal)
        return new Sample(DateTimeOffset.UtcNow, value);

    // Strings: check size before storing.
    if (value is string s)
    {
        var maxSize = MaxJsonSize ?? 8 * 1024;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(s);
        if (byteCount > maxSize)
        {
            OversizeCount++;
            return new Sample(DateTimeOffset.UtcNow, MakePlaceholder(byteCount));
        }
        return new Sample(DateTimeOffset.UtcNow, value);
    }

    // Enums: store the name.
    if (value.GetType().IsEnum)
        return new Sample(DateTimeOffset.UtcNow, value.ToString());

    // Should not happen if HasHistory() did its job.
    return new Sample(DateTimeOffset.UtcNow, value);
}

private static JsonElement MakePlaceholder(int size) =>
    JsonSerializer.SerializeToElement(new { dollarOversize = true, size }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = null,
    });
// Note: serialize with literal "$oversize" key — System.Text.Json doesn't easily emit "$" in property names;
// use JsonObject or write the JSON string manually.
```

The `$oversize` key needs careful JSON construction; use `JsonObject` from `System.Text.Json.Nodes` or hand-build the JSON:

```csharp
var placeholder = new System.Text.Json.Nodes.JsonObject
{
    ["$oversize"] = true,
    ["size"] = size
};
return JsonDocument.Parse(placeholder.ToJsonString()).RootElement;
```

**Step 3:** Run, verify pass.

**Step 4:** Commit.

### Task 17: `InMemoryHistoryStore.QueryAsync` (TDD)

**Step 1:** Tests:
- Raw query returns samples from the property's buffer.
- Bucketed query returns per-bucket aggregates.
- Query for unknown path returns empty series.
- Aggregations on a `value_json`-stored property throw `HistoryAggregationNotSupportedException`.

**Step 2:** Implement:

```csharp
public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken ct)
{
    if (!_buffers.TryGetValue(query.PropertyPath, out var buffer))
        return Task.FromResult(new HistorySeries(query.PropertyPath, ImmutableArray<HistoryPoint>.Empty, false));

    // Determine the property's value column for aggregation gating.
    // We don't have direct type access here; the buffer stores object?,
    // so we look at the first sample's runtime type as a proxy (or pass column via the query, future).
    var sampleType = PeekSampleType(buffer);
    var column = sampleType is null ? ValueColumn.Json : HistoryColumns.ValueColumnFor(sampleType);

    if (query.Bucket is null)
    {
        var raw = buffer.Range(query.From, query.To, query.MaxPoints, out var truncated);
        var points = raw.Select(s => ToHistoryPoint(s)).ToImmutableArray();
        return Task.FromResult(new HistorySeries(query.PropertyPath, points, truncated));
    }
    else
    {
        var bucketed = buffer.Bucket(
            query.From, query.To, query.Bucket.Value, query.Aggregation, column, maxBuckets: 1000, out var truncated);
        return Task.FromResult(new HistorySeries(query.PropertyPath, bucketed, truncated));
    }
}
```

**Step 3:** Verify all tests pass.

**Step 4:** Commit.

### Task 18: Metrics roll-up (TrackedPropertyCount, TotalSampleCount, EstimatedMemoryBytes)

**Step 1:** Test that after recording N samples across M properties, the state metrics report sensible values.

**Step 2:** Implement `UpdateMetrics()` invoked at the end of each batch:

```csharp
private void UpdateMetrics()
{
    var total = 0L;
    var props = 0;
    var evicted = 0L;
    foreach (var b in _buffers.Values)
    {
        total += b.Count;
        evicted += b.EvictedCount;
        props++;
    }
    TrackedPropertyCount = props;
    TotalSampleCount = total;
    EstimatedMemoryBytes = total * 50;
    EvictedCount = evicted;
    IncomingChangesPerSecond = _incomingThroughput.CurrentRate;
    RecordedChangesPerSecond = _recordedThroughput.CurrentRate;
}
```

**Step 3:** Commit.

### Task 19: In-memory store under-load shakedown test

**Step 1:** Test: spawn 10k properties, push 100 changes each, query several at random, assert counts. Also a stress test with a `Stopwatch` to confirm reasonable per-op time (<1µs per add). Mark as `[Trait("Category", "Slow")]` if it takes >1s so default `dotnet test` runs stay fast.

**Step 2:** Commit.

---

## Phase 3: In-memory Blazor

### Task 20: Create `HomeBlaze.History.InMemory.Blazor` project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory.Blazor/HomeBlaze.History.InMemory.Blazor.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory.Blazor/_Imports.razor`

Pattern: mirror `HomeBlaze.OpcUa.Blazor`. Target net10.0, RazorSdk, reference `MudBlazor` 9.2.*, `HomeBlaze.Components.Abstractions`, `HomeBlaze.History.InMemory`.

### Task 21: `InMemoryHistoryStoreEditComponent.razor`

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.InMemory.Blazor/InMemoryHistoryStoreEditComponent.razor`

Pattern: copy `OpcUaServerEditComponent.razor` and adapt fields. Show: Name, IsEnabled, MaxAge, MaxPointsPerProperty, BufferTime, MaxJsonSize. Wire `[SubjectComponent(SubjectComponentType.Edit, typeof(InMemoryHistoryStore))]` + `ISubjectEditComponent`.

Commit.

---

## Phase 4: Timescale store

### Task 22: Create `HomeBlaze.History.TimescaleDb` project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb/HomeBlaze.History.TimescaleDb.csproj`

Reference: `HomeBlaze.History.Abstractions`, `Namotion.Interceptor.Connectors`, `Npgsql` (latest), `HomeBlaze.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`.

### Task 23: `TimescaleDbHistoryStore` skeleton

Same pattern as `InMemoryHistoryStore`: `[InterceptorSubject] partial class : BackgroundService, IHistoryStore`. Add `[Configuration]` for ConnectionString, Retention, BufferTime, FlushInterval, MaxJsonSize. Add `[State]` for Status, StatusMessage, QueueDepth, DropCount, OversizeCount, RecordedCount, IncomingChangesPerSecond, RecordedChangesPerSecond, LastFlushUtc, LastError. Priority = 10.

Commit.

### Task 24: Schema bootstrap (integration test)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/HomeBlaze.History.TimescaleDb.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/TimescaleDbFixture.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests/SchemaBootstrapTests.cs`

**Step 1:** Add Testcontainers + xUnit references. Add `[Trait("Category", "Integration")]` at the class level. Use `IClassFixture<TimescaleDbFixture>`.

**Step 2:** `TimescaleDbFixture.cs`:

```csharp
using Testcontainers.PostgreSql;

namespace HomeBlaze.History.TimescaleDb.Tests;

public sealed class TimescaleDbFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder()
            .WithImage("timescale/timescaledb:latest-pg16")
            .WithDatabase("history")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
```

**Step 3:** Failing test that asserts `property_history` table + hypertable + index exist after the store starts. Verify second start is a no-op.

**Step 4:** Implement `EnsureSchemaAsync(connectionString, ct)` in the store. Idempotent SQL:

```sql
CREATE TABLE IF NOT EXISTS property_history (
  ts            timestamptz       NOT NULL,
  path          text              NOT NULL,
  value_long    bigint            NULL,
  value_double  double precision  NULL,
  value_json    jsonb             NULL
);
SELECT create_hypertable('property_history', 'ts', if_not_exists => true);
CREATE INDEX IF NOT EXISTS ix_property_history_path_ts ON property_history (path, ts DESC);
```

**Step 5:** Verify test passes. Commit.

### Task 25: Per-property write routing into typed columns (integration test)

**Step 1:** Test: instantiate the store, push synthetic changes (one per supported type), query the underlying table via raw Npgsql, assert correct column populated per row.

Cover:
- `double` 3.14 → `value_double = 3.14`, others null
- `long` 9_007_199_254_740_993 (above 2^53!) → `value_long = 9_007_199_254_740_993` exactly
- `bool true` → `value_long = 1`
- `decimal 123.4567890123456789m` → `value_json = 123.4567890123456789`, lossless round-trip
- `string "hello"` → `value_json = "hello"`
- enum `Color.Red` → `value_json = "Red"`

**Step 2:** Implement value routing in the store. For COPY:

```csharp
private async Task FlushBatchAsync(IReadOnlyList<(string Path, Sample Sample)> batch, CancellationToken ct)
{
    await using var conn = await OpenAsync(ct);
    await using var writer = await conn.BeginBinaryImportAsync(
        "COPY property_history (ts, path, value_long, value_double, value_json) FROM STDIN (FORMAT BINARY)",
        ct);
    foreach (var (path, sample) in batch)
    {
        await writer.StartRowAsync(ct);
        await writer.WriteAsync(sample.Timestamp.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
        await writer.WriteAsync(path, NpgsqlDbType.Text, ct);
        RouteValue(writer, sample.Value, ct);
    }
    await writer.CompleteAsync(ct);
}

private static async ValueTask RouteValue(NpgsqlBinaryImporter w, object? value, CancellationToken ct)
{
    switch (value)
    {
        case null:
            await w.WriteNullAsync(ct);  // value_long
            await w.WriteNullAsync(ct);  // value_double
            await w.WriteNullAsync(ct);  // value_json
            break;
        case double d:
            await w.WriteNullAsync(ct);
            await w.WriteAsync(d, NpgsqlDbType.Double, ct);
            await w.WriteNullAsync(ct);
            break;
        case float f:
            await w.WriteNullAsync(ct);
            await w.WriteAsync((double)f, NpgsqlDbType.Double, ct);
            await w.WriteNullAsync(ct);
            break;
        case ulong ul when ul > long.MaxValue:
            // overflow case: route to JSON
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            await w.WriteAsync(JsonSerializer.SerializeToDocument(ul.ToString()).RootElement, NpgsqlDbType.Jsonb, ct);
            break;
        case ulong ul:
            await w.WriteAsync((long)ul, NpgsqlDbType.Bigint, ct);
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            break;
        case long l:
            await w.WriteAsync(l, NpgsqlDbType.Bigint, ct);
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            break;
        // ... int, short, byte, uint, ushort, sbyte, bool similarly to value_long ...
        case decimal m:
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            await w.WriteAsync(JsonSerializer.SerializeToDocument(m).RootElement, NpgsqlDbType.Jsonb, ct);
            break;
        case string s:
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            await w.WriteAsync(JsonSerializer.SerializeToDocument(s).RootElement, NpgsqlDbType.Jsonb, ct);
            break;
        case Enum e:
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            await w.WriteAsync(JsonSerializer.SerializeToDocument(e.ToString()).RootElement, NpgsqlDbType.Jsonb, ct);
            break;
        // Special placeholder (oversize) — already a JsonElement from the in-memory path.
        case JsonElement je:
            await w.WriteNullAsync(ct);
            await w.WriteNullAsync(ct);
            await w.WriteAsync(je, NpgsqlDbType.Jsonb, ct);
            break;
    }
}
```

**Step 3:** Verify integration tests pass. Commit.

### Task 26: BufferTime + FlushInterval dual-knob batching (integration test)

**Step 1:** Test verifies:
- With `BufferTime = 100ms` and `FlushInterval = 500ms`, 5 batches accumulate per flush.
- A property that changes 100× within one `BufferTime` window stores 1 sample (coalesce).

**Step 2:** Implement: the `ChangeQueueProcessor` already coalesces within `BufferTime`. The store's `HandleBatch` appends to a pending list; a separate timer fires every `FlushInterval` and calls `FlushBatchAsync`. Validate `FlushInterval >= BufferTime` at startup (clamp up with a log warning).

**Step 3:** Commit.

### Task 27: Raw query SQL builder per column (integration test)

Tests cover `value_long`-, `value_double`-, and `value_json`-typed properties. For each, write 5 samples, query the raw range, assert ordering, count, and that `number` (vs `json`) is populated correctly.

Implement the SQL builder. Use parameterized commands:

```csharp
private async Task<HistorySeries> RawQueryAsync(HistoryQuery query, Type propertyType, CancellationToken ct)
{
    var column = HistoryColumns.ValueColumnFor(propertyType);
    var (selectNumber, selectJson) = column switch
    {
        ValueColumn.Long   => ("value_long::double precision",  "NULL::jsonb"),
        ValueColumn.Double => ("value_double",                  "NULL::jsonb"),
        ValueColumn.Json   => ("NULL::double precision",        "value_json"),
        _ => throw new InvalidOperationException()
    };
    var sql = $@"SELECT ts, {selectNumber} AS number, {selectJson} AS json
                 FROM property_history
                 WHERE path = $1 AND ts >= $2 AND ts < $3
                 ORDER BY ts ASC
                 LIMIT $4 + 1";
    // ... execute, materialize, set truncated when count == query.MaxPoints + 1.
}
```

Property type is looked up via the registry on entry; the store needs a `PathProvider` or `SubjectRegistry` reference.

Commit.

### Task 28: Bucketed query SQL builder (integration test)

Cover all 6 aggregations on `value_long` and `value_double`. Also verify `Last` on `value_json` returns the JSON literal.

Implement the bucketed SQL with column dispatch, casting bigint aggregates to double precision on output, and switching on aggregation in C# to produce the SQL fragment.

Commit.

### Task 29: ulong COALESCE read variant (integration test)

Test:
- Write a `ulong`-typed property with values both below and above `long.MaxValue`.
- Query bucketed `Average`.
- Result must be the true mean across both columns.

Implement: when `HistoryColumns.IsUlongProperty(propertyType)` is true, emit the COALESCE SQL variant per the design doc (lines around 244-254 in `history-mvp.md`).

Commit.

### Task 30: Unsupported aggregation error (integration test)

Test: `Average` requested on a string-typed property raises `HistoryAggregationNotSupportedException`.

Implement: in the bucketed-query builder, when `ValueColumnFor(propertyType) == Json` and `aggregation not in (Last, Count)`, throw before touching the DB.

Commit.

### Task 31: Retention via `add_retention_policy` (integration test)

Test: with `Retention = 1 second`, insert synthetic old rows, wait for Timescale's retention job, assert rows removed.

Implement: in `EnsureSchemaAsync`, after creating the hypertable, run:

```sql
SELECT add_retention_policy('property_history', INTERVAL '30 days', if_not_exists => true);
```

Reload the policy whenever `Retention` changes (drop + re-add).

Commit.

### Task 32: State metrics (throughput, last-flush, last-error) (integration test)

Tests verify each state property updates as expected (after a successful flush `LastFlushUtc` is recent; after a DB failure `LastError` is populated).

Implement throughput counters using the shared `ThroughputCounter`. Update state in `UpdateMetrics` invoked from `HandleBatch` and `FlushBatchAsync`.

Commit.

### Task 33: Backpressure + reconnect integration tests

Tests:
- Throttle DB → queue overflows → oldest dropped → `DropCount` increments.
- Restart container mid-run → sink reconnects on next flush.

These exercise the `ChangeQueueProcessor` semantics inherited from the platform; verify the store's error handling doesn't crash the host.

Commit.

---

## Phase 5: Timescale Blazor

### Task 34: Create `HomeBlaze.History.TimescaleDb.Blazor`

Mirror `InMemory.Blazor`. Commit.

### Task 35: `TimescaleDbHistoryStoreEditComponent.razor`

Tabs: General (Name, IsEnabled, ConnectionString, Retention) and Advanced (BufferTime, FlushInterval, MaxJsonSize). Status block at the bottom. Pattern from `OpcUaServerEditComponent.razor`. Commit.

---

## Phase 6: MCP tool

### Task 36: Add `get_property_history` tool to `HomeBlaze.AI/Mcp`

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.AI/Mcp/HistoryMcpToolProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/McpBuilderExtensions.cs` (register the new provider)

**Step 1:** Follow the `HomeBlazeMcpToolProvider` pattern. Implement `IMcpToolProvider`. Yield one tool:

```csharp
yield return new McpToolInfo
{
    Name = "get_property_history",
    Description = "Query historical values for a property. Aggregates across all history stores by priority and coverage.",
    InputSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Subject property path" },
            from = new { type = "string", description = "ISO 8601 start timestamp (UTC assumed if no offset)" },
            to = new { type = "string", description = "ISO 8601 end timestamp; defaults to now" },
            bucket = new { type = "string", description = "Bucket interval (e.g., '5m'); omit for raw" },
            aggregation = new { type = "string", description = "last|average|minimum|maximum|sum|count", @enum = new[] { "last", "average", "minimum", "maximum", "sum", "count" } }
        },
        required = new[] { "path", "from" }
    }),
    Handler = HandleAsync
};
```

`HandleAsync` parses parameters, builds a `HistoryQuery`, and calls `_registry.QueryHistoryAsync`.

**Step 2:** Register in `McpBuilderExtensions.cs` next to the existing providers.

**Step 3:** Add unit tests in `HomeBlaze.AI.Tests/Mcp/HistoryMcpToolProviderTests.cs`:
- Parses `from`/`to`/`bucket`/`aggregation` correctly.
- Defaults `to` to now when omitted.
- Case-insensitive aggregation.
- Unknown path returns empty `points` (not an error).
- Bare timestamp without offset parses as UTC.

**Step 4:** Run tests, verify pass.

**Step 5:** Commit.

---

## Phase 7: UI dialog

### Task 37: `PropertyHistoryDialog` component

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/PropertyHistoryDialog.razor`
- Create: `src/HomeBlaze/HomeBlaze.Components/Dialogs/PropertyHistoryDialog.razor.cs`

**Step 1:** Implement the layout per the design doc: range buttons, bucket auto, aggregation selector, `MudTimeSeriesChart`, source/truncated footer.

**Step 2:** Calls `_registry.QueryHistoryAsync` on parameter change. Converts `HistorySeries` to `MudTimeSeriesChart`'s `ChartSeries`.

**Step 3:** Defaults from the design doc: 24h, Auto bucket, Average for numerics; raw + Last for non-numerics.

**Step 4:** Auto-bucket function picks `range / 200` rounded to a sane interval.

**Step 5:** Manual test by running the sample. Commit.

### Task 38: Add "View history" icon to property view

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Components/Editors/PropertyEditor.razor`

**Step 1:** Add a `MudIconButton` next to the property value that renders only when both conditions hold:
- `property.HasHistory()`
- registry contains at least one `IHistoryStore` (resolved once on init)

Clicking opens `PropertyHistoryDialog` with the property's path pre-filled.

**Step 2:** Manual test by running the sample. Commit.

---

## Phase 8: Solution wiring + smoke

### Task 39: Verify everything builds together

```bash
dotnet build src/Namotion.Interceptor.slnx
```
Expected: 0 errors, 0 warnings (warnings are errors per Directory.Build.props).

Fix any issues. Commit.

### Task 40: Run all non-integration tests

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```
Expected: all green.

### Task 41: Run history integration tests

```bash
dotnet test src/HomeBlaze/HomeBlaze.History.TimescaleDb.Tests
```
Expected: all green (requires Docker).

### Task 42: Manual end-to-end smoke

**Step 1:** Run the sample app: `dotnet run --project src/HomeBlaze/HomeBlaze.Host --launch-profile HomeBlaze`. Add an `InMemoryHistoryStore` subject via the UI. Wait a minute. Open a `[State]` property, click the history icon, verify a chart appears with recent data.

**Step 2:** Add a `TimescaleDbHistoryStore` subject pointing at a local TimescaleDB instance (Docker run if needed). Wait, query the history of a property, verify Timescale serves older buckets while in-memory serves the freshest.

**Step 3:** From an MCP client (Claude Desktop with the HomeBlaze MCP server configured), invoke `get_property_history` for a real property path. Verify JSON response.

**Step 4:** No commit needed unless you found bugs.

### Task 43: Update `state.md` to reflect implementation status

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Data/Docs/architecture/state.md`

Move the Time-series history row from Planned to Implemented. Commit.

---

## Notes for the executing engineer

- **The design doc is the source of truth for behavior.** When the plan and design conflict, the design wins (the plan may be coarser).
- **CLAUDE.md rules apply:** descriptive identifiers, no em dashes in docs, integration tests use `[Trait("Category", "Integration")]`.
- **TDD is strict for the abstractions and in-memory store** (Tasks 4-8, 11-13). The Timescale phase is integration-test-driven because the value of testing it without a real DB is low.
- **Path resolution** (turning a `PropertyChangedContext` into a string path) likely needs `PathProviderBase`. Look at how `OpcUaServer` does it and mirror.
- **Source generator for `[InterceptorSubject] partial class`** runs at build; partial properties materialize backing fields automatically. If a property doesn't seem to compile, check `Namotion.Interceptor.Generator/`.
- **`IConfigurable` / `ITitleProvider` / `IIconProvider`** are the HomeBlaze contracts that make a subject editable/displayable in the UI. Implement them on both stores; copy from `OpcUaServer`.
- **For the MCP tool:** `IMcpToolProvider` is the registration extension point. `HomeBlazeMcpToolProvider` shows the pattern (constructor injection of registry/services, `GetTools` enumerator).
- **Commit cadence:** commit after each task. Frequent small commits make rebases and reviews easy.
- **If a step doesn't compile against the actual codebase:** stop, investigate the relevant interface, adjust the code in the plan, and proceed. The plan codifies the design; minor API discrepancies are expected.
