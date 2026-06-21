# ChangeQueueProcessor Overflow Behavior Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hard-coded `maxQueueDepth` drop on `ChangeQueueProcessor` with an explicit `OverflowBehavior` (`Unbounded`/`DropOldest`/`DropNewest`), a synchronous overflow callback, a consolidated `ChangeQueueProcessorConfiguration`, and a single `SubjectSourceBase` seam where a future resync plugs in.

**Architecture:** A new `ChangeQueueProcessorConfiguration` carries the processor's tuning knobs (buffer time, bound, behavior, overflow handler). The behavior gates the bound: `Unbounded` ignores `MaxQueueSize`; `DropOldest`/`DropNewest` require it. The bound stays on the raw pre-dedup change count, enforced on the existing single-producer lock-free enqueue path. `SubjectSourceBase` exposes one overridable factory (`CreateChangeQueueConfiguration`) and wraps the returned handler with a warning log. Default everywhere is `Unbounded`, so no current connector changes behavior. Per-connector resync is a separate follow-up.

**Tech Stack:** C# 13, .NET 9.0 (`Namotion.Interceptor.Connectors`), xUnit + `PublicApiGenerator` + Verify snapshot tests.

**Reference spec:** `docs/superpowers/specs/2026-06-22-changequeue-overflow-policy-design.md`

---

## Conventions (read before executing)

- **Branch:** do this work on a feature branch off `master` (for example `feature/changequeue-overflow-policy`), not directly on `master`.
- **Build:** `dotnet build src/Namotion.Interceptor.slnx` (warnings are errors).
- **Unit tests (default):** `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`.
- **No auto-commits:** the "Commit" steps mark natural commit points; pause for the maintainer's go-ahead rather than committing unprompted. Conventional-commit prefixes (`feat:`, `refactor:`), no AI attribution.
- **No em dashes** in code comments, docs, or commit messages; use commas, parentheses, or periods.
- **Test conventions:** `When<Condition>_Then<ExpectedBehavior>` naming; explicit `// Arrange` / `// Act` / `// Assert` comments (`// Act & Assert` for exception tests); no hardcoded waits (use `AsyncTestHelpers.WaitUntilAsync` or `ManualResetEventSlim`/`CountdownEvent`, never `Task.Delay`/`Thread.Sleep`).
- **The plan and spec under `docs/superpowers/` are scratch artifacts: do not commit them.**
- Type lookup: the test namespace `Namotion.Interceptor.Connectors.Tests` is nested under `Namotion.Interceptor.Connectors`, so new types in that namespace are visible in tests without a `using`.

---

## File Structure

**New files (`src/Namotion.Interceptor.Connectors/`):**
- `OverflowBehavior.cs` - the `Unbounded`/`DropOldest`/`DropNewest` enum.
- `ChangeQueueOverflow.cs` - the readonly record struct passed to the overflow handler.
- `ChangeQueueProcessorConfiguration.cs` - the processor tuning configuration with `Validate()`.

**Modified files:**
- `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` - constructor takes the configuration; `DropCount` renamed to `DroppedChangeCount`; behavior-gated bound; overflow handler firing.
- `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs` - `CreateChangeQueueConfiguration` virtual; handler warning-log wrap; build processor from the configuration.
- `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs` - new constructor call.
- `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs` - new constructor call.
- `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs` - new constructor call.
- `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs` - update call sites, rename `DropCount`, add behavior and handler tests.
- `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionEchoSuppressionTests.cs` - update call site.
- `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs` - add config override hook.
- `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt` - accept new snapshot.

**New test file:**
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseOverflowTests.cs` - the seam test.

---

## Task 1: New public types (enum, payload, configuration)

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/OverflowBehavior.cs`
- Create: `src/Namotion.Interceptor.Connectors/ChangeQueueOverflow.cs`
- Create: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessorConfiguration.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorConfigurationTests.cs`

- [ ] **Step 1: Write the failing `Validate` tests**

Create `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorConfigurationTests.cs`:

```csharp
using Namotion.Interceptor.Connectors;

namespace Namotion.Interceptor.Connectors.Tests;

public class ChangeQueueProcessorConfigurationTests
{
    [Fact]
    public void WhenUnbounded_ThenValidatePasses()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration();

