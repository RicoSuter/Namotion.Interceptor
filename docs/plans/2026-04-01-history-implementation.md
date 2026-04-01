# History System Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add time-series history recording and querying to HomeBlaze with an allocation-optimized write path, SQLite storage, periodic graph snapshots, and MCP tool integration.

**Architecture:** `HistoryService` (BackgroundService) subscribes to property changes via its own `ChangeQueueProcessor`, buffers changes, and fans out to sink subjects discovered via lifecycle events. `SqliteHistorySink` stores data in partitioned SQLite databases (one per day/week/month). MCP tools in `HomeBlaze.AI` query via `IHistoryReader` from `HomeBlaze.History.Abstractions`.

**Tech Stack:** .NET 10, C# 13, Microsoft.Data.Sqlite, System.Text.Json, xUnit

**Design doc:** `docs/plans/2026-04-01-history-design.md`

---

### Task 1: Create HomeBlaze.History.Abstractions Project

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

**Step 1: Create the .csproj file**

```xml
<!-- src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

**Step 2: Create enums**

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/AggregationType.cs
namespace HomeBlaze.History;

public enum AggregationType
{
    Avg, Min, Max, Sum, Count, First, Last
}
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryValueType.cs
namespace HomeBlaze.History;

public enum HistoryValueType : byte
{
    Null, Double, Boolean, String, Complex
}
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryPartitionInterval.cs
namespace HomeBlaze.History;

public enum HistoryPartitionInterval
{
    Daily, Weekly, Monthly
}
```

**Step 3: Create data model records**

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryRecord.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record HistoryRecord(
    string SubjectPath,
    string PropertyName,
    DateTimeOffset Timestamp,
    JsonElement Value);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistoryQuery.cs
namespace HomeBlaze.History;

public record HistoryQuery(
    string SubjectPath,
    IReadOnlyList<string> PropertyNames,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? BucketSize = null,
    AggregationType? Aggregation = null,
    bool FollowMoves = true);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/AggregatedRecord.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record AggregatedRecord(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    double? Avg, double? Min, double? Max,
    double? Sum, long Count,
    JsonElement? First, JsonElement? Last);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/MoveRecord.cs
namespace HomeBlaze.History;

public record MoveRecord(
    DateTimeOffset Timestamp,
    string FromPath,
    string ToPath);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistorySnapshot.cs
namespace HomeBlaze.History;

public record HistorySnapshot(
    DateTimeOffset Timestamp,
    string BasePath,
    IReadOnlyDictionary<string, HistorySubjectSnapshot> Subjects);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/HistorySubjectSnapshot.cs
using System.Text.Json;

namespace HomeBlaze.History;

public record HistorySubjectSnapshot(
    IReadOnlyDictionary<string, JsonElement> Properties);
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/ResolvedHistoryRecord.cs
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

**Step 4: Create interfaces**

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/IHistoryWriter.cs
namespace HomeBlaze.History;

public interface IHistoryWriter
{
    Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);
    Task WriteSnapshotAsync(HistorySnapshot snapshot);
    Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);
}
```

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/IHistoryReader.cs
namespace HomeBlaze.History;

public interface IHistoryReader
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

```csharp
// src/HomeBlaze/HomeBlaze.History.Abstractions/IHistorySink.cs
namespace HomeBlaze.History;

public interface IHistorySink : IHistoryWriter, IHistoryReader { }
```

**Step 5: Add project to solution**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj --solution-folder /HomeBlaze/Abstractions`

**Step 6: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.History.Abstractions/HomeBlaze.History.Abstractions.csproj`
Expected: Build succeeded

**Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Abstractions/ src/Namotion.Interceptor.slnx
git commit -m "feat: add HomeBlaze.History.Abstractions with interfaces and data model"
```

---

### Task 2: Create HomeBlaze.History Project with HistorySinkBase

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History/HistorySinkBase.cs`
- Create: `src/HomeBlaze/HomeBlaze.History/HistoryServiceExtensions.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create the .csproj file**

Reference pattern from `src/HomeBlaze/HomeBlaze.Services/HomeBlaze.Services.csproj`. The HistorySinkBase is an `[InterceptorSubject]`, so it needs the source generator reference.

```xml
<!-- src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj -->
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

**Step 2: Create HistorySinkBase**

```csharp
// src/HomeBlaze/HomeBlaze.History/HistorySinkBase.cs
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History;

[InterceptorSubject]
public abstract partial class HistorySinkBase : IHistorySink
{
    [Configuration]
    public partial TimeSpan SnapshotInterval { get; set; }

    [Configuration]
    public partial int RetentionDays { get; set; }

    [Configuration]
    public partial int Priority { get; set; }

    [State]
    public partial DateTimeOffset? LastSnapshotTime { get; set; }

    [State]
    public partial long RecordsWritten { get; set; }

    [State]
    public partial string? Status { get; set; }

    int IHistoryReader.Priority => Priority;

    public abstract DateTimeOffset? OldestRecord { get; }

    public abstract bool SupportsNativeAggregation { get; }

    public abstract Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records);

    public abstract Task WriteSnapshotAsync(HistorySnapshot snapshot);

    public abstract Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves);

    public abstract Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query);

    public abstract Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time);

    public abstract IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to,
        TimeSpan interval);
}
```

**Step 3: Create DI extensions**

```csharp
// src/HomeBlaze/HomeBlaze.History/HistoryServiceExtensions.cs
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.History;

public static class HistoryServiceExtensions
{
    public static IServiceCollection AddHistoryService(
        this IServiceCollection services,
        TimeSpan? bufferTime = null)
    {
        services.AddSingleton(new HistoryServiceOptions
        {
            BufferTime = bufferTime ?? TimeSpan.FromSeconds(1)
        });
        services.AddSingleton<HistoryService>();
        services.AddHostedService(sp => sp.GetRequiredService<HistoryService>());
        return services;
    }
}

public class HistoryServiceOptions
{
    public TimeSpan BufferTime { get; init; } = TimeSpan.FromSeconds(1);
}
```

**Step 4: Create HistoryService skeleton**

```csharp
// src/HomeBlaze/HomeBlaze.History/HistoryService.cs
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History;

public class HistoryService : BackgroundService
{
    private readonly IInterceptorSubjectContext _context;
    private readonly SubjectPathResolver _pathResolver;
    private readonly HistoryServiceOptions _options;
    private readonly ILogger<HistoryService> _logger;
    private readonly HashSet<IHistorySink> _sinks = new();
    private readonly Lock _sinksLock = new();

    public HistoryService(
        IInterceptorSubjectContext context,
        SubjectPathResolver pathResolver,
        HistoryServiceOptions options,
        ILogger<HistoryService> logger)
    {
        _context = context;
        _pathResolver = pathResolver;
        _options = options;
        _logger = logger;
    }