        // Act & Assert
        configuration.Validate();
    }

    [Fact]
    public void WhenBoundedWithPositiveMaxQueueSize_ThenValidatePasses()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropOldest,
            MaxQueueSize = 100,
        };

        // Act & Assert
        configuration.Validate();
    }

    [Fact]
    public void WhenBoundedWithoutMaxQueueSize_ThenValidateThrows()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropNewest,
            MaxQueueSize = null,
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void WhenBoundedWithNonPositiveMaxQueueSize_ThenValidateThrows()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration
        {
            OverflowBehavior = OverflowBehavior.DropOldest,
            MaxQueueSize = 0,
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }

    [Fact]
    public void WhenDefault_ThenUnboundedWithEightMillisecondBufferTime()
    {
        // Arrange
        var configuration = new ChangeQueueProcessorConfiguration();

        // Act & Assert
        Assert.Equal(OverflowBehavior.Unbounded, configuration.OverflowBehavior);
        Assert.Equal(TimeSpan.FromMilliseconds(8), configuration.BufferTime);
        Assert.Null(configuration.MaxQueueSize);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ChangeQueueProcessorConfigurationTests"`
Expected: FAIL to compile (`OverflowBehavior` and `ChangeQueueProcessorConfiguration` do not exist yet).

- [ ] **Step 3: Create the `OverflowBehavior` enum**

Create `src/Namotion.Interceptor.Connectors/OverflowBehavior.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Controls how <see cref="ChangeQueueProcessor"/> reacts when its buffered change queue
/// reaches <see cref="ChangeQueueProcessorConfiguration.MaxQueueSize"/>.
/// </summary>
public enum OverflowBehavior
{
    /// <summary>
    /// No bound is applied and <see cref="ChangeQueueProcessorConfiguration.MaxQueueSize"/> is ignored.
    /// This is the default and matches the original unbounded behavior.
    /// </summary>
    Unbounded = 0,

    /// <summary>
    /// On overflow, drop the oldest queued changes until the queue is back within the bound,
    /// so the newest change is retained.
    /// </summary>
    DropOldest,

    /// <summary>
    /// On overflow, reject the incoming change and keep what is already queued.
    /// </summary>
    DropNewest,
}
```

- [ ] **Step 4: Create the `ChangeQueueOverflow` payload**

Create `src/Namotion.Interceptor.Connectors/ChangeQueueOverflow.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Describes a single overflow event on a <see cref="ChangeQueueProcessor"/>. Passed to
/// <see cref="ChangeQueueProcessorConfiguration.OverflowHandler"/> once per overflow event
/// (not once per dropped change). <see cref="OverflowBehavior"/> is always
/// <see cref="OverflowBehavior.DropOldest"/> or <see cref="OverflowBehavior.DropNewest"/> here,
/// since <see cref="OverflowBehavior.Unbounded"/> never overflows.
/// </summary>
/// <param name="DroppedChangeCount">Number of changes dropped in this overflow event.</param>
/// <param name="OverflowBehavior">The behavior that produced the drop.</param>
/// <param name="MaxQueueSize">The configured queue bound that was exceeded.</param>
public readonly record struct ChangeQueueOverflow(
    int DroppedChangeCount,
    OverflowBehavior OverflowBehavior,
    int MaxQueueSize);
```

- [ ] **Step 5: Create the `ChangeQueueProcessorConfiguration`**

Create `src/Namotion.Interceptor.Connectors/ChangeQueueProcessorConfiguration.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Tuning configuration for a <see cref="ChangeQueueProcessor"/>.
/// </summary>
public sealed class ChangeQueueProcessorConfiguration
{
    /// <summary>
    /// Gets or sets the time to buffer changes before flushing. Default is 8ms.
    /// A value less than or equal to zero disables buffering (each change is processed individually).
    /// </summary>
    public TimeSpan BufferTime { get; set; } = TimeSpan.FromMilliseconds(8);

    /// <summary>
    /// Gets or sets how the processor reacts on overflow. Default is <see cref="OverflowBehavior.Unbounded"/>.
    /// </summary>
    public OverflowBehavior OverflowBehavior { get; set; } = OverflowBehavior.Unbounded;

    /// <summary>
    /// Gets or sets the bound on the buffered (pre-deduplication) change count. Required and must be
    /// positive when <see cref="OverflowBehavior"/> is <see cref="OverflowBehavior.DropOldest"/> or
    /// <see cref="OverflowBehavior.DropNewest"/>; ignored when <see cref="OverflowBehavior.Unbounded"/>.
    /// The queue coalesces by property at flush, so a burst on a single property can inflate this count.
    /// </summary>
    public int? MaxQueueSize { get; set; }

    /// <summary>
    /// Gets or sets a synchronous callback invoked once per overflow event (not once per dropped change).
    /// It runs on the producer thread and must be non-blocking: only record or flag, never do I/O inline.
    /// </summary>
    public Action<ChangeQueueOverflow>? OverflowHandler { get; set; }

    /// <summary>
    /// Validates the configuration and throws if invalid.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a bounded behavior has no positive <see cref="MaxQueueSize"/>.</exception>
    public void Validate()
    {
        if (OverflowBehavior != OverflowBehavior.Unbounded && MaxQueueSize is not > 0)
        {
            throw new ArgumentException(
                $"MaxQueueSize must be a positive value when OverflowBehavior is {OverflowBehavior}, got: {(MaxQueueSize?.ToString() ?? "null")}.",
                nameof(MaxQueueSize));
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~ChangeQueueProcessorConfigurationTests"`
Expected: PASS (5 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/OverflowBehavior.cs \
        src/Namotion.Interceptor.Connectors/ChangeQueueOverflow.cs \
        src/Namotion.Interceptor.Connectors/ChangeQueueProcessorConfiguration.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorConfigurationTests.cs
git commit -m "feat: add ChangeQueueProcessor overflow configuration types"
```

---

## Task 2: Rework `ChangeQueueProcessor` to use the configuration

This changes the constructor signature, so every call site (production and tests) must update before the solution compiles again. The commit happens once the build is green. This task implements all three behaviors and the drop counter, but not the overflow handler firing (Task 3).

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs:208-211`
- Modify: `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs:171-178`
- Modify: `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs:368-373`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs:87-94`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionEchoSuppressionTests.cs:336-350`

- [ ] **Step 1: Replace the overflow fields in `ChangeQueueProcessor`**

In `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`, replace the field block (currently lines 27-36):

```csharp
    private readonly int? _maxQueueDepth;
    private long _dropCount;
    private int _flushGate; // 0 = free, 1 = flushing
    private int _disposed; // 0 = not disposed, 1 = disposed (use Interlocked for thread-safe check)

    /// <summary>
    /// Number of buffered changes dropped due to bounded-queue overflow.
    /// Always zero when <c>maxQueueDepth</c> is null (unbounded).
    /// </summary>
    public long DropCount => Interlocked.Read(ref _dropCount);
```

with:

```csharp
    private readonly OverflowBehavior _overflowBehavior;
    private readonly int? _maxQueueSize;
    private readonly Action<ChangeQueueOverflow>? _overflowHandler;
    private long _droppedChangeCount;
    private int _flushGate; // 0 = free, 1 = flushing
    private int _disposed; // 0 = not disposed, 1 = disposed (use Interlocked for thread-safe check)

    /// <summary>
    /// Number of buffered changes dropped due to bounded-queue overflow.
    /// Always zero when <see cref="OverflowBehavior.Unbounded"/> (the default).
    /// </summary>
    public long DroppedChangeCount => Interlocked.Read(ref _droppedChangeCount);
```

- [ ] **Step 2: Replace the constructor**

Replace the constructor and its XML doc (currently lines 51-96) with:

```csharp
    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeQueueProcessor"/> class.
    /// The subscription is created immediately so that changes are captured from this point,
    /// even before <see cref="ProcessAsync"/> is called. This prevents change loss during
    /// initialization gaps (e.g., between OPC UA node creation and processing start).
    /// </summary>
    /// <param name="source">Source to ignore (to prevent update loops).</param>
    /// <param name="context">The interceptor subject context.</param>
    /// <param name="propertyFilter">Filter to determine if a property change should be included.
    /// The <see cref="PropertyReference"/> may not have a registered property (e.g., when the subject
    /// is momentarily unregistered due to a concurrent structural mutation). Callers should handle
    /// this case explicitly, typically by resolving via <c>TryGetRegisteredProperty()</c> and
    /// returning <c>false</c> when null.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="configuration">The processor tuning configuration (buffer time, overflow bound and behavior, overflow handler).</param>
    /// <param name="logger">The logger.</param>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<PropertyReference, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        ChangeQueueProcessorConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();

        _source = source;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = configuration.BufferTime;
        _overflowBehavior = configuration.OverflowBehavior;
        _maxQueueSize = configuration.MaxQueueSize;
        _overflowHandler = configuration.OverflowHandler;

        try
        {
            _subscription = context.CreatePropertyChangeQueueSubscription();
        }
        catch
        {
            ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
            _flushDedupedBuffer = null!;
            throw;
        }
    }
```

Note: `_bufferTime` is already declared (currently assigned `bufferTime ?? TimeSpan.FromMilliseconds(8)`); it is now assigned directly from `configuration.BufferTime`, which already defaults to 8ms.

- [ ] **Step 3: Replace the buffered enqueue branch**

Replace the buffered `else` branch in `ProcessAsync` (currently lines 172-182):

```csharp
                else
                {
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(change);

                    // Optional bounded-queue backpressure: drop oldest changes on overflow
                    if (_maxQueueDepth is int maxQueueDepth && _changes.Count > maxQueueDepth)
                    {
                        DropOverflow(maxQueueDepth);
                    }
                }
```

with:

```csharp
                else if (_maxQueueSize is int dropNewestBound
                         && _overflowBehavior == OverflowBehavior.DropNewest
                         && _changes.Count >= dropNewestBound)
                {
                    // DropNewest: reject the incoming change, keep what is already queued.
                    // Single-producer enqueue makes the pre-check exact.
                    RecordOverflow(1);
                }
                else
                {
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(change);

                    // DropOldest backpressure: drop oldest changes until back within the bound.
                    if (_maxQueueSize is int dropOldestBound
                        && _overflowBehavior == OverflowBehavior.DropOldest
                        && _changes.Count > dropOldestBound)
                    {
                        DropOldestOverflow(dropOldestBound);
                    }
                }
```

- [ ] **Step 4: Replace the `DropOverflow` helper**

Replace the `DropOverflow` method and its XML doc (currently lines 192-203):

```csharp
    /// <summary>
    /// Drops the oldest buffered changes until the queue is back within <paramref name="maxQueueDepth"/>,
    /// incrementing <see cref="DropCount"/> for each. Best-effort: a concurrent flush may drain the queue
    /// below the bound first, in which case fewer drops occur.
    /// </summary>
    private void DropOverflow(int maxQueueDepth)
    {
        while (_changes.Count > maxQueueDepth && _changes.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropCount);
        }
    }
```

with:

```csharp
    /// <summary>
    /// Drops the oldest buffered changes until the queue is back within <paramref name="maxQueueSize"/>,
    /// then records a single overflow event for the batch. Best-effort: a concurrent flush may drain the
    /// queue below the bound first, in which case fewer drops occur.
    /// </summary>
    private void DropOldestOverflow(int maxQueueSize)
    {
        var dropped = 0;
        while (_changes.Count > maxQueueSize && _changes.TryDequeue(out _))
        {
            dropped++;
        }

        if (dropped > 0)
        {
            RecordOverflow(dropped);
        }
    }

    /// <summary>
    /// Records an overflow event: adds to <see cref="DroppedChangeCount"/>. The overflow handler
    /// is invoked here in a later change (Task 3); for now only the counter is updated.
    /// </summary>
    private void RecordOverflow(int droppedCount)
    {
        Interlocked.Add(ref _droppedChangeCount, droppedCount);
    }
```

- [ ] **Step 5: Update the OPC UA server call site**

In `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`, replace the constructor call (currently lines 208-211):

```csharp
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        _configuration.BufferTime, maxQueueDepth: null, logger: _logger);
```

with:

```csharp
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this, _context,
                        propertyFilter: IsPropertyIncluded, writeHandler: WriteChangesAsync,
                        new ChangeQueueProcessorConfiguration
                        {
                            BufferTime = _configuration.BufferTime ?? TimeSpan.FromMilliseconds(8),
                        },
                        logger: _logger);
```

Note: `OpcUaServerConfiguration.BufferTime` is `TimeSpan?`, so it is coalesced to the 8ms default. Ensure `using Namotion.Interceptor.Connectors;` is present (it already is, since `ChangeQueueProcessor` is used here).

- [ ] **Step 6: Update the MQTT server call site**

In `src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs`, replace the constructor call (currently lines 171-178):

```csharp
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this,
                        _context,
                        propertyFilter: IsPropertyIncluded,
                        writeHandler: WriteChangesAsync,
                        _configuration.BufferTime,
                        maxQueueDepth: null,
                        logger: _logger);
```

with:

```csharp
                    using var changeQueueProcessor = new ChangeQueueProcessor(
                        source: this,
                        _context,
                        propertyFilter: IsPropertyIncluded,
                        writeHandler: WriteChangesAsync,
                        new ChangeQueueProcessorConfiguration { BufferTime = _configuration.BufferTime },
                        logger: _logger);
```

Note: `MqttServerConfiguration.BufferTime` is a non-nullable `TimeSpan`, so it maps directly.

- [ ] **Step 7: Update the WebSocket server call site**

In `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs`, replace `CreateChangeQueueProcessor` (currently lines 368-373):

```csharp
    public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
        new(source: this, Context,
            propertyFilter: propertyReference =>
                propertyReference.TryGetRegisteredProperty() is { } property &&
                (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
            writeHandler: BroadcastChangesAsync, BufferTime, null, logger);
```

with:

```csharp
    public ChangeQueueProcessor CreateChangeQueueProcessor(ILogger logger) =>
        new(source: this, Context,
            propertyFilter: propertyReference =>
                propertyReference.TryGetRegisteredProperty() is { } property &&
                (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
            writeHandler: BroadcastChangesAsync,
            new ChangeQueueProcessorConfiguration { BufferTime = BufferTime },
            logger);
```

Note: `BufferTime` here is the handler's `public TimeSpan BufferTime => _configuration.BufferTime;` (non-nullable). Ensure `using Namotion.Interceptor.Connectors;` is present (it already is).

- [ ] **Step 8: Update the `SubjectSourceBase` call site (interim)**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, replace the processor construction (currently lines 87-94):

```csharp
                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    _bufferTime,
                    maxQueueDepth: null,
                    logger: _logger);
```

with:

```csharp
                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    new ChangeQueueProcessorConfiguration { BufferTime = _bufferTime },
                    logger: _logger);
```

(Task 4 replaces this with the `CreateChangeQueueConfiguration` seam.)

- [ ] **Step 9: Update the existing `ChangeQueueProcessorTests` call sites**

In `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`, every existing `new ChangeQueueProcessor(...)` passes `bufferTime: X, maxQueueDepth: null`. Replace each such pair of arguments with a configuration. There are six unbounded call sites (around lines 23-34, 64-74, 103-113, 147-157, 177-183, 204-215) and two in the overflow/unbounded tests (around lines 254-261 and 295-309).

For each unbounded call site, replace the two lines:

```csharp
            bufferTime: TimeSpan.FromMilliseconds(50),
            maxQueueDepth: null,
```

with (preserving each call site's own buffer time value):

```csharp
            configuration: new ChangeQueueProcessorConfiguration { BufferTime = TimeSpan.FromMilliseconds(50) },
```

For the bounded test `WhenBoundedQueueOverflows_ThenOldestChangesAreDropped` (around lines 254-261), replace:

```csharp
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            bufferTime: TimeSpan.FromMinutes(10),
            maxQueueDepth: 2,
            logger: NullLogger.Instance);
```

with:

```csharp
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            configuration: new ChangeQueueProcessorConfiguration
            {
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
            },
            logger: NullLogger.Instance);
```

Then in that same test, rename the two `processor.DropCount` references (around lines 273 and 277) to `processor.DroppedChangeCount`.

For the `WhenUnbounded_ThenNoChangesAreDropped` test (around lines 295-325), replace the `bufferTime`/`maxQueueDepth` pair with `configuration: new ChangeQueueProcessorConfiguration { BufferTime = TimeSpan.FromMilliseconds(20) }`, and rename `processor.DropCount` (around line 325) to `processor.DroppedChangeCount`.

- [ ] **Step 10: Update the transaction echo-suppression test call site**

In `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionEchoSuppressionTests.cs`, replace the constructor call (currently lines 336-350) so the `bufferTime`/`maxQueueDepth` pair becomes a configuration:

```csharp
        var processor = new ChangeQueueProcessor(
            source: serverSource,
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                lock (serverReceived)
                {
                    serverReceived.AddRange(changes.ToArray());
                }
                return ValueTask.CompletedTask;
            },
            configuration: new ChangeQueueProcessorConfiguration { BufferTime = TimeSpan.FromMilliseconds(8) },
            logger: NullLogger.Instance);
```

- [ ] **Step 11: Build the solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS (no remaining references to `maxQueueDepth` or `DropCount`). If the build flags a missed call site, update it to the configuration form.

- [ ] **Step 12: Add the `DropNewest` behavior test**

In `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`, add this test (next to the existing overflow tests). It mirrors the existing `WhenBoundedQueueOverflows` setup (large buffer time so the periodic flush never drains the queue):

```csharp
    [Fact]
    public async Task WhenDropNewestQueueOverflows_ThenIncomingChangesAreDropped()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            configuration: new ChangeQueueProcessorConfiguration
            {
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropNewest,
                MaxQueueSize = 2,
            },
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act - five changes into a queue bounded to two; the three newest must be dropped
        for (var i = 1; i <= 5; i++)
        {
            subject.FirstName = $"v{i}";
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => processor.DroppedChangeCount >= 3,
            message: "Three of the five changes should be dropped");

        // Assert
        Assert.Equal(3, processor.DroppedChangeCount);

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }
```

- [ ] **Step 13: Run the Connectors tests**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`
Expected: PASS (existing tests adapted, plus the new `DropNewest` test and the Task 1 configuration tests).

- [ ] **Step 14: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs \
        src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs \
        src/Namotion.Interceptor.Mqtt/Server/MqttSubjectServer.cs \
        src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs \
        src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs \
        src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionEchoSuppressionTests.cs
git commit -m "refactor: drive ChangeQueueProcessor overflow via configuration and OverflowBehavior"
```

---

## Task 3: Fire the overflow handler

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`

- [ ] **Step 1: Write the failing handler tests**

Add to `src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs`:

```csharp
    [Fact]
    public async Task WhenDropOldestOverflows_ThenOverflowHandlerFiresWithDroppedCount()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);
        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();

        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (_, _) => ValueTask.CompletedTask,
            configuration: new ChangeQueueProcessorConfiguration
            {
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
                OverflowHandler = overflow => overflows.Enqueue(overflow),
            },
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act - five changes into a queue bounded to two; three single-drop events expected
        for (var i = 1; i <= 5; i++)
        {
            subject.FirstName = $"v{i}";
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => overflows.Count >= 3,
            message: "Three overflow events should be reported");

        // Assert - each event reports one drop, with the configured behavior and bound
        Assert.Equal(3, overflows.Count);
        Assert.All(overflows, overflow =>
        {
            Assert.Equal(1, overflow.DroppedChangeCount);
            Assert.Equal(OverflowBehavior.DropOldest, overflow.OverflowBehavior);
            Assert.Equal(2, overflow.MaxQueueSize);
        });

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }

    [Fact]
    public async Task WhenUnboundedOverflows_ThenOverflowHandlerNeverFires()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);
        var handlerFired = false;

        var lastWritten = "";
        using var processor = new ChangeQueueProcessor(
            source: new object(),
            context: context,
            propertyFilter: _ => true,
            writeHandler: (changes, _) =>
            {
                foreach (var change in changes.ToArray())
                {
                    lastWritten = change.GetNewValue<string>() ?? lastWritten;
                }
                return ValueTask.CompletedTask;
            },
            configuration: new ChangeQueueProcessorConfiguration
            {
                BufferTime = TimeSpan.FromMilliseconds(20),
                OverflowHandler = _ => handlerFired = true,
            },
            logger: NullLogger.Instance);

        using var cancellation = new CancellationTokenSource();
        var processing = processor.ProcessAsync(cancellation.Token);

        // Act
        for (var i = 1; i <= 5; i++)
        {
            subject.FirstName = $"v{i}";
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => lastWritten == "v5",
            message: "The newest change should be flushed");

        // Assert
        Assert.False(handlerFired);

        // Cleanup
        await cancellation.CancelAsync();
        await processing;
    }
```

- [ ] **Step 2: Run the new tests to verify the first fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~WhenDropOldestOverflows_ThenOverflowHandlerFiresWithDroppedCount"`
Expected: FAIL (no overflow is reported because `RecordOverflow` does not invoke the handler yet).

- [ ] **Step 3: Invoke the handler in `RecordOverflow`**

In `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs`, update `RecordOverflow` to invoke the handler:

```csharp
    /// <summary>
    /// Records an overflow event: adds to <see cref="DroppedChangeCount"/> and invokes the overflow
    /// handler once for the event. The handler runs synchronously on the producer thread and must be
    /// non-blocking.
    /// </summary>
    private void RecordOverflow(int droppedCount)
    {
        Interlocked.Add(ref _droppedChangeCount, droppedCount);
        _overflowHandler?.Invoke(new ChangeQueueOverflow(droppedCount, _overflowBehavior, _maxQueueSize!.Value));
    }
```

Note: `_maxQueueSize` is guaranteed non-null here, because `RecordOverflow` is only reached from the bounded branches where `_maxQueueSize is int`.

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~OverflowHandler"`
Expected: PASS (both handler tests).

- [ ] **Step 5: Run the full Connectors test project**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs \
        src/Namotion.Interceptor.Connectors.Tests/ChangeQueueProcessorTests.cs