    internal IReadOnlyCollection<IHistorySink> Sinks
    {
        get
        {
            lock (_sinksLock)
            {
                return _sinks.ToArray();
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Will be implemented in Task 6
        return Task.CompletedTask;
    }
}
```

**Step 5: Add project to solution and build**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj --solution-folder /HomeBlaze`

Run: `dotnet build src/HomeBlaze/HomeBlaze.History/HomeBlaze.History.csproj`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History/ src/Namotion.Interceptor.slnx
git commit -m "feat: add HomeBlaze.History with HistorySinkBase and DI extensions"
```

---

### Task 3: Create HomeBlaze.History.Tests and Write Sink Discovery Tests

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/HomeBlaze.History.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/TestHistorySink.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create test .csproj**

Follow pattern from `src/HomeBlaze/HomeBlaze.Services.Tests/HomeBlaze.Services.Tests.csproj`.

```xml
<!-- src/HomeBlaze/HomeBlaze.History.Tests/HomeBlaze.History.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History\HomeBlaze.History.csproj" />
    <ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

**Step 2: Create TestHistorySink**

A concrete `[InterceptorSubject]` implementing `HistorySinkBase` for testing.

```csharp
// src/HomeBlaze/HomeBlaze.History.Tests/TestHistorySink.cs
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Tests;

[InterceptorSubject]
public partial class TestHistorySink : HistorySinkBase
{
    public List<ResolvedHistoryRecord[]> WrittenBatches { get; } = new();
    public List<HistorySnapshot> WrittenSnapshots { get; } = new();

    public override DateTimeOffset? OldestRecord => null;
    public override bool SupportsNativeAggregation => false;

    public override Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records)
    {
        WrittenBatches.Add(records.ToArray());
        RecordsWritten += records.Length;
        Status = "Connected";
        return Task.CompletedTask;
    }

    public override Task WriteSnapshotAsync(HistorySnapshot snapshot)
    {
        WrittenSnapshots.Add(snapshot);
        LastSnapshotTime = snapshot.Timestamp;
        return Task.CompletedTask;
    }

    public override Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves)
        => Task.CompletedTask;

    public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
        => Task.FromResult<IReadOnlyList<HistoryRecord>>(Array.Empty<HistoryRecord>());

    public override Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time)
        => throw new NotImplementedException();

    public override async IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to, TimeSpan interval)
    {
        await Task.CompletedTask;
        yield break;
    }
}
```

**Step 3: Write sink discovery tests**

```csharp
// src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.History.Tests;

public class HistoryServiceTests
{
    private static (HistoryService service, IInterceptorSubjectContext context) CreateService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle();

        var pathResolver = new SubjectPathResolver(/* needs RootManager - will need adjustment */);
        var options = new HistoryServiceOptions { BufferTime = TimeSpan.FromSeconds(1) };
        var logger = services.BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<HistoryService>();

        var service = new HistoryService(context, pathResolver, options, logger);
        return (service, context);
    }

    [Fact]
    public void WhenSinkAttachedToGraph_ThenServiceDiscoversSink()
    {
        // Arrange
        var (service, context) = CreateService();
        var sink = new TestHistorySink(context);

        // Act — attach sink to the graph (simulate FluentStorage loading a JSON config)
        // This requires a parent subject with a property that holds the sink.
        // The lifecycle interceptor fires SubjectAttached when the sink is assigned.

        // Assert
        Assert.Contains(sink, service.Sinks);
    }

    [Fact]
    public void WhenSinkDetachedFromGraph_ThenServiceRemovesSink()
    {
        // Arrange
        var (service, context) = CreateService();
        var sink = new TestHistorySink(context);

        // Act — attach then detach

        // Assert
        Assert.DoesNotContain(sink, service.Sinks);
    }
}
```

**Note to implementer:** The exact test setup for sink discovery depends on how the `HistoryService` subscribes to lifecycle events. The tests above are skeleton — adjust the setup to match the `LifecycleInterceptor.SubjectAttached` subscription pattern. You may need a parent `[InterceptorSubject]` with a collection property to attach/detach the sink from. Look at `src/Namotion.Interceptor.Tracking.Tests/Lifecycle/` for examples of lifecycle test patterns.

**Step 4: Add to solution and verify tests fail**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Tests/HomeBlaze.History.Tests.csproj --solution-folder /HomeBlaze/Tests`

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore`
Expected: FAIL (sink discovery not implemented yet)

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Tests/ src/Namotion.Interceptor.slnx
git commit -m "test: add history service sink discovery tests (red)"
```

---

### Task 4: Implement HistoryService Sink Discovery

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`

**Step 1: Subscribe to lifecycle events**

Update `HistoryService` to subscribe to `LifecycleInterceptor.SubjectAttached` and `SubjectDetaching`:

```csharp
// In HistoryService constructor, add:
var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
if (lifecycleInterceptor is not null)
{
    lifecycleInterceptor.SubjectAttached += OnSubjectAttached;
    lifecycleInterceptor.SubjectDetaching += OnSubjectDetaching;
}

private void OnSubjectAttached(SubjectLifecycleChange change)
{
    if (!change.IsContextAttach)
        return;

    if (change.Subject is IHistorySink sink)
    {
        lock (_sinksLock)
        {
            _sinks.Add(sink);
        }
        _logger.LogInformation("History sink discovered: {Type}", sink.GetType().Name);
    }
}

private void OnSubjectDetaching(SubjectLifecycleChange change)
{
    if (!change.IsContextDetach)
        return;

    if (change.Subject is IHistorySink sink)
    {
        lock (_sinksLock)
        {
            _sinks.Remove(sink);
        }
        _logger.LogInformation("History sink removed: {Type}", sink.GetType().Name);
    }
}
```

**Step 2: Look up `TryGetLifecycleInterceptor`**

This extension method is in `Namotion.Interceptor.Tracking.Lifecycle`. Check:
`src/Namotion.Interceptor.Tracking/Lifecycle/LifecycleInterceptorExtensions.cs`
for the exact method name and import it.

**Step 3: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore`
Expected: PASS

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History/HistoryService.cs
git commit -m "feat: implement lifecycle-based sink discovery in HistoryService"
```

---

### Task 5: Write Change Recording Tests

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Tests/TestSubject.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`

**Step 1: Create a test subject with [State] and [Configuration] properties**

```csharp
// src/HomeBlaze/HomeBlaze.History.Tests/TestSubject.cs
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Tests;

[InterceptorSubject]
public partial class TestSubject
{
    [State]
    public partial double Temperature { get; set; }

    [State]
    public partial string? Status { get; set; }

    [Configuration]
    public partial string? Name { get; set; }
}
```

**Step 2: Write test — state property change flows to sink**

```csharp
[Fact]
public async Task WhenStatePropertyChanges_ThenSinkReceivesBatch()
{
    // Arrange
    var (service, context, sink, rootSubject) = CreateServiceWithSink();
    var testSubject = new TestSubject(context);
    rootSubject.Child = testSubject; // attach to graph

    using var cts = new CancellationTokenSource();
    var serviceTask = service.StartAsync(cts.Token);

    // Act
    testSubject.Temperature = 42.5;

    // Wait for flush (buffer time + margin)
    await Task.Delay(TimeSpan.FromSeconds(2));

    // Assert
    Assert.NotEmpty(sink.WrittenBatches);
    var allRecords = sink.WrittenBatches.SelectMany(b => b).ToList();
    Assert.Contains(allRecords, r =>
        r.PropertyName == "Temperature" &&
        r.NumericValue == 42.5 &&
        r.ValueType == HistoryValueType.Double);

    cts.Cancel();
    await serviceTask;
}
```

**Step 3: Write test — configuration property is NOT recorded**

```csharp
[Fact]
public async Task WhenConfigurationPropertyChanges_ThenSinkDoesNotReceive()
{
    // Arrange
    var (service, context, sink, rootSubject) = CreateServiceWithSink();
    var testSubject = new TestSubject(context);
    rootSubject.Child = testSubject;

    using var cts = new CancellationTokenSource();
    var serviceTask = service.StartAsync(cts.Token);

    // Act
    testSubject.Name = "TestMotor";
    await Task.Delay(TimeSpan.FromSeconds(2));

    // Assert
    var allRecords = sink.WrittenBatches.SelectMany(b => b).ToList();
    Assert.DoesNotContain(allRecords, r => r.PropertyName == "Name");

    cts.Cancel();
    await serviceTask;
}
```

**Step 4: Write test — failing sink doesn't crash service**

```csharp
[Fact]
public async Task WhenSinkThrows_ThenServiceContinuesRunning()
{
    // Arrange — use a sink that throws on WriteBatchAsync
    // Create a FailingSink subclass or configure TestHistorySink to throw

    // Act — change property, wait for flush

    // Assert — service is still running, sink.Status contains error
}
```

**Note to implementer:** Adjust the `CreateServiceWithSink()` helper to set up a full context with a root subject, attach the sink via a collection property, and wire up the HistoryService. Look at existing test patterns in `HomeBlaze.Services.Tests/` for how to create a minimal HomeBlaze context in tests.

**Step 5: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore`
Expected: FAIL (ExecuteAsync not implemented)

**Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Tests/
git commit -m "test: add change recording tests for HistoryService (red)"
```

---

### Task 6: Implement HistoryService Change Recording

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`

**Step 1: Implement ExecuteAsync with CQP**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    var pathProvider = new StateAttributePathProvider();

    bool PropertyFilter(PropertyReference propertyReference)
    {
        var registered = propertyReference.TryGetRegisteredProperty();
        return registered is null || pathProvider.IsPropertyIncluded(registered);
    }

    using var changeQueueProcessor = new ChangeQueueProcessor(
        source: this,
        _context,
        propertyFilter: PropertyFilter,
        writeHandler: HandleChangesAsync,
        _options.BufferTime,
        _logger);

    await changeQueueProcessor.ProcessAsync(stoppingToken);
}
```

**Step 2: Implement the write handler**

```csharp
private async ValueTask HandleChangesAsync(
    ReadOnlyMemory<SubjectPropertyChange> changes,
    CancellationToken cancellationToken)
{
    if (changes.Length == 0)
        return;

    IHistorySink[] currentSinks;
    lock (_sinksLock)
    {
        if (_sinks.Count == 0)
            return;
        currentSinks = _sinks.ToArray();
    }

    // Convert changes to resolved records
    var records = new ResolvedHistoryRecord[changes.Length];
    var count = 0;

    for (var i = 0; i < changes.Length; i++)
    {
        var change = changes.Span[i];
        var subject = change.Property.Subject;
        var path = _pathResolver.GetPath(subject, PathStyle.Canonical);

        if (path is null)
            continue;

        var newValue = change.GetNewValue<object?>();
        var record = CreateRecord(path, change.Property.Name, change.ChangedTimestamp, newValue);
        records[count++] = record;
    }

    if (count == 0)
        return;

    var memory = new ReadOnlyMemory<ResolvedHistoryRecord>(records, 0, count);

    // Fan out to all sinks in parallel
    await Task.WhenAll(currentSinks.Select(async sink =>
    {
        try
        {
            await sink.WriteBatchAsync(memory);
        }
        catch (Exception ex)
        {
            if (sink is HistorySinkBase sinkBase)
                sinkBase.Status = $"Error: {ex.Message}";

            _logger.LogWarning(ex, "History sink {Type} flush failed", sink.GetType().Name);
        }
    }));
}

private static ResolvedHistoryRecord CreateRecord(
    string subjectPath, string propertyName,
    DateTimeOffset timestamp, object? value)
{
    return value switch
    {
        null => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Null, 0, default),

        double d => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Double, d, default),

        float f => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Double, f, default),

        int n => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Double, n, default),

        long n => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Double, n, default),

        decimal d => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Double, (double)d, default),

        bool b => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Boolean, b ? 1.0 : 0.0, default),

        string s => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.String, 0,
            System.Text.Encoding.UTF8.GetBytes(s)),

        _ => new ResolvedHistoryRecord(
            subjectPath, propertyName, timestamp.Ticks,
            HistoryValueType.Complex, 0,
            JsonSerializer.SerializeToUtf8Bytes(value))
    };
}
```

**Step 3: Add required imports**

```csharp
using System.Text.Json;
using HomeBlaze.AI.Mcp; // for StateAttributePathProvider
using HomeBlaze.Services; // for SubjectPathResolver, PathStyle
using Namotion.Interceptor.Connectors; // for ChangeQueueProcessor
using Namotion.Interceptor.Tracking.Change; // for SubjectPropertyChange
```

**Step 4: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History/HistoryService.cs
git commit -m "feat: implement change recording with CQP and sink fan-out"
```

---

### Task 7: Create HomeBlaze.History.Sqlite Project

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqlitePartitionManager.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create .csproj**

```xml
<!-- src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="HomeBlaze.History.Sqlite.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History\HomeBlaze.History.csproj" />
    <ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
    <ProjectReference Include="..\HomeBlaze.Abstractions\HomeBlaze.Abstractions.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 2: Create SqlitePartitionManager**

Handles partition-to-filename mapping and database initialization.

```csharp
// src/HomeBlaze/HomeBlaze.History.Sqlite/SqlitePartitionManager.cs
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite;

internal class SqlitePartitionManager
{
    private readonly string _basePath;
    private readonly HistoryPartitionInterval _interval;

    public SqlitePartitionManager(string basePath, HistoryPartitionInterval interval)
    {
        _basePath = basePath;
        _interval = interval;
    }