git commit -m "feat: fire ChangeQueueProcessor overflow handler once per event"
```

---

## Task 4: `SubjectSourceBase` overflow seam

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseOverflowTests.cs`

- [ ] **Step 1: Add the `CreateChangeQueueConfigurationOverride` hook to `TestSubjectSource`**

In `src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs`, add the override property and the protected override (after the existing `WriteChangesAsync` override):

```csharp
    public Func<ChangeQueueProcessorConfiguration>? CreateChangeQueueConfigurationOverride { get; init; }

    protected override ChangeQueueProcessorConfiguration CreateChangeQueueConfiguration()
        => CreateChangeQueueConfigurationOverride is not null
            ? CreateChangeQueueConfigurationOverride()
            : base.CreateChangeQueueConfiguration();
```

Add `using Namotion.Interceptor.Connectors;` at the top if the compiler flags `ChangeQueueProcessorConfiguration` (the file currently lives in the `Namotion.Interceptor.Connectors.Tests` namespace, so the type is visible without a using).

- [ ] **Step 2: Write the failing seam test**

Create `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseOverflowTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class SubjectSourceBaseOverflowTests
{
    [Fact]
    public async Task WhenSourceConfiguresBoundedQueue_ThenOverflowHandlerFires()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        context.WithRegistry();
        context.WithPropertyChangeQueue();

        var subject = new Person(context);

        var overflows = new System.Collections.Concurrent.ConcurrentQueue<ChangeQueueOverflow>();
        using var source = new TestSubjectSource(subject, context, NullLogger.Instance)
        {
            CreateChangeQueueConfigurationOverride = () => new ChangeQueueProcessorConfiguration
            {
                // Large buffer time keeps the periodic flush from draining the queue, so the bound
                // is exercised purely by enqueue-side overflow.
                BufferTime = TimeSpan.FromMinutes(10),
                OverflowBehavior = OverflowBehavior.DropOldest,
                MaxQueueSize = 2,
                OverflowHandler = overflow => overflows.Enqueue(overflow),
            },
        };

        // The processor only handles changes to properties whose source is this source, and ignores
        // changes that originated from this source. Claim ownership, then mutate locally (source null)
        // so the changes are queued for outbound writing.
        new PropertyReference(subject, nameof(Person.FirstName)).SetSource(source);

        using var cancellation = new CancellationTokenSource();
        await source.StartAsync(cancellation.Token);

        // Act - continuously produce unique changes until the bounded queue overflows. Looping covers
        // the window between StartAsync returning and the background processor's subscription going
        // live, so no change is missed because of startup timing. Unique values keep the equality
        // interceptor from coalescing the changes away.
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && overflows.Count < 3)
            {
                subject.FirstName = Guid.NewGuid().ToString("N");
                await Task.Yield();
            }
        });

        await AsyncTestHelpers.WaitUntilAsync(
            () => overflows.Count >= 3,
            message: "Three overflow events should be reported through the seam");

        // Assert
        Assert.All(overflows, overflow => Assert.Equal(OverflowBehavior.DropOldest, overflow.OverflowBehavior));

        // Cleanup
        await cancellation.CancelAsync();
        await producer;
        await source.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 3: Run the seam test to verify it fails**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceBaseOverflowTests"`