    public string GetPartitionKey(DateTimeOffset timestamp)
    {
        return _interval switch
        {
            HistoryPartitionInterval.Daily =>
                timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HistoryPartitionInterval.Weekly =>
                $"{timestamp.UtcDateTime.Year}-W{ISOWeek.GetWeekOfYear(timestamp.UtcDateTime):D2}",
            HistoryPartitionInterval.Monthly =>
                timestamp.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public string GetDatabasePath(string partitionKey)
    {
        return Path.Combine(_basePath, $"history-{partitionKey}.db");
    }

    public SqliteConnection CreateConnection(string partitionKey)
    {
        var dbPath = GetDatabasePath(partitionKey);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            """;
        pragmaCmd.ExecuteNonQuery();

        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS history (
                timestamp INTEGER NOT NULL,
                subject_path TEXT NOT NULL,
                property_name TEXT NOT NULL,
                value_type INTEGER NOT NULL,
                numeric_value REAL,
                raw_value TEXT,
                PRIMARY KEY (subject_path, property_name, timestamp)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_history_time
                ON history (subject_path, property_name, timestamp);

            CREATE TABLE IF NOT EXISTS snapshots (
                timestamp INTEGER NOT NULL PRIMARY KEY,
                base_path TEXT NOT NULL,
                data BLOB NOT NULL
            );
            """;
        schemaCmd.ExecuteNonQuery();

        return connection;
    }

    public IEnumerable<string> GetPartitionKeysInRange(DateTimeOffset from, DateTimeOffset to)
    {
        var current = from;
        while (current <= to)
        {
            yield return GetPartitionKey(current);

            current = _interval switch
            {
                HistoryPartitionInterval.Daily => current.AddDays(1),
                HistoryPartitionInterval.Weekly => current.AddDays(7),
                HistoryPartitionInterval.Monthly => current.AddMonths(1),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}
```

**Step 3: Create SqliteHistorySink skeleton**

```csharp
// src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Sqlite;

[Category("History")]
[Description("SQLite-based history sink with partitioned databases")]
[InterceptorSubject]
public partial class SqliteHistorySink : HistorySinkBase, IConfigurable
{
    private SqlitePartitionManager? _partitionManager;

    [Configuration]
    public partial string DatabasePath { get; set; }

    [Configuration]
    public partial HistoryPartitionInterval PartitionInterval { get; set; }

    public override DateTimeOffset? OldestRecord => null; // TODO: implement

    public override bool SupportsNativeAggregation => true;

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        _partitionManager = new SqlitePartitionManager(
            DatabasePath ?? "./history",
            PartitionInterval);
        Status = "Connected";
        return Task.CompletedTask;
    }

    public override Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records)
    {
        // Implemented in Task 9
        throw new NotImplementedException();
    }

    public override Task WriteSnapshotAsync(HistorySnapshot snapshot)
    {
        // Implemented in Task 12
        throw new NotImplementedException();
    }

    public override Task WriteMovesAsync(ReadOnlyMemory<MoveRecord> moves)
        => Task.CompletedTask; // Move tracking is a planned follow-up

    public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
    {
        // Implemented in Task 11
        throw new NotImplementedException();
    }

    public override Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time)
    {
        // Implemented in Task 12
        throw new NotImplementedException();
    }

    public override async IAsyncEnumerable<HistorySnapshot> GetSnapshotsAsync(
        string path, DateTimeOffset from, DateTimeOffset to, TimeSpan interval)
    {
        // Implemented in Task 13
        await Task.CompletedTask;
        yield break;
    }
}
```

**Step 4: Add to solution and build**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj --solution-folder /HomeBlaze`

Run: `dotnet build src/HomeBlaze/HomeBlaze.History.Sqlite/HomeBlaze.History.Sqlite.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/ src/Namotion.Interceptor.slnx
git commit -m "feat: add HomeBlaze.History.Sqlite project with partition manager and schema"
```

---

### Task 8: Create SQLite Tests and Write Batch Write Tests

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/HomeBlaze.History.Sqlite.Tests.csproj`
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkWriteTests.cs`
- Modify: `src/Namotion.Interceptor.slnx`

**Step 1: Create test .csproj**

```xml
<!-- src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/HomeBlaze.History.Sqlite.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\HomeBlaze.History.Sqlite\HomeBlaze.History.Sqlite.csproj" />
    <ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor\Namotion.Interceptor.csproj" />
    <ProjectReference Include="..\..\Namotion.Interceptor.Generator\Namotion.Interceptor.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

**Step 2: Write batch write tests**

```csharp
// src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkWriteTests.cs
using Microsoft.Data.Sqlite;
using Namotion.Interceptor;

namespace HomeBlaze.History.Sqlite.Tests;

public class SqliteHistorySinkWriteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteHistorySink _sink;

    public SqliteHistorySinkWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"history-test-{Guid.NewGuid():N}");
        var context = InterceptorSubjectContext.Create();
        _sink = new SqliteHistorySink(context)
        {
            DatabasePath = _tempDir,
            PartitionInterval = HistoryPartitionInterval.Daily
        };
        _sink.ApplyConfigurationAsync(CancellationToken.None).Wait();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WhenNumericRecordWritten_ThenStoredWithNumericValue()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", timestamp.Ticks,
                HistoryValueType.Double, 42.5, default)
        };

        // Act
        await _sink.WriteBatchAsync(records);

        // Assert
        var partitionManager = new SqlitePartitionManager(_tempDir, HistoryPartitionInterval.Daily);
        var dbPath = partitionManager.GetDatabasePath("2026-04-01");
        Assert.True(File.Exists(dbPath));

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT numeric_value, value_type FROM history WHERE subject_path = '/Demo/Motor' AND property_name = 'Temperature'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42.5, reader.GetDouble(0));
        Assert.Equal((int)HistoryValueType.Double, reader.GetInt32(1));
    }

    [Fact]
    public async Task WhenStringRecordWritten_ThenStoredInRawValue()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var rawValue = System.Text.Encoding.UTF8.GetBytes("\"Running\"");
        var records = new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Status", timestamp.Ticks,
                HistoryValueType.String, 0, rawValue)
        };

        // Act
        await _sink.WriteBatchAsync(records);

        // Assert — verify raw_value contains the string
    }

    [Fact]
    public async Task WhenMultipleRecordsWritten_ThenAllStored()
    {
        // Arrange
        var timestamp = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", timestamp.Ticks,
                HistoryValueType.Double, 42.5, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Speed", timestamp.AddSeconds(1).Ticks,
                HistoryValueType.Double, 100.0, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", timestamp.AddSeconds(2).Ticks,
                HistoryValueType.Double, 43.1, default),
        };

        // Act
        await _sink.WriteBatchAsync(records);

        // Assert
        // Verify all 3 records are stored
    }

    [Fact]
    public async Task WhenRecordsSpanMultipleDays_ThenStoredInSeparatePartitions()
    {
        // Arrange
        var day1 = new DateTimeOffset(2026, 4, 1, 23, 59, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 4, 2, 0, 1, 0, TimeSpan.Zero);
        var records = new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", day1.Ticks,
                HistoryValueType.Double, 42.5, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", day2.Ticks,
                HistoryValueType.Double, 43.0, default),
        };

        // Act
        await _sink.WriteBatchAsync(records);

        // Assert
        var partitionManager = new SqlitePartitionManager(_tempDir, HistoryPartitionInterval.Daily);
        Assert.True(File.Exists(partitionManager.GetDatabasePath("2026-04-01")));
        Assert.True(File.Exists(partitionManager.GetDatabasePath("2026-04-02")));
    }
}
```

**Step 3: Add to solution and verify tests fail**

Run: `dotnet sln src/Namotion.Interceptor.slnx add src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/HomeBlaze.History.Sqlite.Tests.csproj --solution-folder /HomeBlaze/Tests`

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: FAIL (WriteBatchAsync not implemented)

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ src/Namotion.Interceptor.slnx
git commit -m "test: add SQLite sink write tests (red)"
```

---

### Task 9: Implement SQLite WriteBatchAsync

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`

**Step 1: Implement WriteBatchAsync**

```csharp
public override Task WriteBatchAsync(ReadOnlyMemory<ResolvedHistoryRecord> records)
{
    if (_partitionManager is null)
        throw new InvalidOperationException("Sink not configured");

    var span = records.Span;

    // Group records by partition key
    var groups = new Dictionary<string, List<int>>(); // partitionKey → record indices
    for (var i = 0; i < span.Length; i++)
    {
        var timestamp = new DateTimeOffset(span[i].TimestampTicks, TimeSpan.Zero);
        var key = _partitionManager.GetPartitionKey(timestamp);
        if (!groups.TryGetValue(key, out var list))
        {
            list = new List<int>();
            groups[key] = list;
        }
        list.Add(i);
    }

    // Write each partition in a single transaction
    foreach (var (partitionKey, indices) in groups)
    {
        using var connection = _partitionManager.CreateConnection(partitionKey);
        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO history
                (timestamp, subject_path, property_name, value_type, numeric_value, raw_value)
            VALUES
                (@timestamp, @subjectPath, @propertyName, @valueType, @numericValue, @rawValue)
            """;

        var pTimestamp = cmd.Parameters.Add("@timestamp", SqliteType.Integer);
        var pSubjectPath = cmd.Parameters.Add("@subjectPath", SqliteType.Text);
        var pPropertyName = cmd.Parameters.Add("@propertyName", SqliteType.Text);
        var pValueType = cmd.Parameters.Add("@valueType", SqliteType.Integer);
        var pNumericValue = cmd.Parameters.Add("@numericValue", SqliteType.Real);
        var pRawValue = cmd.Parameters.Add("@rawValue", SqliteType.Text);

        foreach (var index in indices)
        {
            ref readonly var record = ref span[index];
            pTimestamp.Value = record.TimestampTicks;
            pSubjectPath.Value = record.SubjectPath;
            pPropertyName.Value = record.PropertyName;
            pValueType.Value = (int)record.ValueType;
            pNumericValue.Value = record.ValueType is HistoryValueType.Double or HistoryValueType.Boolean
                ? record.NumericValue : DBNull.Value;
            pRawValue.Value = record.RawValue.Length > 0
                ? System.Text.Encoding.UTF8.GetString(record.RawValue.Span)
                : DBNull.Value;

            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    RecordsWritten += records.Length;
    return Task.CompletedTask;
}
```

**Step 2: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: PASS

**Step 3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs
git commit -m "feat: implement SQLite WriteBatchAsync with partition grouping"
```

---

### Task 10: Write and Implement SQLite QueryAsync (Raw Values)

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkReadTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`

**Step 1: Write raw query tests**

```csharp
// src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkReadTests.cs
using Namotion.Interceptor;

namespace HomeBlaze.History.Sqlite.Tests;

public class SqliteHistorySinkReadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteHistorySink _sink;

    public SqliteHistorySinkReadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"history-test-{Guid.NewGuid():N}");
        var context = InterceptorSubjectContext.Create();
        _sink = new SqliteHistorySink(context)
        {
            DatabasePath = _tempDir,
            PartitionInterval = HistoryPartitionInterval.Daily
        };
        _sink.ApplyConfigurationAsync(CancellationToken.None).Wait();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WhenQueryingSingleProperty_ThenReturnsRecordsInRange()
    {
        // Arrange
        var baseTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        await WriteSampleRecords(baseTime);

        // Act
        var query = new HistoryQuery(
            "/Demo/Motor", new[] { "Temperature" },
            baseTime, baseTime.AddMinutes(5));
        var results = await _sink.QueryAsync(query);

        // Assert
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("Temperature", r.PropertyName));
        Assert.All(results, r => Assert.InRange(r.Timestamp, baseTime, baseTime.AddMinutes(5)));
    }

    [Fact]
    public async Task WhenQueryingMultipleProperties_ThenReturnsBoth()
    {
        // Arrange
        var baseTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        await WriteSampleRecords(baseTime);

        // Act
        var query = new HistoryQuery(
            "/Demo/Motor", new[] { "Temperature", "Speed" },
            baseTime, baseTime.AddMinutes(5));
        var results = await _sink.QueryAsync(query);

        // Assert
        Assert.Contains(results, r => r.PropertyName == "Temperature");
        Assert.Contains(results, r => r.PropertyName == "Speed");
    }

    [Fact]
    public async Task WhenQueryingOutsideRange_ThenReturnsEmpty()
    {
        // Arrange
        var baseTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        await WriteSampleRecords(baseTime);

        // Act
        var query = new HistoryQuery(
            "/Demo/Motor", new[] { "Temperature" },
            baseTime.AddHours(10), baseTime.AddHours(11));
        var results = await _sink.QueryAsync(query);

        // Assert
        Assert.Empty(results);
    }

    private async Task WriteSampleRecords(DateTimeOffset baseTime)
    {
        var records = new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", baseTime.Ticks,
                HistoryValueType.Double, 42.5, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", baseTime.AddMinutes(1).Ticks,
                HistoryValueType.Double, 43.0, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Speed", baseTime.AddMinutes(2).Ticks,
                HistoryValueType.Double, 100.0, default),
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", baseTime.AddMinutes(3).Ticks,
                HistoryValueType.Double, 44.5, default),
        };
        await _sink.WriteBatchAsync(records);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --filter "FullyQualifiedName~ReadTests" --no-restore`
Expected: FAIL

**Step 3: Implement QueryAsync for raw values**

```csharp
public override Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query)
{
    if (_partitionManager is null)
        throw new InvalidOperationException("Sink not configured");

    if (query.BucketSize is not null && query.Aggregation is not null)
        return QueryAggregatedAsync(query);

    var results = new List<HistoryRecord>();
    var partitionKeys = _partitionManager.GetPartitionKeysInRange(query.From, query.To);
    var propertyFilter = string.Join(",", query.PropertyNames.Select((_, i) => $"@p{i}"));

    foreach (var key in partitionKeys)
    {
        var dbPath = _partitionManager.GetDatabasePath(key);
        if (!File.Exists(dbPath))
            continue;

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT timestamp, subject_path, property_name, value_type, numeric_value, raw_value
            FROM history
            WHERE subject_path = @subjectPath
              AND property_name IN ({propertyFilter})
              AND timestamp BETWEEN @from AND @to
            ORDER BY timestamp
            """;

        cmd.Parameters.AddWithValue("@subjectPath", query.SubjectPath);
        for (var i = 0; i < query.PropertyNames.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", query.PropertyNames[i]);
        cmd.Parameters.AddWithValue("@from", query.From.Ticks);
        cmd.Parameters.AddWithValue("@to", query.To.Ticks);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var timestamp = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
            var subjectPath = reader.GetString(1);
            var propertyName = reader.GetString(2);
            var valueType = (HistoryValueType)reader.GetInt32(3);
            var value = ReadJsonValue(reader, valueType);

            results.Add(new HistoryRecord(subjectPath, propertyName, timestamp, value));
        }
    }

    return Task.FromResult<IReadOnlyList<HistoryRecord>>(results);
}

private static JsonElement ReadJsonValue(SqliteDataReader reader, HistoryValueType valueType)
{
    return valueType switch
    {
        HistoryValueType.Null => default,
        HistoryValueType.Double or HistoryValueType.Boolean =>
            JsonSerializer.SerializeToElement(reader.GetDouble(4)),
        HistoryValueType.String or HistoryValueType.Complex =>
            JsonDocument.Parse(reader.GetString(5)).RootElement,
        _ => default
    };
}
```

**Step 4: Add using**

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
```

**Step 5: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: PASS

**Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/ src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/
git commit -m "feat: implement SQLite QueryAsync for raw property values"
```

---

### Task 11: Write and Implement SQLite Aggregation

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkReadTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`

**Step 1: Write aggregation test**

```csharp
[Fact]
public async Task WhenQueryingWithAggregation_ThenReturnsBucketedResults()
{
    // Arrange
    var baseTime = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
    var records = Enumerable.Range(0, 10)
        .Select(i => new ResolvedHistoryRecord(
            "/Demo/Motor", "Temperature",
            baseTime.AddMinutes(i).Ticks,
            HistoryValueType.Double, 40.0 + i, default))
        .ToArray();
    await _sink.WriteBatchAsync(records);

    // Act
    var query = new HistoryQuery(
        "/Demo/Motor", new[] { "Temperature" },
        baseTime, baseTime.AddMinutes(10),
        BucketSize: TimeSpan.FromMinutes(5),
        Aggregation: AggregationType.Avg);
    var results = await _sink.QueryAsync(query);

    // Assert — expect 2 buckets (0-5min avg, 5-10min avg)
    Assert.Equal(2, results.Count);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --filter "Aggregation" --no-restore`
Expected: FAIL