Expected: FAIL to compile (`CreateChangeQueueConfiguration` is not defined on `SubjectSourceBase`, so `base.CreateChangeQueueConfiguration()` in `TestSubjectSource` does not resolve).

- [ ] **Step 4: Add the seam to `SubjectSourceBase`**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, add `using Namotion.Interceptor.Connectors;` is not needed (same namespace). Add this virtual method (place it just before `ExecuteAsync`, after the `WriteChangesAsync` abstract declaration near line 73):

```csharp
    /// <summary>
    /// Creates the configuration for this source's <see cref="ChangeQueueProcessor"/>. Override to opt
    /// into a bounded queue and react to overflow (for example, to request a resync). The default is
    /// unbounded with the source's configured buffer time. The base wraps the returned
    /// <see cref="ChangeQueueProcessorConfiguration.OverflowHandler"/> so overflow is also logged.
    /// </summary>
    /// <returns>The processor configuration.</returns>
    protected virtual ChangeQueueProcessorConfiguration CreateChangeQueueConfiguration()
        => new() { BufferTime = _bufferTime };
```

- [ ] **Step 5: Build the configuration and wrap the handler in `ExecuteAsync`**

In `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`, replace the processor construction added in Task 2 Step 8:

```csharp
                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    new ChangeQueueProcessorConfiguration { BufferTime = _bufferTime },
                    logger: _logger);
```

with:

```csharp
                var changeQueueConfiguration = CreateChangeQueueConfiguration();
                var sourceOverflowHandler = changeQueueConfiguration.OverflowHandler;
                changeQueueConfiguration.OverflowHandler = overflow =>
                {
                    _logger.LogWarning(
                        "Change queue overflow in source: dropped {Count} change(s) ({Behavior}).",
                        overflow.DroppedChangeCount, overflow.OverflowBehavior);
                    sourceOverflowHandler?.Invoke(overflow);
                };

                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    changeQueueConfiguration,
                    logger: _logger);
```

- [ ] **Step 6: Run the seam test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SubjectSourceBaseOverflowTests"`
Expected: PASS.

- [ ] **Step 7: Run the full Connectors test project**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs \
        src/Namotion.Interceptor.Connectors.Tests/TestSubjectSource.cs \
        src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseOverflowTests.cs
git commit -m "feat: add SubjectSourceBase overflow configuration seam"
```

---

## Task 5: Accept the public API snapshot and verify the whole solution

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`

- [ ] **Step 1: Run the public API snapshot test**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL. The `ChangeQueueProcessor` constructor and `DropCount` changed, and three public types were added, so `VerifyChecksTests.PublicApi` writes a `VerifyChecksTests.PublicApi.received.txt` that differs from the verified snapshot. The new surface should include (formatting per `PublicApiGenerator`):