**Step 3: Implement QueryAggregatedAsync**

```csharp
private Task<IReadOnlyList<HistoryRecord>> QueryAggregatedAsync(HistoryQuery query)
{
    if (_partitionManager is null)
        throw new InvalidOperationException("Sink not configured");

    var results = new List<HistoryRecord>();
    var bucketTicks = query.BucketSize!.Value.Ticks;
    var partitionKeys = _partitionManager.GetPartitionKeysInRange(query.From, query.To);

    // Aggregate across all partitions in one pass using in-memory collection
    var allRows = new List<(long timestamp, string propertyName, double value)>();

    foreach (var key in partitionKeys)
    {
        var dbPath = _partitionManager.GetDatabasePath(key);
        if (!File.Exists(dbPath))
            continue;

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var propertyFilter = string.Join(",", query.PropertyNames.Select((_, i) => $"@p{i}"));
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT timestamp, property_name, numeric_value
            FROM history
            WHERE subject_path = @subjectPath
              AND property_name IN ({propertyFilter})
              AND timestamp BETWEEN @from AND @to
              AND value_type IN ({(int)HistoryValueType.Double}, {(int)HistoryValueType.Boolean})
            ORDER BY timestamp
            """;

        cmd.Parameters.AddWithValue("@subjectPath", query.SubjectPath);
        for (var i = 0; i < query.PropertyNames.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", query.PropertyNames[i]);
        cmd.Parameters.AddWithValue("@from", query.From.Ticks);
        cmd.Parameters.AddWithValue("@to", query.To.Ticks);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            allRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));
        }
    }

    // Group by bucket and aggregate
    var grouped = allRows
        .GroupBy(r => (r.propertyName, bucket: (r.timestamp / bucketTicks) * bucketTicks))
        .OrderBy(g => g.Key.bucket);

    foreach (var group in grouped)
    {
        var bucketStart = new DateTimeOffset(group.Key.bucket, TimeSpan.Zero);
        var values = group.Select(r => r.value).ToList();
        var aggregatedValue = query.Aggregation switch
        {
            AggregationType.Avg => values.Average(),
            AggregationType.Min => values.Min(),
            AggregationType.Max => values.Max(),
            AggregationType.Sum => values.Sum(),
            AggregationType.Count => values.Count,
            AggregationType.First => values.First(),
            AggregationType.Last => values.Last(),
            _ => values.Average()
        };

        var jsonValue = JsonSerializer.SerializeToElement(aggregatedValue);
        results.Add(new HistoryRecord(query.SubjectPath, group.Key.propertyName, bucketStart, jsonValue));
    }

    return Task.FromResult<IReadOnlyList<HistoryRecord>>(results);
}
```

**Note to implementer:** This implementation loads rows into memory for cross-partition aggregation. For single-partition queries, an optimized SQL-only path using `GROUP BY (timestamp / @bucketTicks) * @bucketTicks` would be more efficient. Consider adding that optimization after the basic implementation works.

**Step 4: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: PASS

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/ src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/
git commit -m "feat: implement SQLite aggregation queries"
```

---

### Task 12: Write and Implement Snapshot Support

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkSnapshotTests.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`

**Step 1: Write snapshot tests**

```csharp
// src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkSnapshotTests.cs
using System.Text.Json;
using Namotion.Interceptor;

namespace HomeBlaze.History.Sqlite.Tests;

public class SqliteHistorySinkSnapshotTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteHistorySink _sink;

    public SqliteHistorySinkSnapshotTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"history-test-{Guid.NewGuid():N}");
        var context = InterceptorSubjectContext.Create();
        _sink = new SqliteHistorySink(context)
        {
            DatabasePath = _tempDir,
            PartitionInterval = HistoryPartitionInterval.Daily
        };
        _sink.ApplyConfigurationAsync(CancellationToken.None).Wait();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WhenSnapshotWrittenAndRead_ThenRoundTrips()
    {
        // Arrange
        var time = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new HistorySnapshot(time, "/", new Dictionary<string, HistorySubjectSnapshot>
        {
            ["/Demo/Motor"] = new HistorySubjectSnapshot(new Dictionary<string, JsonElement>
            {
                ["Temperature"] = JsonSerializer.SerializeToElement(42.5),
                ["Speed"] = JsonSerializer.SerializeToElement(100)
            })
        });

        // Act
        await _sink.WriteSnapshotAsync(snapshot);
        var result = await _sink.GetSnapshotAsync("/", time);

        // Assert
        Assert.Equal(time, result.Timestamp);
        Assert.Contains("/Demo/Motor", result.Subjects.Keys);
        Assert.Equal(42.5, result.Subjects["/Demo/Motor"].Properties["Temperature"].GetDouble());
    }

    [Fact]
    public async Task WhenSnapshotRequestedBetweenSnapshots_ThenUsesNearestBeforeAndReplays()
    {
        // Arrange
        var snapshotTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new HistorySnapshot(snapshotTime, "/", new Dictionary<string, HistorySubjectSnapshot>
        {
            ["/Demo/Motor"] = new HistorySubjectSnapshot(new Dictionary<string, JsonElement>
            {
                ["Temperature"] = JsonSerializer.SerializeToElement(42.5)
            })
        });
        await _sink.WriteSnapshotAsync(snapshot);

        // Write some changes after the snapshot
        var changeTime = snapshotTime.AddHours(6);
        await _sink.WriteBatchAsync(new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature", changeTime.Ticks,
                HistoryValueType.Double, 50.0, default)
        });

        // Act — query at a time after the change
        var queryTime = snapshotTime.AddHours(12);
        var result = await _sink.GetSnapshotAsync("/", queryTime);

        // Assert — snapshot should reflect the replayed change
        Assert.Equal(50.0, result.Subjects["/Demo/Motor"].Properties["Temperature"].GetDouble());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --filter "Snapshot" --no-restore`
Expected: FAIL

**Step 3: Implement WriteSnapshotAsync**

```csharp
public override Task WriteSnapshotAsync(HistorySnapshot snapshot)
{
    if (_partitionManager is null)
        throw new InvalidOperationException("Sink not configured");

    var partitionKey = _partitionManager.GetPartitionKey(snapshot.Timestamp);
    using var connection = _partitionManager.CreateConnection(partitionKey);

    // Serialize and gzip the snapshot
    var json = JsonSerializer.SerializeToUtf8Bytes(snapshot.Subjects);
    byte[] compressed;
    using (var ms = new MemoryStream())
    {
        using (var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
        {
            gzip.Write(json);
        }
        compressed = ms.ToArray();
    }

    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        INSERT OR REPLACE INTO snapshots (timestamp, base_path, data)
        VALUES (@timestamp, @basePath, @data)
        """;
    cmd.Parameters.AddWithValue("@timestamp", snapshot.Timestamp.Ticks);
    cmd.Parameters.AddWithValue("@basePath", snapshot.BasePath);
    cmd.Parameters.AddWithValue("@data", compressed);
    cmd.ExecuteNonQuery();

    LastSnapshotTime = snapshot.Timestamp;
    return Task.CompletedTask;
}
```

**Step 4: Implement GetSnapshotAsync**

```csharp
public override Task<HistorySnapshot> GetSnapshotAsync(string path, DateTimeOffset time)
{
    if (_partitionManager is null)
        throw new InvalidOperationException("Sink not configured");

    // 1. Find nearest snapshot before time
    HistorySnapshot? baseSnapshot = null;
    DateTimeOffset snapshotTime = DateTimeOffset.MinValue;

    foreach (var key in _partitionManager.GetPartitionKeysInRange(
        time.AddDays(-RetentionDays), time))
    {
        var dbPath = _partitionManager.GetDatabasePath(key);
        if (!File.Exists(dbPath))
            continue;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, base_path, data FROM snapshots
            WHERE timestamp <= @time
            ORDER BY timestamp DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@time", time.Ticks);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var ts = new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero);
            if (ts > snapshotTime)
            {
                snapshotTime = ts;
                var basePath = reader.GetString(1);
                var compressed = (byte[])reader["data"];
                var subjects = DecompressSnapshot(compressed);
                baseSnapshot = new HistorySnapshot(ts, basePath, subjects);
            }
        }
    }

    if (baseSnapshot is null)
    {
        return Task.FromResult(new HistorySnapshot(time, path,
            new Dictionary<string, HistorySubjectSnapshot>()));
    }

    // 2. Replay changes between snapshot and target time
    var mutableSubjects = baseSnapshot.Subjects
        .ToDictionary(
            kvp => kvp.Key,
            kvp => new Dictionary<string, JsonElement>(kvp.Value.Properties));

    // Query all changes from snapshot time to target time
    foreach (var key in _partitionManager.GetPartitionKeysInRange(snapshotTime, time))
    {
        var dbPath = _partitionManager.GetDatabasePath(key);
        if (!File.Exists(dbPath))
            continue;

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT subject_path, property_name, value_type, numeric_value, raw_value
            FROM history
            WHERE timestamp > @from AND timestamp <= @to
            ORDER BY timestamp
            """;
        cmd.Parameters.AddWithValue("@from", snapshotTime.Ticks);
        cmd.Parameters.AddWithValue("@to", time.Ticks);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var subjectPath = reader.GetString(0);
            var propertyName = reader.GetString(1);
            var valueType = (HistoryValueType)reader.GetInt32(2);
            var value = ReadJsonValue(reader, valueType);

            // Apply to path filter
            if (path != "/" && !subjectPath.StartsWith(path))
                continue;

            if (!mutableSubjects.TryGetValue(subjectPath, out var props))
            {
                props = new Dictionary<string, JsonElement>();
                mutableSubjects[subjectPath] = props;
            }
            props[propertyName] = value;
        }
    }

    // Filter by path prefix
    var filteredSubjects = mutableSubjects
        .Where(kvp => path == "/" || kvp.Key.StartsWith(path))
        .ToDictionary(
            kvp => kvp.Key,
            kvp => new HistorySubjectSnapshot(kvp.Value));

    return Task.FromResult(new HistorySnapshot(time, path, filteredSubjects));
}

private static IReadOnlyDictionary<string, HistorySubjectSnapshot> DecompressSnapshot(byte[] compressed)
{
    using var ms = new MemoryStream(compressed);
    using var gzip = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
    using var output = new MemoryStream();
    gzip.CopyTo(output);
    return JsonSerializer.Deserialize<Dictionary<string, HistorySubjectSnapshot>>(output.ToArray())
        ?? new Dictionary<string, HistorySubjectSnapshot>();
}
```

**Step 5: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: PASS

**Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/ src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/
git commit -m "feat: implement SQLite snapshot write, read, and replay"
```

---

### Task 13: Implement Snapshot Scheduling in HistoryService

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History/HistoryService.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Tests/HistoryServiceTests.cs`

**Step 1: Add snapshot creation method to HistoryService**

The HistoryService creates snapshots by traversing the live graph. Add a method that:
1. Walks all registered subjects via the registry
2. For each subject, reads current [State] property values
3. Builds a `HistorySnapshot` and calls `sink.WriteSnapshotAsync()`

**Step 2: Add snapshot scheduling to ExecuteAsync**

Start a background timer per sink based on `sink.SnapshotInterval`. On each tick:
1. Check if enough time has passed since `sink.LastSnapshotTime`
2. Create snapshot from live graph
3. Call `sink.WriteSnapshotAsync()`

Use `PeriodicTimer` with the smallest sink interval, then check each sink's individual interval.

**Step 3: Add retention housekeeping**

On each snapshot tick, also check and enforce retention:
- For SQLite sink: delete partition DB files older than `RetentionDays`
- Run `PRAGMA wal_checkpoint(TRUNCATE)` on active partitions

**Step 4: Write test for snapshot scheduling**

```csharp
[Fact]
public async Task WhenSnapshotIntervalElapses_ThenSnapshotWrittenToSink()
{
    // Arrange — set up service with a sink that has a short snapshot interval
    // Act — wait for snapshot interval to elapse
    // Assert — sink.WrittenSnapshots is not empty
}
```

**Step 5: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Tests/ --no-restore`
Expected: PASS

**Step 6: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History/
git commit -m "feat: add snapshot scheduling and retention housekeeping to HistoryService"
```

---

### Task 14: Add MCP Tools to HomeBlaze.AI

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.AI/Mcp/HistoryMcpToolProvider.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/McpBuilderExtensions.cs`
- Modify: `src/HomeBlaze/HomeBlaze.AI/HomeBlaze.AI.csproj`

**Step 1: Add HomeBlaze.History.Abstractions reference to HomeBlaze.AI**

Add to `HomeBlaze.AI.csproj`:
```xml
<ProjectReference Include="..\HomeBlaze.History.Abstractions\HomeBlaze.History.Abstractions.csproj" />
```

**Step 2: Create HistoryMcpToolProvider**

Follow the pattern from `HomeBlazeMcpToolProvider.cs`. Implement three tools:

```csharp
// src/HomeBlaze/HomeBlaze.AI/Mcp/HistoryMcpToolProvider.cs
using System.Text.Json;
using HomeBlaze.History;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;

namespace HomeBlaze.AI.Mcp;

public class HistoryMcpToolProvider : IMcpToolProvider
{
    private readonly Func<IHistoryReader?> _readerResolver;