```
public ChangeQueueProcessor(object? source, Namotion.Interceptor.IInterceptorSubjectContext context, System.Func<Namotion.Interceptor.PropertyReference, bool> propertyFilter, System.Func<System.ReadOnlyMemory<Namotion.Interceptor.Tracking.Change.SubjectPropertyChange>, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask> writeHandler, Namotion.Interceptor.Connectors.ChangeQueueProcessorConfiguration configuration, Microsoft.Extensions.Logging.ILogger logger) { }
public long DroppedChangeCount { get; }
```

plus the new `ChangeQueueProcessorConfiguration` class, the `ChangeQueueOverflow` readonly record struct (with its generated equality and `Deconstruct` members), the `OverflowBehavior` enum, and the new `SubjectSourceBase.CreateChangeQueueConfiguration` protected member.

- [ ] **Step 2: Accept the new snapshot**

Replace the verified snapshot with the received output (per the repo convention in CLAUDE.md):

```bash
cp src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
```

If your runner writes the received file to a different location, locate it with:

Run: `git status --porcelain` and look for the `*.received.txt` artifact, then copy it over the `*.verified.txt`.

- [ ] **Step 3: Re-run the snapshot test to verify it passes**

Run: `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: PASS.

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS (warnings are errors).

- [ ] **Step 5: Run the full unit test suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. This covers the OPC UA / MQTT / WebSocket projects whose server call sites changed (the affected paths are exercised by their unit tests; integration tests are out of scope for this mechanism-only change).

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "test: accept ChangeQueueProcessor public API snapshot"
```

---

## Follow-up (not in this plan)

Open a separate issue for per-connector resync-on-overflow. Each sync connector overrides `CreateChangeQueueConfiguration` to set a bound plus an `OverflowHandler` that requests a resync (server-side full-state re-push: OPC UA node re-write, MQTT retained re-publish, WebSocket broadcast snapshot; client-side strategy that does not overwrite pending outbound writes), verified with the ConnectorTester chaos harness.
```