    public HistoryMcpToolProvider(Func<IHistoryReader?> readerResolver)
    {
        _readerResolver = readerResolver;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "get_property_history",
            Description = "Query historical property values over a time range, with optional aggregation into time buckets.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Subject path (canonical format)" },
                    properties = new { type = "array", items = new { type = "string" }, description = "Property names to query" },
                    from = new { type = "string", description = "Start time (ISO 8601)" },
                    to = new { type = "string", description = "End time (ISO 8601)" },
                    bucketSize = new { type = "string", description = "Optional: bucket size for aggregation (e.g., '00:01:00' for 1 minute)" },
                    aggregation = new { type = "string", description = "Optional: Avg, Min, Max, Sum, Count, First, Last" }
                },
                required = new[] { "path", "properties", "from", "to" }
            }),
            Handler = HandleGetPropertyHistoryAsync
        };

        yield return new McpToolInfo
        {
            Name = "get_snapshot",
            Description = "Reconstruct the state of a subject or branch at a specific point in time.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Subject path or branch prefix (e.g., '/' for full graph, '/Demo' for subtree)" },
                    time = new { type = "string", description = "Point in time (ISO 8601)" }
                },
                required = new[] { "path", "time" }
            }),
            Handler = HandleGetSnapshotAsync
        };

        yield return new McpToolInfo
        {
            Name = "get_snapshots",
            Description = "Get a series of snapshots over a time range at regular intervals.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Subject path or branch prefix" },
                    from = new { type = "string", description = "Start time (ISO 8601)" },
                    to = new { type = "string", description = "End time (ISO 8601)" },
                    interval = new { type = "string", description = "Interval between snapshots (e.g., '01:00:00' for hourly)" }
                },
                required = new[] { "path", "from", "to", "interval" }
            }),
            Handler = HandleGetSnapshotsAsync
        };
    }

    private async Task<object?> HandleGetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var reader = _readerResolver();
        if (reader is null)
            return new { error = "No history sink available." };

        var path = input.GetProperty("path").GetString()!;
        var properties = input.GetProperty("properties").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        var from = DateTimeOffset.Parse(input.GetProperty("from").GetString()!);
        var to = DateTimeOffset.Parse(input.GetProperty("to").GetString()!);

        TimeSpan? bucketSize = input.TryGetProperty("bucketSize", out var bs)
            ? TimeSpan.Parse(bs.GetString()!) : null;
        AggregationType? aggregation = input.TryGetProperty("aggregation", out var agg)
            ? Enum.Parse<AggregationType>(agg.GetString()!) : null;

        var query = new HistoryQuery(path, properties, from, to, bucketSize, aggregation);
        var results = await reader.QueryAsync(query);

        var grouped = results.GroupBy(r => r.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new { t = r.Timestamp, v = r.Value }).ToArray());

        return new { records = grouped };
    }

    private async Task<object?> HandleGetSnapshotAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var reader = _readerResolver();
        if (reader is null)
            return new { error = "No history sink available." };

        var path = input.GetProperty("path").GetString()!;
        var time = DateTimeOffset.Parse(input.GetProperty("time").GetString()!);

        var snapshot = await reader.GetSnapshotAsync(path, time);
        return new { time = snapshot.Timestamp, subjects = snapshot.Subjects };
    }

    private async Task<object?> HandleGetSnapshotsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var reader = _readerResolver();
        if (reader is null)
            return new { error = "No history sink available." };

        var path = input.GetProperty("path").GetString()!;
        var from = DateTimeOffset.Parse(input.GetProperty("from").GetString()!);
        var to = DateTimeOffset.Parse(input.GetProperty("to").GetString()!);
        var interval = TimeSpan.Parse(input.GetProperty("interval").GetString()!);

        var snapshots = new List<object>();
        await foreach (var snapshot in reader.GetSnapshotsAsync(path, from, to, interval))
        {
            snapshots.Add(new { time = snapshot.Timestamp, subjects = snapshot.Subjects });
        }

        return new { snapshots };
    }
}
```

**Step 3: Register in McpBuilderExtensions**

In `WithHomeBlazeMcpTools()`, add the `HistoryMcpToolProvider` to the `ToolProviders` list. The reader resolver should find the best `IHistoryReader` from the HistoryService (or from the context's lifecycle-tracked sinks).

```csharp
// Add to McpServerConfiguration.ToolProviders:
new HistoryMcpToolProvider(() =>
{
    var historyService = sp.GetService<HistoryService>();
    return historyService?.GetBestReader();
})
```

**Note to implementer:** Add a `GetBestReader()` method to `HistoryService` that returns the highest-priority reader from the discovered sinks. This method should be public and handle the case where no sinks are available (return null).

**Step 4: Build**

Run: `dotnet build src/HomeBlaze/HomeBlaze.AI/HomeBlaze.AI.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.AI/
git commit -m "feat: add history MCP tools (get_property_history, get_snapshot, get_snapshots)"
```

---

### Task 15: Wire Up in Program.cs and Register Types

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze/Program.cs`

**Step 1: Add service registration**

After `builder.Services.AddHomeBlazeHost()`, add:

```csharp
builder.Services.AddHistoryService(bufferTime: TimeSpan.FromSeconds(1));
```

Add `using HomeBlaze.History;` at the top.

**Step 2: Register type in TypeProvider**

After the existing `typeProvider.AddAssembly(...)` calls, add:

```csharp
typeProvider.AddAssembly(typeof(SqliteHistorySink).Assembly);
```

Add `using HomeBlaze.History.Sqlite;` at the top.

**Step 3: Add project references to main HomeBlaze.csproj**

```xml
<ProjectReference Include="..\HomeBlaze.History\HomeBlaze.History.csproj" />
<ProjectReference Include="..\HomeBlaze.History.Sqlite\HomeBlaze.History.Sqlite.csproj" />
```

**Step 4: Create sample config file**

Create `src/HomeBlaze/HomeBlaze/Data/history-sqlite.json`:

```json
{
  "$type": "HomeBlaze.History.Sqlite.SqliteHistorySink",
  "DatabasePath": "./Data/history",
  "PartitionInterval": "Weekly",
  "SnapshotInterval": "1.00:00:00",
  "RetentionDays": 365,
  "Priority": 100
}
```

**Step 5: Build and verify the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 6: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All PASS

**Step 7: Commit**

```bash
git add src/HomeBlaze/HomeBlaze/ src/HomeBlaze/HomeBlaze.History.Sqlite/
git commit -m "feat: wire up history system in HomeBlaze with SQLite sink and sample config"
```

---

### Task 16: Implement GetSnapshotsAsync (Series)

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite/SqliteHistorySink.cs`
- Modify: `src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/SqliteHistorySinkSnapshotTests.cs`

**Step 1: Write test for snapshot series**

```csharp
[Fact]
public async Task WhenQueryingSnapshotSeries_ThenReturnsSnapshotsAtIntervals()
{
    // Arrange — write snapshot and several changes
    var baseTime = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    var snapshot = new HistorySnapshot(baseTime, "/", new Dictionary<string, HistorySubjectSnapshot>
    {
        ["/Demo/Motor"] = new HistorySubjectSnapshot(new Dictionary<string, JsonElement>
        {
            ["Temperature"] = JsonSerializer.SerializeToElement(40.0)
        })
    });
    await _sink.WriteSnapshotAsync(snapshot);

    // Write changes at different hours
    for (var h = 1; h <= 6; h++)
    {
        await _sink.WriteBatchAsync(new[]
        {
            new ResolvedHistoryRecord("/Demo/Motor", "Temperature",
                baseTime.AddHours(h).Ticks, HistoryValueType.Double, 40.0 + h, default)
        });
    }

    // Act — get snapshots every 2 hours for 6 hours
    var snapshots = new List<HistorySnapshot>();
    await foreach (var s in _sink.GetSnapshotsAsync("/", baseTime, baseTime.AddHours(6), TimeSpan.FromHours(2)))
    {
        snapshots.Add(s);
    }

    // Assert — expect 4 snapshots (at 0h, 2h, 4h, 6h)
    Assert.Equal(4, snapshots.Count);
}
```

**Step 2: Implement GetSnapshotsAsync**

```csharp
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
```

**Step 3: Run tests**

Run: `dotnet test src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/ --no-restore`
Expected: PASS

**Step 4: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.History.Sqlite/ src/HomeBlaze/HomeBlaze.History.Sqlite.Tests/
git commit -m "feat: implement GetSnapshotsAsync for snapshot time series"
```

---

### Task 17: Final Integration Test and Full Build

**Step 1: Run full solution build**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with zero warnings

**Step 2: Run all unit tests**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: All PASS

**Step 3: Verify no leftover TODOs**

Search for `throw new NotImplementedException()` in history projects. All should be resolved except `WriteMovesAsync` (planned follow-up) and `OldestRecord` (nice-to-have).

**Step 4: Final commit**

```bash
git add -A
git commit -m "chore: final cleanup for history system implementation"
```
