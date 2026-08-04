# Source Synchronization State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a consumer wait until a named branch of a subject tree is actually synchronized with its external sources, and observe source state per source and per property.

**Architecture:** A `SourceState` on every `ISubjectSource`, driven from `SubjectPropertyWriter` (which every connector reconnect path already calls) rather than from the pump. A `SourceMonitor` context service publishes a typed `SourceEvent` stream with per-subscriber queues, and holds a registration count so waits cannot complete on a partially registered tree. Waits are anchored on a subject and scoped to sources whose `RootSubject` shares a root-to-leaf path with that anchor.

**Tech Stack:** C# 13, .NET 9 (Connectors, Tracking, Hosting), .NET Standard 2.0 (core), xUnit, `PublicApiGenerator` + `Verify` snapshots, BenchmarkDotNet.

**Spec:** `docs/superpowers/specs/2026-07-03-source-sync-state-design.md`. Read it before Task 1. Where this plan and the spec disagree, the spec wins and the plan is wrong.

## Global Constraints

- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Every test body has explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- **No hardcoded waits.** Use `AsyncTestHelpers.WaitUntilAsync(() => condition)`, `ManualResetEventSlim`, or `CountdownEvent`. Never `Task.Delay` or `Thread.Sleep` as synchronization.
- `Directory.Build.props` sets nullable enabled and **warnings as errors**. A build warning fails the build.
- No abbreviations in identifiers: `attribute`, not `attr`.
- Comments explain only the non-obvious. Do not narrate what the code already says.
- No em dashes in any documentation, XML doc comment, or commit message.
- Never put AI attribution in commit messages: no agent names, no `Co-Authored-By`, no "Generated with" footer.
- Public API changes break `VerifyChecksTests.PublicApi` in the affected test project. Accept the new snapshot by copying the test's `.received.txt` over the checked-in `.verified.txt`. Run snapshot loops with `DiffEngine_Disabled=true` so no diff tool launches.
- Build: `dotnet build src/Namotion.Interceptor.slnx`
- Unit tests: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
- Integration tests are per-project and only for Task 12: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
- OPC UA integration tests need port 4840 free. Stop any local Demo.Host app first.
- Priority order when requirements conflict: correctness (thread safety, quiescent consistency, documented semantics) first, then performance (allocations usually outweigh CPU), then idiom.

## File Structure

**Create:**

| Path | Responsibility |
|---|---|
| `src/Namotion.Interceptor.Connectors/SourceState.cs` | The four-value state enum |
| `src/Namotion.Interceptor.Connectors/SourceEvent.cs` | `SourceEventKind` and the `SourceEvent` record struct including `CurrentState` |
| `src/Namotion.Interceptor.Connectors/ISourceStateReporter.cs` | Internal seam letting `SubjectPropertyWriter` drive transitions |
| `src/Namotion.Interceptor.Connectors/SourceMonitor.cs` | Registration, the stream, the registration count, the wait engine |
| `src/Namotion.Interceptor.Connectors/SourceSubscription.cs` | One subscriber: its own queue, its own drain loop, its snapshot |
| `src/Namotion.Interceptor.Connectors/SourceScope.cs` | `IsAncestorOrSelf` and the in-scope predicate |
| `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs` | `WithSourceMonitoring`, `GetSourceMonitor`, `CompleteSourceRegistration`, `DeferWaitCompletion`, `WaitForSynchronizationAsync`, `GetSourceState` |
| `src/Namotion.Interceptor.Connectors/SourceRegistrationGate.cs` | Hosted service that releases the registration count at `ApplicationStarted` |
| `docs/connectors-source-monitoring.md` | The feature page |

**Modify:**

| Path | Change |
|---|---|
| `src/Namotion.Interceptor/PropertyReference.cs` | Add `TryAddPropertyData` |
| `src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs` | Add `GetCurrentValue<TValue>()` |
| `src/Namotion.Interceptor.Connectors/ISubjectSource.cs` | Add `State`, `LastSynchronizedAt`, `PendingWriteCount`, `StateChanged` |
| `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs` | State field, transition lock, terminal guard, pump transitions, registration |
| `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs` | Transitions on `StartBuffering` and `LoadInitialStateAndResumeAsync` |
| `src/Namotion.Interceptor.Connectors/SourcePropertyExtensions.cs` | Claim and release emission, `SetSource` switched to `TryAddPropertyData` |
| `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs` | `WaitForPendingActionsAsync` |
| `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs` | `WaitForPendingHostedServiceActionsAsync` |
| `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` | `ReportConnectionLost()` at keep-alive failure |
| `docs/connectors.md`, `docs/hosting.md`, `docs/tracking.md` | Cross-links and the two new APIs |

**Test files:** `src/Namotion.Interceptor.Tests/PropertyDataTests.cs`, `src/Namotion.Interceptor.Tracking.Tests/Change/CurrentValueTests.cs`, `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`, `SourceMonitorTests.cs`, `SourceEventEmissionTests.cs`, `SourceWaitTests.cs`, `SourceScopeTests.cs`, `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs`, `src/Namotion.Interceptor.OpcUa.Tests/Client/OutageStateTests.cs`.

---

## Task 1: Atomic add-if-absent property data

**Files:**
- Modify: `src/Namotion.Interceptor/PropertyReference.cs` (after `GetOrSetPropertyData`, around line 57)
- Test: `src/Namotion.Interceptor.Tests/PropertyDataTests.cs` (create)
- Modify: `src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: `public bool PropertyReference.TryAddPropertyData(string key, object? value)`. Returns `true` when the key was absent and the value was stored, `false` when a value already existed, which is left untouched. Task 7 uses this to tell a fresh claim from a re-claim atomically.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tests/PropertyDataTests.cs`:

```csharp
using Namotion.Interceptor.Tests.Models;

namespace Namotion.Interceptor.Tests;

public class PropertyDataTests
{
    [Fact]
    public void WhenKeyIsAbsent_ThenTryAddPropertyDataStoresValueAndReturnsTrue()
    {
        // Arrange
        var person = new Person();
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var added = property.TryAddPropertyData("test.key", "first");

        // Assert
        Assert.True(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void WhenKeyIsPresent_ThenTryAddPropertyDataLeavesValueAndReturnsFalse()
    {
        // Arrange
        var person = new Person();
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.TryAddPropertyData("test.key", "first");

        // Act
        var added = property.TryAddPropertyData("test.key", "second");

        // Assert
        Assert.False(added);
        Assert.True(property.TryGetPropertyData("test.key", out var value));
        Assert.Equal("first", value);
    }
}
```

If `Namotion.Interceptor.Tests.Models.Person` does not exist or lacks `FirstName`, use any existing `[InterceptorSubject]` model in that test project instead. Do not create a new model.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PropertyDataTests"
```

Expected: compile error, `'PropertyReference' does not contain a definition for 'TryAddPropertyData'`.

- [ ] **Step 3: Implement**

In `src/Namotion.Interceptor/PropertyReference.cs`, directly after `GetOrSetPropertyData`:

```csharp
    /// <summary>
    /// Adds the property data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, and is the add-if-absent counterpart to
    /// <see cref="TryRemovePropertyData"/>. Use it when the caller must distinguish a first
    /// write from a subsequent one, which <see cref="GetOrSetPropertyData"/> cannot express.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value to store when the key is absent.</param>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present, which is left untouched.</returns>
    public bool TryAddPropertyData(string key, object? value)
    {
        return Subject.Data.TryAdd((Name, key), value);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PropertyDataTests"
```

Expected: 2 passed.

- [ ] **Step 5: Accept the public API snapshot**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PublicApi"
```

Expected: FAIL on first run. Then:

```bash
cp src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tests/VerifyChecksTests.PublicApi.verified.txt
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tests --filter "FullyQualifiedName~PublicApi"
```

Expected: PASS. Confirm the diff added only the `TryAddPropertyData` line.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor/PropertyReference.cs src/Namotion.Interceptor.Tests/
git commit -m "Add PropertyReference.TryAddPropertyData atomic add-if-absent primitive"
```

---

## Task 2: Current value accessor on property changes

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/SubjectPropertyChange.cs` (after `GetNewValue<TValue>()`, around line 105)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/CurrentValueTests.cs` (create)
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: `public TValue GetCurrentValue<TValue>()` on `SubjectPropertyChange`. Nothing else in this plan calls it; it exists because `IPropertyChangeObserver` already instructs consumers to re-read the property and gives them no API for it.

**Note on sequencing:** PR #399 rewrites this file. If #399 has landed, rebase before this task and place the method next to the post-#399 accessors.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/CurrentValueTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class CurrentValueTests
{
    [Fact]
    public void WhenNothingWrittenSinceTheChange_ThenGetCurrentValueEqualsGetNewValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => captured = change);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(captured.GetNewValue<string>(), captured.GetCurrentValue<string>());
        Assert.Equal("Rico", captured.GetCurrentValue<string>());
    }

    [Fact]
    public void WhenPropertyWrittenAgainAfterTheChange_ThenGetCurrentValueReflectsTheLaterWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        SubjectPropertyChange captured = default;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) =>
            {
                if (captured.Property.Subject is null) captured = change;
            });
        person.FirstName = "Rico";

        // Act
        person.FirstName = "Suter";

        // Assert
        Assert.Equal("Rico", captured.GetNewValue<string>());
        Assert.Equal("Suter", captured.GetCurrentValue<string>());
    }
}
```

Use whichever `[InterceptorSubject]` model that test project already provides; do not add one.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CurrentValueTests"
```

Expected: compile error, no definition for `GetCurrentValue`.

- [ ] **Step 3: Implement**

In `SubjectPropertyChange.cs`, after `GetNewValue<TValue>()`:

```csharp
    /// <summary>
    /// Reads the property's value now, rather than the value captured when this change was created.
    /// Deliveries can arrive out of commit order under concurrent writes to the same property, so a
    /// consumer maintaining a derived view must use this instead of <see cref="GetNewValue{TValue}"/>,
    /// which describes one commit and can be superseded by the time it is delivered.
    /// </summary>
    /// <exception cref="InvalidCastException">The current value is not assignable to <typeparamref name="TValue"/>.</exception>
    public TValue GetCurrentValue<TValue>()
    {
        var value = Property.Metadata.GetValue?.Invoke(Property.Subject);
        if (value is TValue typed)
        {
            return typed;
        }

        if (value is null)
        {
            return default!;
        }

        throw new InvalidCastException(
            $"Current value of property '{Property.Name}' is of type '{value.GetType().FullName}' " +
            $"and cannot be cast to '{typeof(TValue).FullName}'.");
    }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~CurrentValueTests"
```

Expected: 2 passed.

- [ ] **Step 5: Accept the Tracking API snapshot**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PublicApi"
cp src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PublicApi"
```

Expected: PASS on the second run.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/ src/Namotion.Interceptor.Tracking.Tests/
git commit -m "Add SubjectPropertyChange.GetCurrentValue for authoritative re-reads"
```

---

## Task 3: Source state enum and per-property derivation

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SourceState.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `public enum SourceState { Unclaimed, Connecting, Synchronized, Stopped }`. Task 4 puts `State` on `ISubjectSource`; Task 3's `GetSourceState` extension is added in Task 4 once `ISubjectSource.State` exists, so this task ships the enum alone.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`:

```csharp
namespace Namotion.Interceptor.Connectors.Tests;

public class SourceStateTests
{
    [Fact]
    public void WhenReadingTheEnum_ThenUnclaimedIsTheDefault()
    {
        // Arrange & Act
        var state = default(SourceState);

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }
}
```

`Unclaimed` must be the zero value so a default-initialised `SourceState` field means "no source", which is what a consumer would expect.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: compile error, type `SourceState` not found.

- [ ] **Step 3: Implement**

Create `src/Namotion.Interceptor.Connectors/SourceState.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The synchronization state of a source, and of a property with respect to its owning source.
/// One enum serves both so the property-level API returns a single coherent type.
/// </summary>
public enum SourceState
{
    /// <summary>No source has claimed the property. Only returned by the property-level API; a source is never Unclaimed.</summary>
    Unclaimed,

    /// <summary>Registered or claimed, but subscribe-read-replay is not complete. Also the state after a detected connection loss, because the connect-and-load phase runs again.</summary>
    Connecting,

    /// <summary>The source completed its initial load procedure. What that guarantees differs per protocol; see the source monitoring documentation.</summary>
    Synchronized,

    /// <summary>The source shut down. Final, a stopped instance is never restarted.</summary>
    Stopped
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/SourceState.cs src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs
git commit -m "Add SourceState enum"
```

---

## Task 4: Source state surface and the transition engine

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SourceEvent.cs`
- Create: `src/Namotion.Interceptor.Connectors/ISourceStateReporter.cs`
- Modify: `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs` (create, `GetSourceState` only)
- Modify: test doubles `ConcurrentTestSource`, `BlockingTestSource` in `src/Namotion.Interceptor.Connectors.Tests/`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`
- Modify: `src/Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt` if that project has one

**Interfaces:**
- Consumes: `SourceState` (Task 3).
- Produces:
  - `SourceEventKind` with seven members: `SourceRegistered`, `SourceUnregistered`, `StateChanged`, `PropertyClaimed`, `PropertyReleased`, `PropertyEnteredView`, `PropertyLeftView`.
  - `readonly record struct SourceEvent(SourceEventKind Kind, ISubjectSource Source, PropertyReference? Property, SourceState OldState, SourceState NewState, DateTimeOffset Timestamp)` with a **computed** `CurrentState` property.
  - `ISubjectSource.State`, `.LastSynchronizedAt`, `.PendingWriteCount`, `event EventHandler<SourceEvent>? StateChanged`.
  - `internal interface ISourceStateReporter { void ReportConnecting(); void ReportSynchronized(); }` implemented by `SubjectSourceBase`. Task 5 calls it from `SubjectPropertyWriter`.
  - `public static SourceState GetSourceState(this PropertyReference property)`.

**Critical:** `CurrentState` must be a computed getter, never `{ get; }`. An auto-property would capture at construction, contradicting its whole purpose, and would add a backing field that joins record-struct equality.

- [ ] **Step 1: Write the failing tests**

Append to `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`:

```csharp
    [Fact]
    public void WhenNoSourceClaimedTheProperty_ThenGetSourceStateReturnsUnclaimed()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Unclaimed, state);
    }

    [Fact]
    public void WhenSourceClaimedTheProperty_ThenGetSourceStateReturnsTheSourcesState()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);

        // Act
        var state = property.GetSourceState();

        // Assert
        Assert.Equal(SourceState.Connecting, state);
    }

    [Fact]
    public void WhenTransitioningToTheSameState_ThenNoEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        var raised = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref raised);

        // Act
        source.ReportConnecting();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void WhenTransitioningToSynchronized_ThenLastSynchronizedAtIsSetBeforeTheEventIsRaised()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        DateTimeOffset? observedInHandler = null;
        source.StateChanged += (_, _) => observedInHandler = source.LastSynchronizedAt;

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
        Assert.NotNull(source.LastSynchronizedAt);
        Assert.Equal(source.LastSynchronizedAt, observedInHandler);
    }

    [Fact]
    public void WhenStopped_ThenNoFurtherTransitionSucceeds()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.ReportSynchronized();
        source.ReportStopped();
        var eventsAfterStop = 0;
        source.StateChanged += (_, _) => Interlocked.Increment(ref eventsAfterStop);
        var timestampAtStop = source.LastSynchronizedAt;

        // Act
        source.ReportConnecting();
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(0, eventsAfterStop);
        Assert.Equal(timestampAtStop, source.LastSynchronizedAt);
    }

    [Fact]
    public void WhenAThrowingHandlerIsSubscribed_ThenTheTransitionStillCompletes()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        source.StateChanged += (_, _) => throw new InvalidOperationException("handler is buggy");

        // Act
        source.ReportSynchronized();

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }
```

Add the test double in the same file:

```csharp
/// <summary>
/// A source that exposes the transition seam directly, so state machine behaviour can be tested
/// without a pump, a network, or a hosted service lifecycle.
/// </summary>
internal class TestStateSource : SubjectSourceBase
{
    public TestStateSource(IInterceptorSubject rootSubject)
        : base(rootSubject.Context, NullLogger.Instance)
    {
        RootSubject = rootSubject;
    }

    public override IInterceptorSubject RootSubject { get; }

    public void ReportConnecting() => ((ISourceStateReporter)this).ReportConnecting();

    public void ReportSynchronized() => ((ISourceStateReporter)this).ReportSynchronized();

    public void ReportStopped() => TransitionTo(SourceState.Stopped);

    /// <summary>How many times the pump body has been entered. Used to prove the terminal guard works.</summary>
    public int ExecuteCount;

    protected override Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExecuteCount);
        return Task.FromResult<IAsyncDisposable?>(null);
    }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => Task.FromResult<Action?>(null);

    public override ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => new(WriteResult.Success(changes));
}
```

If `WriteResult.Success` has a different factory shape, match the shape already used by `ConcurrentTestSource` in that project rather than inventing one.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: compile errors for `GetSourceState`, `State`, `StateChanged`, `ISourceStateReporter`, `TransitionTo`.

- [ ] **Step 3: Add the event types**

Create `src/Namotion.Interceptor.Connectors/SourceEvent.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>The kind of source metadata change a <see cref="SourceEvent"/> reports.</summary>
public enum SourceEventKind
{
    /// <summary>A source registered with the monitor, which happens when it starts.</summary>
    SourceRegistered,

    /// <summary>A source unregistered, which happens when it is disposed.</summary>
    SourceUnregistered,

    /// <summary>A source's own state changed.</summary>
    StateChanged,

    /// <summary>A source took ownership of a property.</summary>
    PropertyClaimed,

    /// <summary>A source gave up ownership of a property.</summary>
    PropertyReleased,

    /// <summary>An already-claimed property joined the tree when its subject attached. Ownership did not change.</summary>
    PropertyEnteredView,

    /// <summary>A still-claimed property left the tree when its subject detached. Ownership did not change.</summary>
    PropertyLeftView
}

/// <summary>
/// A change to source metadata: registration, state, ownership, or tree membership.
/// </summary>
/// <remarks>
/// <see cref="OldState"/> and <see cref="NewState"/> record one transition and must not be applied
/// blindly to a derived view, because events for the same property can be enqueued out of order:
/// the ownership compare-and-set and the enqueue are not atomic. Use <see cref="CurrentState"/>.
/// </remarks>
public readonly record struct SourceEvent(
    SourceEventKind Kind,
    ISubjectSource Source,
    PropertyReference? Property,
    SourceState OldState,
    SourceState NewState,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// The authoritative state for this event's subject, read now rather than captured when the
    /// event was created. This is what a consumer maintaining a derived view applies.
    /// </summary>
    /// <remarks>
    /// For <see cref="SourceEventKind.StateChanged"/> this is the SOURCE's state and says nothing
    /// about any individual property; a consumer updating properties on a state change must call
    /// <see cref="SourceMonitoringExtensions.GetSourceState"/> per property instead.
    /// Not cached: each access performs a property-data lookup and a volatile read, so hoist it to
    /// a local if you read it more than once.
    /// </remarks>
    public SourceState CurrentState => ResolveCurrentState();

    private SourceState ResolveCurrentState()
    {
        if (Property is null)
        {
            return Source.State;
        }

        return Property.Value.GetSourceState();
    }
}
```

Task 7 replaces `ResolveCurrentState` with the topology-aware version. Leaving it ownership-only here keeps this task independently testable.

- [ ] **Step 4: Add the reporter seam**

Create `src/Namotion.Interceptor.Connectors/ISourceStateReporter.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The seam through which <see cref="SubjectPropertyWriter"/> drives a source's connection state.
/// Internal because state reporting is the base class's responsibility, not part of the source contract.
/// </summary>
internal interface ISourceStateReporter
{
    /// <summary>Reports that the source is connecting or reconnecting and its live feed is not trusted.</summary>
    void ReportConnecting();

    /// <summary>Reports that the source completed its initial load procedure.</summary>
    void ReportSynchronized();
}
```

- [ ] **Step 5: Extend the source contract**

In `src/Namotion.Interceptor.Connectors/ISubjectSource.cs`, add to the interface:

```csharp
    /// <summary>
    /// Gets the source's synchronization state. Describes the inbound direction only: the model
    /// mirroring the external system. Outbound backlog is <see cref="PendingWriteCount"/>.
    /// </summary>
    SourceState State { get; }

    /// <summary>
    /// Gets when the most recent initial synchronization completed, or <c>null</c> if it never has.
    /// While <see cref="SourceState.Connecting"/> after a drop, this is how a dashboard says
    /// "stale, last confirmed at T".
    /// </summary>
    DateTimeOffset? LastSynchronizedAt { get; }

    /// <summary>
    /// Gets the number of writes currently queued for retry. Orthogonal to <see cref="State"/>:
    /// this queue can be non-empty during entirely normal synchronized operation.
    /// </summary>
    int PendingWriteCount { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    /// <remarks>
    /// Raised synchronously on the transitioning thread and inside the source's transition lock.
    /// Handlers MUST be observe-only: they must not block, and must not cause a transition of any
    /// source, directly or indirectly, because the lock is reentrant and a nested transition would
    /// publish out of order. Mutating consumers belong on the SourceMonitor stream, where delivery
    /// is queued and outside all locks.
    /// </remarks>
    event EventHandler<SourceEvent>? StateChanged;
```

- [ ] **Step 6: Implement the transition engine**

In `SubjectSourceBase.cs`, change the class declaration to add the seam and add the members. `PendingWriteCount` already exists at line 28 and now satisfies the interface, so leave it.

```csharp
public abstract class SubjectSourceBase : BackgroundService, ISubjectSource, ISourceStateReporter
{
    private readonly Lock _stateLock = new();
    private int _state = (int)SourceState.Connecting;
    private long _lastSynchronizedTicks;

    /// <inheritdoc />
    public SourceState State => (SourceState)Volatile.Read(ref _state);

    /// <inheritdoc />
    public DateTimeOffset? LastSynchronizedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSynchronizedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public event EventHandler<SourceEvent>? StateChanged;

    void ISourceStateReporter.ReportConnecting() => TransitionTo(SourceState.Connecting);

    void ISourceStateReporter.ReportSynchronized() => TransitionTo(SourceState.Synchronized);

    /// <summary>
    /// Moves to <paramref name="newState"/> and publishes the change, or does nothing when the
    /// transition is a no-op or the source has already stopped.
    /// </summary>
    /// <remarks>
    /// The state change, the timestamp write, and the event raise are all inside one lock. A bare
    /// compare-exchange is not enough: a writer could set Synchronized, be preempted, let disposal
    /// set Stopped and unregister, then resume and publish Synchronized after Stopped. Both
    /// compare-exchanges would have succeeded, so no stickiness rule can prevent it.
    /// </remarks>
    protected bool TransitionTo(SourceState newState)
    {
        lock (_stateLock)
        {
            var oldState = (SourceState)_state;
            if (oldState == newState || oldState == SourceState.Stopped)
            {
                return false;
            }

            _state = (int)newState;

            if (newState == SourceState.Synchronized)
            {
                Interlocked.Exchange(ref _lastSynchronizedTicks, DateTimeOffset.UtcNow.UtcTicks);
            }

            var sourceEvent = new SourceEvent(
                SourceEventKind.StateChanged, this, null, oldState, newState, DateTimeOffset.UtcNow);

            try
            {
                StateChanged?.Invoke(this, sourceEvent);
            }
            catch (Exception exception)
            {
                // A buggy Synchronized handler must not be mistaken for a source failure, or the
                // source would be flipped back to Connecting and loop forever.
                _logger.LogError(exception, "A StateChanged handler threw and was ignored.");
            }

            return true;
        }
    }
}
```

`_logger` is already a private field on this class; if it is not accessible from the new code, widen it to `private protected` rather than adding a second logger.

- [ ] **Step 7: Add the per-property extension**

Create `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs`:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>Consumer-facing entry points for source monitoring.</summary>
public static class SourceMonitoringExtensions
{
    /// <summary>
    /// Gets the property's synchronization state, derived from its owning source with no per-property storage.
    /// </summary>
    /// <remarks>
    /// Only fully meaningful once the branch containing the property has been awaited through
    /// WaitForSynchronizationAsync: before claiming has happened, Unclaimed cannot be distinguished
    /// from not-yet-claimed. After a claim it reports Connecting, so "will sync, still loading" is
    /// already distinguishable from "no source" even before the wait completes.
    /// </remarks>
    public static SourceState GetSourceState(this PropertyReference property)
    {
        return property.TryGetSource(out var source) ? source.State : SourceState.Unclaimed;
    }
}
```

- [ ] **Step 8: Update the test doubles**

`ConcurrentTestSource` and `BlockingTestSource` implement `ISubjectSource` directly and will not compile. Add to each:

```csharp
    public SourceState State { get; private set; } = SourceState.Connecting;

    public DateTimeOffset? LastSynchronizedAt { get; private set; }

    public int PendingWriteCount => 0;

    public event EventHandler<SourceEvent>? StateChanged;

    /// <summary>Test hook: drives the state the way a real source's pump would.</summary>
    public void SetState(SourceState state)
    {
        var oldState = State;
        if (oldState == state)
        {
            return;
        }

        State = state;
        if (state == SourceState.Synchronized)
        {
            LastSynchronizedAt = DateTimeOffset.UtcNow;
        }

        StateChanged?.Invoke(this, new SourceEvent(
            SourceEventKind.StateChanged, this, null, oldState, state, DateTimeOffset.UtcNow));
    }
```

- [ ] **Step 9: Run the tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: build clean with no warnings, 7 passed.

- [ ] **Step 10: Accept the Connectors API snapshot and commit**

```bash
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~PublicApi"
```

If that test exists, copy `.received.txt` over `.verified.txt` and rerun until it passes. Then:

```bash
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Add source state surface with a serialized transition engine

Stopped is terminal in the transition helper, and the state change, timestamp
write and event raise all happen inside one lock, because two successful
compare-exchanges could otherwise publish Synchronized after Stopped."
```

---

## Task 5: Transitions driven by the property writer and the pump

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SubjectPropertyWriter.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceStateTests.cs`

**Interfaces:**
- Consumes: `ISourceStateReporter`, `TransitionTo`, `SourceState` (Task 4).
- Produces: `public void ReportConnectionLost()` on `SubjectSourceBase`, called by connectors that detect an outage before they buffer. Task 12 calls it from OPC UA.

**Why this placement, do not "simplify" it:** the built-in clients handle ordinary reconnects themselves and call `StartBuffering` and `LoadInitialStateAndResumeAsync` from five sites **outside** `ExecuteAsync`, while the pump sits in `processor.ProcessAsync`: `MqttSubjectClientSource:125,549`, `WebSocketSubjectClientSource:598,629,659`, `OpcUaSubjectClientSource:458,491`, `SessionManager:77,80,611`. Putting the transitions on the pump reports `Synchronized` straight through every real outage.

- [ ] **Step 1: Write the failing tests**

Append to `SourceStateTests.cs`:

```csharp
    [Fact]
    public async Task WhenBufferingStartsOutsideThePump_ThenTheSourceReportsConnecting()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        ((ISourceStateReporter)source).ReportSynchronized();

        // Act
        writer.StartBuffering();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenTheInitialLoadCompletesOutsideThePump_ThenTheSourceReportsSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new TestStateSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act
        await writer.LoadInitialStateAndResumeAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceState.Synchronized, source.State);
    }

    [Fact]
    public async Task WhenTheInitialLoadThrows_ThenTheSourceDoesNotReportSynchronized()
    {
        // Arrange
        var person = new Person();
        var source = new ThrowingLoadSource(person);
        var writer = new SubjectPropertyWriter(source, NullLogger.Instance);
        writer.StartBuffering();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.LoadInitialStateAndResumeAsync(CancellationToken.None));
        Assert.Equal(SourceState.Connecting, source.State);
    }

    [Fact]
    public void WhenAConnectorDetectsLossBeforeBuffering_ThenReportConnectionLostTransitions()
    {
        // Arrange
        var source = new TestStateSource(new Person());
        ((ISourceStateReporter)source).ReportSynchronized();

        // Act
        source.ReportConnectionLost();

        // Assert
        Assert.Equal(SourceState.Connecting, source.State);
    }
```

Add the throwing double to the same file:

```csharp
internal sealed class ThrowingLoadSource : TestStateSource
{
    public ThrowingLoadSource(IInterceptorSubject rootSubject) : base(rootSubject) { }

    public override Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("load failed");
}
```

`TestStateSource` is already declared non-sealed in Task 4 for exactly this. `LoadInitialStateAsync` is an `override` of an `abstract` member, so it is virtual and can be overridden again without any change.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: the four new tests fail; `ReportConnectionLost` does not compile.

- [ ] **Step 3: Drive transitions from the writer**

In `SubjectPropertyWriter.cs`, in `StartBuffering`, after the existing body:

```csharp
    public void StartBuffering()
    {
        lock (_lock)
        {
            _updates = [];
        }

        // Buffering starts exactly when the source has stopped trusting its live feed, on first
        // connect and on every reconnect, including reconnects the base pump never sees.
        (_source as ISourceStateReporter)?.ReportConnecting();
    }
```

In `LoadInitialStateAndResumeAsync`, report on every normal completion, including the concurrent-replay early return, which also means state has been loaded and replayed. A throwing load propagates and does not report. The early return is inside the lock, so restructure to report after the lock rather than duplicating the call:

```csharp
    public async Task LoadInitialStateAndResumeAsync(CancellationToken cancellationToken)
    {
        var applyAction = await _source.LoadInitialStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            applyAction?.Invoke();

            var updates = _updates;
            if (updates is null)
            {
                _logger.LogDebug("LoadInitialStateAndResumeAsync called but updates already replayed by concurrent reconnection.");
            }
            else
            {
                // ... existing replay loop and _updates = null, unchanged ...
            }
        }

        (_source as ISourceStateReporter)?.ReportSynchronized();
    }
```

Keep every existing line of the replay loop exactly as it is; only the control flow around it changes so both paths fall through to one report.

- [ ] **Step 4: Add the loss-detection hook and the pump transitions**

In `SubjectSourceBase.cs`:

```csharp
    /// <summary>
    /// Reports that the connection was lost, for connectors that detect an outage before they
    /// start buffering.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="SubjectPropertyWriter.StartBuffering"/>: calling that
    /// at detection time would replace the buffer with a fresh list, and the later StartBuffering
    /// on the reconnect path would then discard everything buffered in between. That would change
    /// data-path behaviour in order to fix a reporting bug.
    /// </remarks>
    public void ReportConnectionLost() => TransitionTo(SourceState.Connecting);
```

In `ExecuteAsync`, add the pump lifecycle transitions. The catch fires for failures that escape the connector's own handling, out of `StartListeningAsync`, `LoadInitialStateAndResumeAsync`, `ReapplyRetryQueue` or `ProcessAsync`:

```csharp
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TransitionTo(SourceState.Connecting);   // no-op in practice, keeps the invariant local
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // ... existing body unchanged ...
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    TransitionTo(SourceState.Connecting);
                    _logger.LogError(ex, "Failed to listen for changes in source.");
                    // ... existing retry delay unchanged ...
                }
            }
        }
        finally
        {
            TransitionTo(SourceState.Stopped);
        }
    }
```

- [ ] **Step 5: Run the tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceStateTests"
```

Expected: 11 passed, build clean.

- [ ] **Step 6: Run the whole unit suite**

```bash
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: all pass. Existing source tests now run with transitions active, so a regression here means the transitions are firing where they should not.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Drive source state from SubjectPropertyWriter rather than the pump

Connectors handle ordinary reconnects themselves and call StartBuffering and
LoadInitialStateAndResumeAsync from five sites outside ExecuteAsync while the
pump sits in ProcessAsync, so pump-placed transitions would report Synchronized
through every real outage. Adds ReportConnectionLost for connectors that detect
loss before they buffer."
```

---

## Task 6: The monitor, its registration, and its stream

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SourceMonitor.cs`
- Create: `src/Namotion.Interceptor.Connectors/SourceSubscription.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SubjectSourceBase.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceMonitorTests.cs` (create)

**Interfaces:**
- Consumes: `SourceEvent`, `SourceEventKind`, `ISubjectSource.StateChanged` (Task 4).
- Produces:
  - `public class SourceMonitor` with `IReadOnlyList<ISubjectSource> Sources`, `SourceSubscription Subscribe(Action<SourceEvent> handler)`, `void Register(ISubjectSource)`, `void Unregister(ISubjectSource)`, and `internal void Publish(in SourceEvent)`.
  - `public sealed class SourceSubscription : IDisposable` with `ImmutableArray<ISubjectSource> Sources`.
  - `public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)`.
  - `public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)`.
  - `internal static ImmutableArray<SourceMonitor> GetSourceMonitors(this IInterceptorSubjectContext context)`.

**Delivery contract:** each subscription owns its queue and its own on-demand drain. A slow handler delays only itself, and a queue created empty cannot contain events from before it existed, which is why no sequence stamping is needed.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceMonitorTests.cs`:

```csharp
using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceMonitorTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenASourceRegisters_ThenSubscribersReceiveSourceRegistered()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var source = new TestStateSource(new Person(context));

        // Act
        monitor.Register(source);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
        Assert.Contains(source, monitor.Sources);
    }

    [Fact]
    public async Task WhenARegisteredSourceTransitions_ThenTheMonitorForwardsStateChanged()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        source.ReportSynchronized();

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.StateChanged && e.NewState == SourceState.Synchronized));
    }

    [Fact]
    public async Task WhenASourceUnregisters_ThenItsLaterTransitionsAreNotForwarded()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        monitor.Register(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Unregister(source);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceUnregistered));
        source.ReportSynchronized();

        // Assert
        Assert.DoesNotContain(received, e => e.Kind == SourceEventKind.StateChanged);
        Assert.DoesNotContain(source, monitor.Sources);
    }

    [Fact]
    public void WhenRegisteringTwice_ThenTheSecondRegistrationEmitsNothing()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(source);
        monitor.Register(source);

        // Assert
        Assert.Single(monitor.Sources);
    }

    [Fact]
    public void WhenUnregisteringAnUnknownSource_ThenNothingHappens()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var source = new TestStateSource(new Person(context));

        // Act & Assert
        monitor.Unregister(source);
        Assert.Empty(monitor.Sources);
    }

    [Fact]
    public async Task WhenOneHandlerThrows_ThenOtherSubscribersStillReceiveEvents()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var throwing = monitor.Subscribe(_ => throw new InvalidOperationException("subscriber is buggy"));
        using var healthy = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }

    [Fact]
    public async Task WhenOneHandlerIsSlow_ThenAnotherSubscriberIsNotDelayed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var release = new ManualResetEventSlim(false);
        var fastReceived = new ManualResetEventSlim(false);
        using var slow = monitor.Subscribe(_ => release.Wait(TimeSpan.FromSeconds(30)));
        using var fast = monitor.Subscribe(_ => fastReceived.Set());

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        Assert.True(fastReceived.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenSubscribing_ThenTheSnapshotPlusDeliveredEventsSeeEachChangeOnce()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var before = new TestStateSource(new Person(context));
        monitor.Register(before);
        var received = new ConcurrentQueue<SourceEvent>();

        // Act
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var after = new TestStateSource(new Person(context));
        monitor.Register(after);

        // Assert
        Assert.Contains(before, subscription.Sources);
        Assert.DoesNotContain(after, subscription.Sources);
        await AsyncTestHelpers.WaitUntilAsync(() =>
            received.Any(e => e.Kind == SourceEventKind.SourceRegistered && ReferenceEquals(e.Source, after)));
        Assert.DoesNotContain(received, e => ReferenceEquals(e.Source, before));
    }

    [Fact]
    public void WhenNoMonitorIsConfigured_ThenGetSourceMonitorThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.GetSourceMonitor());
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }

    [Fact]
    public void WhenTwoMonitorsAreReachable_ThenGetSourceMonitorThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithLifecycle().WithSourceMonitoring();
        var child = InterceptorSubjectContext.Create().WithSourceMonitoring();
        child.AddFallbackContext(parent);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => child.GetSourceMonitor());
        Assert.Contains("GetServices", exception.Message);
    }

    [Fact]
    public async Task WhenAStoppedSourceIsStartedAgain_ThenThePumpDoesNotRun()
    {
        // Arrange
        var context = CreateContext();
        var source = new TestStateSource(new Person(context));
        await source.StartAsync(CancellationToken.None);
        await source.StopAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Stopped);

        // Act
        await source.StartAsync(CancellationToken.None);

        // Assert
        // BackgroundService.StartAsync builds a FRESH linked CancellationTokenSource on every call
        // and StopAsync cancels only the previous one, so without the guard ExecuteAsync would run
        // a second time against an uncancelled token while State stayed pinned at Stopped.
        Assert.Equal(SourceState.Stopped, source.State);
        Assert.Equal(1, source.ExecuteCount);
    }

    [Fact]
    public void WhenASubjectIsAttachedToASecondTree_ThenOnlyTheFirstTreesMonitorIsReachable()
    {
        // Arrange
        var firstTree = CreateContext();
        var secondTree = CreateContext();
        var firstRoot = new Person(firstTree);
        var secondRoot = new Person(secondTree);
        var shared = new Person();
        firstRoot.Mother = shared;

        // Act
        secondRoot.Mother = shared;

        // Assert
        // Characterization, not aspiration: ContextInheritanceHandler adds a parent fallback only on
        // the FIRST attach ({ ReferenceCount: 1, IsContextAttach: true }), so the second tree's
        // monitor never becomes reachable and this design claims no multi-tree coverage. If context
        // inheritance ever starts tracking every parent, this test fails and the limitation, the
        // topology-aware CurrentState, and the docs all need revisiting together.
        var reachable = ((IInterceptorSubject)shared).Context.GetServices<SourceMonitor>();
        Assert.Single(reachable);
        Assert.Same(firstTree.GetSourceMonitor(), reachable[0]);
    }

    [Fact]
    public async Task WhenNoLifecycleTrackingIsConfigured_ThenTheMonitorStillWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithSourceMonitoring();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        monitor.Register(new TestStateSource(new Person(context)));

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.SourceRegistered));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceMonitorTests"
```

Expected: compile errors, no `SourceMonitor`, `WithSourceMonitoring`, `GetSourceMonitor`.

- [ ] **Step 3: Implement the subscription**

Create `src/Namotion.Interceptor.Connectors/SourceSubscription.cs`:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// One subscriber to the source event stream, with its own queue and its own drain.
/// </summary>
/// <remarks>
/// Per-subscriber queues mean a slow handler delays only itself. They also remove the need for
/// sequence stamping: this queue is created empty, so it cannot contain events enqueued before the
/// subscription existed. Pair it with <see cref="Sources"/>, captured atomically with the
/// subscription, to observe every change exactly once.
/// </remarks>
public sealed class SourceSubscription : IDisposable
{
    private readonly ConcurrentQueue<SourceEvent> _queue = new();
    private readonly Action<SourceEvent> _handler;
    private readonly Action<SourceSubscription> _onDisposed;
    private readonly ILogger? _logger;

    private int _draining;
    private volatile bool _disposed;

    internal SourceSubscription(
        Action<SourceEvent> handler,
        ImmutableArray<ISubjectSource> sources,
        Action<SourceSubscription> onDisposed,
        ILogger? logger)
    {
        _handler = handler;
        Sources = sources;
        _onDisposed = onDisposed;
        _logger = logger;
    }

    /// <summary>
    /// The sources registered at the moment this subscription was created, captured atomically with
    /// it. Reading SourceMonitor.Sources separately after subscribing is not race-free: a source
    /// registering between the two calls appears in both, and a naive consumer double-counts it.
    /// </summary>
    public ImmutableArray<ISubjectSource> Sources { get; }

    internal void Enqueue(in SourceEvent sourceEvent)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(sourceEvent);

        // Single-flight: one drain at a time, and it exits when the queue is empty, so an idle
        // subscription owns no task.
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            _ = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        do
        {
            while (_queue.TryDequeue(out var sourceEvent))
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _handler(sourceEvent);
                }
                catch (Exception exception)
                {
                    _logger?.LogError(exception, "A source event handler threw and was ignored.");
                }
            }

            Volatile.Write(ref _draining, 0);
        }
        while (!_queue.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _onDisposed(this);
    }
}
```

- [ ] **Step 4: Implement the monitor**

Create `src/Namotion.Interceptor.Connectors/SourceMonitor.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The per-tree registry of sources, the source event stream, and the synchronization waits.
/// Added to the tree root context by WithSourceMonitoring.
/// </summary>
public class SourceMonitor
{
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    private ImmutableArray<ISubjectSource> _sources = [];
    private ImmutableArray<SourceSubscription> _subscriptions = [];

    /// <summary>Creates a monitor. Prefer WithSourceMonitoring over calling this directly.</summary>
    public SourceMonitor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public IReadOnlyList<ISubjectSource> Sources => _sources;

    /// <summary>True when at least one public subscriber exists. Gates the attach and detach catch-up scan.</summary>
    internal bool HasSubscribers => !_subscriptions.IsEmpty;

    /// <summary>Subscribes to the stream and captures the source snapshot atomically with the subscription.</summary>
    public SourceSubscription Subscribe(Action<SourceEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var subscription = new SourceSubscription(handler, _sources, Remove, _logger);
            _subscriptions = _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void Remove(SourceSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions = _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Registers a source. Idempotent.</summary>
    public void Register(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (_sources.Contains(source))
            {
                return;
            }

            _sources = _sources.Add(source);
            source.StateChanged += OnSourceStateChanged;
        }

        Publish(new SourceEvent(
            SourceEventKind.SourceRegistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
    }

    /// <summary>Unregisters a source. A no-op for a source that was never registered.</summary>
    public void Unregister(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (!_sources.Contains(source))
            {
                return;
            }

            _sources = _sources.Remove(source);
            source.StateChanged -= OnSourceStateChanged;
        }

        Publish(new SourceEvent(
            SourceEventKind.SourceUnregistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
    }

    private void OnSourceStateChanged(object? sender, SourceEvent sourceEvent) => Publish(sourceEvent);

    /// <summary>Enqueues an event onto every subscriber's own queue.</summary>
    internal void Publish(in SourceEvent sourceEvent)
    {
        var subscriptions = _subscriptions;
        foreach (var subscription in subscriptions)
        {
            subscription.Enqueue(sourceEvent);
        }
    }
}
```

`OnSourceStateChanged` runs inside the source's transition lock, so it must only enqueue. It does.

- [ ] **Step 5: Add configuration and resolution**

Append to `SourceMonitoringExtensions.cs`:

```csharp
    /// <summary>
    /// Adds source monitoring to this context. Call it on the TREE ROOT context: a service added to
    /// a subtree context is invisible to the root and to sibling subtrees, because context fallbacks
    /// point child to parent and never sideways, so a subtree-placed monitor fragments the tree.
    /// Implies WithParents, which the branch-scoped wait needs.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)
    {
        context.TryAddService(() => new SourceMonitor(), _ => true);
        return context.WithParents();
    }

    /// <summary>
    /// Resolves the single reachable monitor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable, or more than one is.</exception>
    public static SourceMonitor GetSourceMonitor(this IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        return monitors.Length switch
        {
            1 => monitors[0],
            0 => throw new InvalidOperationException(
                "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context."),
            _ => throw new InvalidOperationException(
                $"{monitors.Length} SourceMonitor instances are reachable from this context. " +
                "Combining them is a decision for the call site: use GetServices<SourceMonitor>() and choose explicitly.")
        };
    }

    internal static ImmutableArray<SourceMonitor> GetSourceMonitors(this IInterceptorSubjectContext context)
    {
        return context.GetServices<SourceMonitor>();
    }
```

Add `using System.Collections.Immutable;` and `using Namotion.Interceptor.Tracking;` at the top.

- [ ] **Step 6: Register from the source lifecycle**

In `SubjectSourceBase.cs`:

```csharp
    private ImmutableArray<SourceMonitor> _registeredMonitors = [];

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Stopped is terminal, and the platform will not enforce it: BackgroundService.StartAsync
        // creates a fresh linked CancellationTokenSource on every call, so a second StartAsync on
        // the same instance would run ExecuteAsync again against an uncancelled token. Without this
        // guard such a source would claim, load and apply live values while State stayed Stopped.
        if (State == SourceState.Stopped)
        {
            _logger.LogWarning(
                "Source {Source} was stopped and cannot be restarted. Create a new instance instead.",
                GetType().Name);
            return Task.CompletedTask;
        }

        // Registration precedes the pump so SourceRegistered precedes any StateChanged of this source.
        _registeredMonitors = RootSubject.Context.GetSourceMonitors();
        foreach (var monitor in _registeredMonitors)
        {
            monitor.Register(this);
        }

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Publish the final Stopped while still registered, so a dispose without a stop is not silent.
        TransitionTo(SourceState.Stopped);

        foreach (var monitor in _registeredMonitors)
        {
            monitor.Unregister(this);
        }
        _registeredMonitors = [];

        WriteRetryQueue?.Dispose();
        base.Dispose();
    }
```

- [ ] **Step 7: Run the tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceMonitorTests"
```

Expected: 13 passed.

- [ ] **Step 8: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Add SourceMonitor with per-subscriber delivery queues

Each subscription owns its queue and its own on-demand drain, so a slow handler
delays only itself, and a queue created empty cannot hold events from before the
subscription existed, which removes the need for sequence stamping."
```

---

## Task 7: Ownership emission and the topology-aware current state

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SourcePropertyExtensions.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceEvent.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceEventEmissionTests.cs` (create)

**Interfaces:**
- Consumes: `TryAddPropertyData` (Task 1), `SourceMonitor.Publish`, `GetSourceMonitors` (Task 6).
- Produces: `SetSource` and `RemoveSource` publish `PropertyClaimed` and `PropertyReleased` on actual ownership transitions only. `SourceEvent.CurrentState` becomes topology-aware.

**Why emission lives on the primitives:** the documented contract has sources calling `SetSource(this)` directly and the ownership API is public, so emitting from `SourceOwnershipManager` would leave silent claims and an untrustworthy stream.

**Why `CurrentState` must be topology-aware:** a claim can commit and capture its monitors, the subject can then detach and the scan emit `PropertyLeftView`, and only then is the delayed claim enqueued. Delivered last, a plain ownership read returns the still-owning source and permanently undoes the release, because detach deliberately leaves ownership intact.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceEventEmissionTests.cs`:

```csharp
using System.Collections.Concurrent;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceEventEmissionTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public async Task WhenAPropertyIsClaimed_ThenPropertyClaimedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyClaimed));
        var claimEvent = received.First(e => e.Kind == SourceEventKind.PropertyClaimed);
        Assert.Equal(SourceState.Unclaimed, claimEvent.OldState);
        Assert.Equal(SourceState.Connecting, claimEvent.NewState);
    }

    [Fact]
    public async Task WhenTheSameSourceReclaims_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(source);

        // Assert
        Assert.True(claimed);
        await Task.Yield();
        Assert.Empty(received);
    }

    [Fact]
    public async Task WhenADifferentSourceClaimsAnOwnedProperty_ThenTheClaimIsRejectedAndNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(person));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var claimed = property.SetSource(new TestStateSource(person));

        // Assert
        Assert.False(claimed);
        await Task.Yield();
        Assert.Empty(received);
    }

    [Fact]
    public async Task WhenOwnershipIsActuallyRemoved_ThenPropertyReleasedIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        property.SetSource(source);
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        var removed = property.RemoveSource(source);

        // Assert
        Assert.True(removed);
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyReleased));
        var releaseEvent = received.First(e => e.Kind == SourceEventKind.PropertyReleased);
        Assert.Equal(SourceState.Unclaimed, releaseEvent.NewState);
    }

    [Fact]
    public void WhenNoMonitorIsReachable_ThenClaimingDoesNotThrowAndPublishesNothing()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert
        Assert.True(property.SetSource(new TestStateSource(person)));
    }

    [Fact]
    public void WhenTheSubjectIsStillAttached_ThenCurrentStateReadsThroughToTheSource()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source = new TestStateSource(person);
        source.ReportSynchronized();
        property.SetSource(source);
        var sourceEvent = new SourceEvent(
            SourceEventKind.PropertyClaimed, source, property,
            SourceState.Unclaimed, SourceState.Synchronized, DateTimeOffset.UtcNow) { Monitor = monitor };

        // Act
        var current = sourceEvent.CurrentState;

        // Assert
        Assert.Equal(SourceState.Synchronized, current);
    }
}
```

The last test uses an init-only `Monitor` member on `SourceEvent`; add it in Step 3.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceEventEmissionTests"
```

Expected: failures, and a compile error for `Monitor`.

- [ ] **Step 3: Make CurrentState topology-aware**

In `SourceEvent.cs`, add the internal monitor reference and rewrite the resolver:

```csharp
    /// <summary>
    /// The monitor this event was published to. Used to decide whether the property is still inside
    /// that monitor's tree. Internal: consumers reach the monitor through the context.
    /// </summary>
    internal SourceMonitor? Monitor { get; init; }

    private SourceState ResolveCurrentState()
    {
        if (Property is null)
        {
            return Source.State;
        }

        var property = Property.Value;

        // A property whose subject has left this monitor's tree has no state within it, whatever the
        // ownership data still says. Detach deliberately leaves ownership intact, so without this a
        // claim delivered after a detach would permanently undo the release.
        if (Monitor is not null && !property.Subject.Context.GetSourceMonitors().Contains(Monitor))
        {
            return SourceState.Unclaimed;
        }

        return property.GetSourceState();
    }
```

This works because `ContextInheritanceHandler` removes the parent fallback on the last detach (`{ ReferenceCount: 0, IsPropertyReferenceRemoved: true }`), so the monitor genuinely stops resolving from a detached subtree. A subject constructed directly against the root context rather than inheriting it never loses resolution; that subject is not detachable in the relevant sense, and the documentation says so.

- [ ] **Step 4: Emit from the ownership primitives**

Rewrite `SetSource` and `RemoveSource` in `SourcePropertyExtensions.cs`:

```csharp
    public static bool SetSource(this PropertyReference property, ISubjectSource source)
    {
        // TryAddPropertyData rather than GetOrSetPropertyData: only the atomic add-if-absent tells a
        // fresh claim from a re-claim, and the stream must publish exactly the real transitions.
        if (property.TryAddPropertyData(SourceKey, source))
        {
            PublishOwnershipChange(property, source, SourceEventKind.PropertyClaimed,
                SourceState.Unclaimed, source.State);
            return true;
        }

        return property.TryGetPropertyData(SourceKey, out var existing) && ReferenceEquals(existing, source);
    }

    public static bool RemoveSource(this PropertyReference property, ISubjectSource expectedSource)
    {
        if (!property.TryRemovePropertyData(SourceKey, expectedSource))
        {
            return false;
        }

        PublishOwnershipChange(property, expectedSource, SourceEventKind.PropertyReleased,
            expectedSource.State, SourceState.Unclaimed);
        return true;
    }

    private static void PublishOwnershipChange(
        PropertyReference property, ISubjectSource source, SourceEventKind kind,
        SourceState oldState, SourceState newState)
    {
        // Usually length 0 or 1 and cached on the context's copy-on-write state snapshot, so a tree
        // without monitoring pays one array check per claim and nothing else.
        var monitors = property.Subject.Context.GetSourceMonitors();
        if (monitors.IsEmpty)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        foreach (var monitor in monitors)
        {
            monitor.Publish(new SourceEvent(kind, source, property, oldState, newState, timestamp)
            {
                Monitor = monitor
            });
        }
    }
```

Add `using Namotion.Interceptor.Connectors;` if needed and keep `SourceKey` unchanged.

- [ ] **Step 5: Run the tests**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests
```

Expected: all pass, including the existing `SourceOwnershipManagerTests`, whose releases now go through the publishing `RemoveSource`.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Publish ownership changes from SetSource and RemoveSource

Emission sits on the primitives because the documented contract has sources
calling SetSource directly, so emitting from SourceOwnershipManager would leave
silent claims. CurrentState becomes topology-aware so a claim delivered after a
detach cannot undo the release."
```

---

## Task 8: View catch-up on attach and detach

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceEventEmissionTests.cs`

**Interfaces:**
- Consumes: `ILifecycleHandler`, `SubjectLifecycleChange`, `SourceMonitor.Publish`, `HasSubscribers` (Task 6).
- Produces: `SourceMonitor` implements `ILifecycleHandler` and publishes `PropertyEnteredView` and `PropertyLeftView`.

**Hook choice matters:** hook `ILifecycleHandler.HandleLifecycleChange`, **not** `LifecycleInterceptor.SubjectDetaching`. `LifecycleInterceptor` raises `SubjectDetaching` (which `SourceOwnershipManager` uses, `SourceOwnershipManager.cs:52`) **before** invoking lifecycle handlers, so the manager's releases are guaranteed to precede this scan and the no-duplicate property holds by construction rather than by accident.

- [ ] **Step 1: Write the failing tests**

Append to `SourceEventEmissionTests.cs`:

```csharp
    [Fact]
    public async Task WhenAClaimedSubjectAttaches_ThenPropertyEnteredViewIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(root));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = child;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyEnteredView));
    }

    [Fact]
    public async Task WhenAStillClaimedSubjectDetaches_ThenPropertyLeftViewReportsUnclaimed()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var property = new PropertyReference(child, nameof(Person.FirstName));
        property.SetSource(new TestStateSource(root));
        var received = new ConcurrentQueue<SourceEvent>();
        using var subscription = monitor.Subscribe(sourceEvent => received.Enqueue(sourceEvent));

        // Act
        root.Mother = null;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => received.Any(e => e.Kind == SourceEventKind.PropertyLeftView));
        var leftEvent = received.First(e => e.Kind == SourceEventKind.PropertyLeftView);
        Assert.Equal(SourceState.Unclaimed, leftEvent.CurrentState);
        // Ownership is deliberately left intact so a re-attached subject still reaches its source.
        Assert.True(property.TryGetSource(out _));
    }

    [Fact]
    public void WhenThereAreNoSubscribers_ThenTheCatchUpScanIsSkipped()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        new PropertyReference(child, nameof(Person.FirstName)).SetSource(new TestStateSource(root));

        // Act & Assert
        root.Mother = child;
        root.Mother = null;
    }
```

Use whichever subject-valued property the test model actually has instead of `Mother` if it differs.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceEventEmissionTests"
```

Expected: the two new event tests fail, nothing is published.

- [ ] **Step 3: Implement the scan**

In `SourceMonitor.cs`, implement `ILifecycleHandler`:

```csharp
public class SourceMonitor : ILifecycleHandler
{
    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // The recently optimized attach and detach hot paths pay one flag check when nobody is
        // listening. Pending waits deliberately do not count as subscribers: a wait is active during
        // startup, exactly when attach storms happen, and never needs property events.
        if (!HasSubscribers)
        {
            return;
        }

        if (change.IsContextAttach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyEnteredView);
        }
        else if (change.IsContextDetach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyLeftView);
        }
    }

    private void ScanSubject(IInterceptorSubject subject, SourceEventKind kind)
    {
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var name in subject.Properties.Keys)
        {
            var property = new PropertyReference(subject, name);
            if (!property.TryGetSource(out var source))
            {
                continue;
            }

            var entered = kind == SourceEventKind.PropertyEnteredView;
            Publish(new SourceEvent(
                kind, source, property,
                entered ? SourceState.Unclaimed : source.State,
                entered ? source.State : SourceState.Unclaimed,
                timestamp) { Monitor = this });
        }
    }
}
```

Only events are enqueued under the lifecycle lock; handler code runs on the subscriber's own drain thread, so a handler may write the graph freely.

- [ ] **Step 4: Register the monitor as a lifecycle handler**

In `WithSourceMonitoring`, register the same instance as an `ILifecycleHandler`:

```csharp
    public static IInterceptorSubjectContext WithSourceMonitoring(this IInterceptorSubjectContext context)
    {
        context.TryAddService<SourceMonitor>(() =>
        {
            var monitor = new SourceMonitor();
            context.AddService<ILifecycleHandler>(monitor);
            return monitor;
        }, _ => true);

        return context.WithParents();
    }
```

If `TryAddService`'s documented restriction against mutating a different context from the factory applies, register the handler after `TryAddService` returns instead, resolving the monitor first.

- [ ] **Step 5: Run the tests and commit**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Publish tree membership changes on subject attach and detach

Hooks ILifecycleHandler rather than SubjectDetaching, because the interceptor
raises SubjectDetaching before invoking handlers, so SourceOwnershipManager's
real releases are guaranteed to precede this scan and no duplicates are possible."
```

---

## Task 9: The registration signal

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs`
- Create: `src/Namotion.Interceptor.Connectors/SourceRegistrationGate.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceWaitTests.cs` (create)

**Interfaces:**
- Consumes: `SourceMonitor` (Task 6).
- Produces: `SourceMonitor.CompleteSourceRegistration()`, `SourceMonitor.DeferWaitCompletion()`, `SourceMonitor.IsRegistrationComplete`, context extensions of the same names, and `WithSourceMonitoring(IServiceCollection)`. Task 11 gates waits on `IsRegistrationComplete`.

**The default is fail-safe on purpose:** the monitor is born holding one count, so forgetting to signal hangs a wait, which is loud, rather than completing early on a partially registered tree, which is silent and is the exact bug this design exists to prevent.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceWaitTests.cs`:

```csharp
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceWaitTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheMonitorIsCreated_ThenRegistrationIsIncomplete()
    {
        // Arrange & Act
        var monitor = CreateContext().GetSourceMonitor();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenRegistrationIsCompleted_ThenTheFlagFlipsAndIsIdempotent()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();

        // Act
        monitor.CompleteSourceRegistration();
        monitor.CompleteSourceRegistration();

        // Assert
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenACompletionHoldIsTaken_ThenRegistrationIsIncompleteUntilItIsReleased()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var hold = monitor.DeferWaitCompletion();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        hold.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenHoldsAreNested_ThenRegistrationCompletesOnlyAfterTheLastRelease()
    {
        // Arrange
        var monitor = CreateContext().GetSourceMonitor();
        monitor.CompleteSourceRegistration();

        // Act
        var outer = monitor.DeferWaitCompletion();
        var inner = monitor.DeferWaitCompletion();
        inner.Dispose();

        // Assert
        Assert.False(monitor.IsRegistrationComplete);
        outer.Dispose();
        Assert.True(monitor.IsRegistrationComplete);
    }

    [Fact]
    public void WhenTheContextExtensionIsUsed_ThenEveryReachableMonitorIsSignalled()
    {
        // Arrange
        var context = CreateContext();

        // Act
        context.CompleteSourceRegistration();

        // Assert
        Assert.True(context.GetSourceMonitor().IsRegistrationComplete);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceWaitTests"
```

Expected: compile errors for `IsRegistrationComplete`, `CompleteSourceRegistration`, `DeferWaitCompletion`.

- [ ] **Step 3: Implement the count**

In `SourceMonitor.cs`:

```csharp
    // Born at 1. The monitor takes this hold at WithSourceMonitoring time, during context
    // configuration, before the host is even built, which is what makes signalling
    // order-independent without any argument about hosted service construction order.
    private int _registrationHolds = 1;
    private int _initialHoldReleased;

    /// <summary>True when no registration hold is outstanding, so waits may complete.</summary>
    public bool IsRegistrationComplete => Volatile.Read(ref _registrationHolds) == 0;

    /// <summary>
    /// Releases the initial hold, declaring that every source this application intends to start has
    /// been started and registered. Idempotent, so a re-entrant loader guard is safe.
    /// </summary>
    public void CompleteSourceRegistration()
    {
        if (Interlocked.Exchange(ref _initialHoldReleased, 1) == 1)
        {
            return;
        }

        ReleaseHold();
    }

    /// <summary>
    /// Takes a further hold for the duration of a later batch of source creation. Counted, so
    /// concurrent holders compose. Taking a hold blocks pending waits but never un-completes an
    /// already-completed one.
    /// </summary>
    public IDisposable DeferWaitCompletion()
    {
        Interlocked.Increment(ref _registrationHolds);
        return new RegistrationHold(this);
    }

    private void ReleaseHold()
    {
        if (Interlocked.Decrement(ref _registrationHolds) == 0)
        {
            OnWaitConditionChanged();
        }
    }

    /// <summary>Re-evaluates every pending wait. Task 11 gives this a body; it is a deliberate no-op until then.</summary>
    private void OnWaitConditionChanged()
    {
    }

    private sealed class RegistrationHold(SourceMonitor monitor) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                monitor.ReleaseHold();
            }
        }
    }
```

`OnWaitConditionChanged` is an empty private method in this task and gets its body in Task 11. Leaving the call sites wired now means Task 11 changes one method rather than five.

- [ ] **Step 4: Add the context extensions and the hosted gate**

Append to `SourceMonitoringExtensions.cs`:

```csharp
    /// <summary>
    /// Declares that source registration is complete on every reachable monitor. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static void CompleteSourceRegistration(this IInterceptorSubjectContext context)
    {
        var monitors = ResolveMonitorsOrThrow(context);
        foreach (var monitor in monitors)
        {
            monitor.CompleteSourceRegistration();
        }
    }

    /// <summary>
    /// Blocks wait completion on every reachable monitor until the returned handle is disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable.</exception>
    public static IDisposable DeferWaitCompletion(this IInterceptorSubjectContext context)
    {
        var monitors = ResolveMonitorsOrThrow(context);
        var holds = monitors.Select(monitor => monitor.DeferWaitCompletion()).ToArray();
        return new CompositeDisposable(holds);
    }

    private static ImmutableArray<SourceMonitor> ResolveMonitorsOrThrow(IInterceptorSubjectContext context)
    {
        var monitors = context.GetSourceMonitors();
        if (monitors.IsEmpty)
        {
            throw new InvalidOperationException(
                "No SourceMonitor is reachable from this context. Call WithSourceMonitoring() on the tree root context.");
        }

        return monitors;
    }

    private sealed class CompositeDisposable(IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Adds source monitoring and registers a hosted service that completes source registration when
    /// IHostApplicationLifetime.ApplicationStarted fires. Use this when every source is a
    /// DI-registered hosted service. Applications that create sources at runtime use the
    /// parameterless overload and call CompleteSourceRegistration themselves.
    /// </summary>
    public static IInterceptorSubjectContext WithSourceMonitoring(
        this IInterceptorSubjectContext context, IServiceCollection services)
    {
        context.WithSourceMonitoring();
        services.AddHostedService(_ => new SourceRegistrationGate(context));
        return context;
    }
```

Create `src/Namotion.Interceptor.Connectors/SourceRegistrationGate.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Completes source registration once host startup has finished, so every DI-registered source has
/// been started and registered. Its only job is releasing the initial hold; it takes none of its own.
/// </summary>
internal sealed class SourceRegistrationGate(
    IInterceptorSubjectContext context,
    IHostApplicationLifetime lifetime) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Awaiting ApplicationStarted here would deadlock the host, because it fires only after
        // every StartAsync has returned. Register a callback instead.
        lifetime.ApplicationStarted.Register(context.CompleteSourceRegistration);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`IHostApplicationLifetime` comes from DI, not from the interceptor context, so the registration in `WithSourceMonitoring(IServiceCollection)` above must resolve it:

```csharp
        services.AddHostedService(serviceProvider => new SourceRegistrationGate(
            context, serviceProvider.GetRequiredService<IHostApplicationLifetime>()));
```

`Namotion.Interceptor.Connectors` already references `Microsoft.Extensions.Hosting.Abstractions`, which brings `Microsoft.Extensions.DependencyInjection.Abstractions` transitively. Add an explicit `PackageReference` for the DI abstractions anyway, so the dependency this file relies on is stated rather than inherited.

- [ ] **Step 5: Run the tests and commit**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceWaitTests"
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Add the registration signal replacing the ApplicationStarted protocol

The monitor is born holding one count, so forgetting to signal hangs a wait
rather than completing it early on a partially registered tree. That matters
because the driving application builds its entire tree inside ExecuteAsync,
after ApplicationStarted has already fired."
```

---

## Task 10: The hosted service pending-actions barrier

**Files:**
- Modify: `src/Namotion.Interceptor.Hosting/HostedServiceHandler.cs`
- Modify: `src/Namotion.Interceptor.Hosting/InterceptorSubjectContextExtensions.cs`
- Test: `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs`

**Interfaces:**
- Consumes: nothing from this plan.
- Produces: `public static Task WaitForPendingHostedServiceActionsAsync(this IInterceptorSubjectContext context, CancellationToken cancellationToken = default)`.

**Why it is needed:** `AttachHostedService` posts the start onto a `BufferBlock` and returns. `PostStartService` awaits `Task.Delay(50)` before calling `StartAsync`, and the drain is sequential, so twenty attached sources take at least a second to register. Signalling right after the tree is built is close to guaranteed wrong, not occasionally wrong.

- [ ] **Step 1: Write the failing tests**

Append to `src/Namotion.Interceptor.Hosting.Tests/HostedServiceHandlerTests.cs`:

```csharp
    [Fact]
    public async Task WhenActionsAreQueued_ThenWaitForPendingActionsCompletesOnlyAfterTheyHaveRun()
    {
        // Arrange
        await RunWithAppLifecycleAsync(async context =>
        {
            var person = new Person(context);
            var hostedService = new PersonBackgroundService(person);

            // Act
            person.AttachHostedService(hostedService);
            await context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);

            // Assert
            Assert.Equal("John", person.FirstName);
        });
    }

    [Fact]
    public async Task WhenNoHandlerIsConfigured_ThenWaitForPendingActionsCompletesImmediately()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();

        // Act
        var task = context.WaitForPendingHostedServiceActionsAsync(CancellationToken.None);

        // Assert
        Assert.True(task.IsCompletedSuccessfully);
    }
```

The first test asserts the post-start side effect (`PersonBackgroundService` sets `FirstName` to `"John"`) is already visible when the await returns, without any `WaitUntilAsync`. That is the barrier property. Match the existing `RunWithAppLifecycleAsync` helper's shape in that file.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Namotion.Interceptor.Hosting.Tests
```

Expected: compile error, no `WaitForPendingHostedServiceActionsAsync`.

- [ ] **Step 3: Implement the barrier**

In `HostedServiceHandler.cs`:

```csharp
    /// <summary>
    /// Completes once the actions queued before this call have run.
    /// </summary>
    /// <remarks>
    /// The drain is FIFO and sequential, so a marker posted after the queued starts runs last, and
    /// everything ahead of it has completed by then. This is a barrier for work ALREADY queued:
    /// subjects attaching afterwards post new actions it does not cover, which is the right
    /// semantics for a loader that has finished building its tree. If the drain loop is not running,
    /// the marker never executes and this never completes.
    /// </remarks>
    internal Task WaitForPendingActionsAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _actions.Post(_ =>
        {
            completion.TrySetResult();
            return Task.CompletedTask;
        });
        return completion.Task.WaitAsync(cancellationToken);
    }
```

In `InterceptorSubjectContextExtensions.cs`:

```csharp
    /// <summary>
    /// Completes once the hosted service start and stop actions queued before this call have run,
    /// so services attached through the lifecycle path have actually started.
    /// </summary>
    /// <remarks>
    /// Returns a completed task when no HostedServiceHandler is configured, because nothing was
    /// ever queued.
    /// </remarks>
    public static Task WaitForPendingHostedServiceActionsAsync(
        this IInterceptorSubjectContext context, CancellationToken cancellationToken = default)
    {
        var handler = context.TryGetService<HostedServiceHandler>();
        return handler is null
            ? Task.CompletedTask
            : handler.WaitForPendingActionsAsync(cancellationToken);
    }
```

- [ ] **Step 4: Run the tests, accept the Hosting API snapshot, commit**

```bash
dotnet test src/Namotion.Interceptor.Hosting.Tests
DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Hosting.Tests --filter "FullyQualifiedName~PublicApi"
```

Accept the snapshot if that project has one, then:

```bash
git add src/Namotion.Interceptor.Hosting/ src/Namotion.Interceptor.Hosting.Tests/
git commit -m "Add a barrier over the hosted service attach queue

AttachHostedService posts starts onto a BufferBlock with a 50 ms delay each and
a sequential drain, so a loader that has finished building its tree must wait
for those starts before declaring its source set complete."
```

---

## Task 11: Branch scope and the wait

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/SourceScope.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitor.cs`
- Modify: `src/Namotion.Interceptor.Connectors/SourceMonitoringExtensions.cs`
- Test: `src/Namotion.Interceptor.Connectors.Tests/SourceScopeTests.cs` (create), `SourceWaitTests.cs`

**Interfaces:**
- Consumes: `GetParents()` from `Namotion.Interceptor.Tracking.Parent`, `IsRegistrationComplete` (Task 9).
- Produces:
  - `internal static bool SourceScope.IsAncestorOrSelf(IInterceptorSubject candidate, IInterceptorSubject target)`
  - `internal static bool SourceScope.IsInScope(ISubjectSource source, IInterceptorSubject anchor)`
  - `public Task SourceMonitor.WaitForSynchronizationAsync(IInterceptorSubject subject, CancellationToken cancellationToken = default)`
  - `public static Task WaitForSynchronizationAsync(this IInterceptorSubject subject, CancellationToken cancellationToken = default)`

**Completion conditions, all three required:** the registration count is zero; at least one in-scope source is registered; every registered non-`Stopped` in-scope source is `Synchronized`.

**Trigger set:** registration, unregistration, registration-count changes, state changes, **and any lifecycle change that mutates the parent graph**. Context attach and detach alone are not enough: a reparent within the same tree moves the reference count 1 to 2 to 1, so `IsContextAttach` (0 to 1) and `IsContextDetach` (reaching 0) both stay silent, while `ParentTrackingHandler` updates parents on `IsPropertyReferenceAdded` and `IsPropertyReferenceRemoved` (`src/Namotion.Interceptor.Tracking/Parent/ParentTrackingHandler.cs:19-29`). That reparent case is the exact scenario the trigger exists for.

- [ ] **Step 1: Write the failing scope tests**

Create `src/Namotion.Interceptor.Connectors.Tests/SourceScopeTests.cs`:

```csharp
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceScopeTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithParents()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheSourceIsRootedAtAnAncestor_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(root);

        // Act
        var inScope = SourceScope.IsInScope(source, child);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedAtADescendant_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(child);

        // Act
        var inScope = SourceScope.IsInScope(source, root);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedOnASiblingBranch_ThenItIsNotInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var source = new TestStateSource(right);

        // Act
        var inScope = SourceScope.IsInScope(source, left);

        // Assert
        Assert.False(inScope);
    }

    [Fact]
    public void WhenTheAnchorIsTheSourceRootItself_ThenItIsInScopeWithoutAnyParentWalk()
    {
        // Arrange
        var context = CreateContext();
        var detached = new Person(context);
        var source = new TestStateSource(detached);

        // Act
        var inScope = SourceScope.IsInScope(source, detached);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSubjectHasTwoParents_ThenSourcesOnEitherPathAreInScope()
    {
        // Arrange
        var context = CreateContext();
        var firstRoot = new Person(context);
        var secondRoot = new Person(context);
        var shared = new Person();
        firstRoot.Mother = shared;
        secondRoot.Mother = shared;

        // Act & Assert
        Assert.True(SourceScope.IsInScope(new TestStateSource(firstRoot), shared));
        Assert.True(SourceScope.IsInScope(new TestStateSource(secondRoot), shared));
    }
}
```

Substitute the real subject-valued property names from the test model for `Mother` and `Father`.

- [ ] **Step 2: Run to verify failure, then implement the scope helper**

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "FullyQualifiedName~SourceScopeTests"
```

Expected: compile error, no `SourceScope`.

Create `src/Namotion.Interceptor.Connectors/SourceScope.cs`:

```csharp
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Decides which sources a branch-scoped wait must observe.
/// </summary>
internal static class SourceScope
{
    /// <summary>
    /// True when the source's root and the anchor lie on the same root-to-leaf path, in either
    /// direction: the source is rooted above the anchor and may claim into it, or rooted inside it.
    /// A source on a sibling branch is in neither set, which is what stops an unrelated failing
    /// connection from blocking a wait.
    /// </summary>
    internal static bool IsInScope(ISubjectSource source, IInterceptorSubject anchor)
    {
        var sourceRoot = source.RootSubject;
        return IsAncestorOrSelf(sourceRoot, anchor) || IsAncestorOrSelf(anchor, sourceRoot);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="target"/> or reachable by walking
    /// up from it through tracked parents.
    /// </summary>
    /// <remarks>
    /// The common shape is a short single-parent chain, so that path allocates nothing. The visited
    /// set is allocated only on the first fan-out, where the parent graph is a genuine DAG and an
    /// unguarded walk could revisit or loop.
    /// </remarks>
    internal static bool IsAncestorOrSelf(IInterceptorSubject candidate, IInterceptorSubject target)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        var current = target;
        while (true)
        {
            var parents = current.GetParents();
            if (parents.Length == 0)
            {
                return false;
            }

            if (parents.Length > 1)
            {
                return SearchGraph(candidate, current);
            }

            current = parents[0].Property.Subject;
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }
        }
    }

    private static bool SearchGraph(IInterceptorSubject candidate, IInterceptorSubject start)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IInterceptorSubject>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (ReferenceEquals(current, candidate))
            {
                return true;
            }

            foreach (var parent in current.GetParents())
            {
                pending.Push(parent.Property.Subject);
            }
        }

        return false;
    }
}
```

`HashSet<IInterceptorSubject>` with `ReferenceEqualityComparer.Instance` needs the set's type argument to be `object`, or a custom `IEqualityComparer<IInterceptorSubject>`. Use whichever compiles cleanly under warnings-as-errors.

- [ ] **Step 3: Write the failing wait tests**

Append to `SourceWaitTests.cs`:

```csharp
    [Fact]
    public async Task WhenRegistrationIsIncomplete_ThenTheWaitDoesNotComplete()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        source.ReportSynchronized();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(wait.IsCompleted);
        monitor.CompleteSourceRegistration();
        await wait;
    }

    [Fact]
    public async Task WhenAnInScopeSourceIsConnecting_ThenTheWaitBlocksUntilItSynchronizes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        source.ReportSynchronized();

        // Assert
        await wait;
    }

    [Fact]
    public async Task WhenASiblingBranchSourceNeverSynchronizes_ThenAScopedWaitStillCompletes()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var healthy = new TestStateSource(left);
        var broken = new TestStateSource(right);
        monitor.Register(healthy);
        monitor.Register(broken);
        monitor.CompleteSourceRegistration();
        healthy.ReportSynchronized();

        // Act
        await left.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(SourceState.Connecting, broken.State);
    }

    [Fact]
    public async Task WhenNoInScopeSourceIsRegistered_ThenTheWaitBlocks()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        monitor.CompleteSourceRegistration();

        // Act
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        await Task.Yield();
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task WhenASourceIsRegisteredMidWait_ThenItIsIncludedAndReBlocksTheWait()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var first = new TestStateSource(root);
        monitor.Register(first);
        monitor.CompleteSourceRegistration();
        first.ReportSynchronized();
        var wait = root.WaitForSynchronizationAsync(CancellationToken.None);
        await wait;

        // Act
        var second = new TestStateSource(root);
        monitor.Register(second);
        var secondWait = root.WaitForSynchronizationAsync(CancellationToken.None);

        // Assert
        Assert.False(secondWait.IsCompleted);
        second.ReportSynchronized();
        await secondWait;
    }

    [Fact]
    public async Task WhenEveryInScopeSourceIsStopped_ThenTheWaitCompletesVacuously()
    {
        // Arrange
        var context = CreateContext();
        var monitor = context.GetSourceMonitor();
        var root = new Person(context);
        var source = new TestStateSource(root);
        monitor.Register(source);
        monitor.CompleteSourceRegistration();

        // Act
        source.ReportStopped();

        // Assert
        await root.WaitForSynchronizationAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WhenCancelled_ThenTheWaitPropagatesCancellation()
    {
        // Arrange
        var context = CreateContext();
        context.GetSourceMonitor().CompleteSourceRegistration();
        var root = new Person(context);
        using var cancellation = new CancellationTokenSource();

        // Act
        var wait = root.WaitForSynchronizationAsync(cancellation.Token);
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task WhenNoMonitorIsReachable_ThenTheWaitThrowsWithGuidance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Person(context);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => root.WaitForSynchronizationAsync(CancellationToken.None));
        Assert.Contains("WithSourceMonitoring", exception.Message);
    }
```

- [ ] **Step 4: Implement the wait engine**

In `SourceMonitor.cs`:

```csharp
    private ImmutableArray<PendingWait> _waits = [];

    /// <summary>
    /// Completes when the branch containing <paramref name="subject"/> is synchronized: registration
    /// is complete, at least one in-scope source is registered, and every registered non-Stopped
    /// in-scope source is Synchronized.
    /// </summary>
    public Task WaitForSynchronizationAsync(
        IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        PendingWait wait;
        lock (_lock)
        {
            if (IsSatisfied(subject))
            {
                return Task.CompletedTask;
            }

            wait = new PendingWait(subject);
            _waits = _waits.Add(wait);
        }

        return wait.AwaitAsync(cancellationToken, () =>
        {
            lock (_lock)
            {
                _waits = _waits.Remove(wait);
            }
        });
    }

    private bool IsSatisfied(IInterceptorSubject anchor)
    {
        if (!IsRegistrationComplete)
        {
            return false;
        }

        var matched = false;
        foreach (var source in _sources)
        {
            if (!SourceScope.IsInScope(source, anchor))
            {
                continue;
            }

            matched = true;
            var state = source.State;
            if (state != SourceState.Stopped && state != SourceState.Synchronized)
            {
                return false;
            }
        }

        if (!matched)
        {
            return false;
        }

        if (_sources.All(source => !SourceScope.IsInScope(source, anchor) || source.State == SourceState.Stopped))
        {
            // Stopped is terminal, so this branch will never become live. Completing is more useful
            // than hanging, but silence would read as success, so say it out loud.
            _logger?.LogWarning(
                "A synchronization wait completed with every in-scope source stopped. " +
                "Stopped is terminal, so this branch will not synchronize again.");
        }

        return true;
    }

    private void OnWaitConditionChanged()
    {
        ImmutableArray<PendingWait> waits;
        lock (_lock)
        {
            waits = _waits;
        }

        foreach (var wait in waits)
        {
            bool satisfied;
            lock (_lock)
            {
                satisfied = IsSatisfied(wait.Anchor);
            }

            if (satisfied)
            {
                wait.Complete();
            }
        }
    }

    private sealed class PendingWait(IInterceptorSubject anchor)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IInterceptorSubject Anchor { get; } = anchor;

        public void Complete() => _completion.TrySetResult();

        public async Task AwaitAsync(CancellationToken cancellationToken, Action onFinished)
        {
            try
            {
                await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onFinished();
            }
        }
    }
```

Call `OnWaitConditionChanged()` at the end of `Register`, `Unregister`, `ReleaseHold`, `DeferWaitCompletion`, and `OnSourceStateChanged`. In `HandleLifecycleChange`, call it for **any** change that adds or removes a property reference, before the subscriber gate:

```csharp
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        // Branch scope reads mutable parent data, so a reparent changes the answer with no
        // registration or state transition. A same-tree reparent fires neither IsContextAttach nor
        // IsContextDetach, so gate on reference mutation, and never on the subscriber count: a wait
        // is exactly the consumer that has no subscription.
        if (change.IsPropertyReferenceAdded || change.IsPropertyReferenceRemoved)
        {
            OnWaitConditionChanged();
        }

        if (!HasSubscribers)
        {
            return;
        }

        // ... the catch-up scan from Task 8, unchanged ...
    }
```

If `SubjectLifecycleChange` names those flags differently, use the actual names from `ParentTrackingHandler.cs:19-29`.

- [ ] **Step 5: Add the subject extension**

Append to `SourceMonitoringExtensions.cs`:

```csharp
    /// <summary>
    /// Waits until every source that can claim into this subject's branch has completed its initial
    /// load. The subject IS the scope, so waiting on the tree root means the whole tree.
    /// </summary>
    /// <exception cref="InvalidOperationException">No monitor is reachable from the subject's context.</exception>
    public static Task WaitForSynchronizationAsync(
        this IInterceptorSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var monitors = ResolveMonitorsOrThrow(subject.Context);
        if (monitors.Length == 1)
        {
            return monitors[0].WaitForSynchronizationAsync(subject, cancellationToken);
        }

        return Task.WhenAll(monitors.Select(
            monitor => monitor.WaitForSynchronizationAsync(subject, cancellationToken)));
    }
```

- [ ] **Step 6: Run the tests and commit**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.Connectors.Tests
```

Expected: all pass.

```bash
git add src/Namotion.Interceptor.Connectors/ src/Namotion.Interceptor.Connectors.Tests/
git commit -m "Add the branch-scoped synchronization wait

The anchor subject is the scope, so waiting on the root means the whole tree and
one method replaces a no-argument wait, a predicate overload and a property-list
overload. Re-evaluation triggers include parent-reference mutation, because a
same-tree reparent changes scope while firing neither context attach nor detach."
```

---

## Task 12: OPC UA outage reporting

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/Connection/SessionManager.cs` (in `OnKeepAlive`, around line 306)
- Test: `src/Namotion.Interceptor.OpcUa.Tests/Client/OutageStateTests.cs` (create)

**Interfaces:**
- Consumes: `SubjectSourceBase.ReportConnectionLost()` (Task 5).
- Produces: nothing later tasks use.

**Why this one connector needs an explicit call:** `OnKeepAlive` sees the bad status, sets `_isReconnecting`, and hands off to `BeginReconnect`. It does **not** buffer. Buffering happens only afterwards in `PerformFullStateSyncIfNeededAsync` (`SessionManager.cs:77`), once the SDK has reconnected. So the writer-driven transitions never fire for the SDK auto-reconnect window, which is the common OPC UA outage.

**Do not "simplify" this into a `StartBuffering` call.** That would replace `_updates` with a fresh list, and the later `StartBuffering` in `PerformFullStateSyncIfNeededAsync` would discard everything buffered in between. That changes data-path behaviour in order to fix a reporting bug.

**Before running:** free port 4840. Stop any local Demo.Host app.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.OpcUa.Tests/Client/OutageStateTests.cs`, following the existing integration-test setup in that project (server fixture, client source construction, `Category=Integration` trait):

First open the nearest existing OPC UA client integration test and copy its fixture verbatim: the server start, the context recipe, and the `OpcUaSubjectClientSource` construction. Do not invent setup. Add `.WithSourceMonitoring()` to the copied context recipe, and bind the constructed source to a local named `source`. Then the test body is:

```csharp
[Trait("Category", "Integration")]
public class OutageStateTests
{
    [Fact]
    public async Task WhenTheConnectionIsLost_ThenTheSourceReportsConnectingUntilItRecovers()
    {
        // Arrange
        // <copied fixture: server started, context built with WithSourceMonitoring(),
        //  OpcUaSubjectClientSource constructed and started, assigned to `source`>
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Synchronized);
        var firstSynchronizedAt = source.LastSynchronizedAt;

        // Act
        await ((IFaultInjectable)source).InjectFaultAsync(FaultType.Kill, CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Connecting);
        await AsyncTestHelpers.WaitUntilAsync(() => source.State == SourceState.Synchronized);
        Assert.NotNull(firstSynchronizedAt);
        Assert.True(source.LastSynchronizedAt > firstSynchronizedAt);
    }
}
```

Three `WaitUntilAsync` calls are the whole test: synchronized, then connecting for the duration of the outage, then synchronized again. The timestamp assertion proves the second synchronization is a real reload rather than a stale value carried through.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OutageStateTests"
```

Expected: FAIL at the second `WaitUntilAsync`, because the state never leaves `Synchronized`.

- [ ] **Step 3: Report the loss**

In `SessionManager.OnKeepAlive`, immediately after the existing log line "OPC UA server connection lost. Beginning reconnect...":

```csharp
            _logger.LogInformation("OPC UA server connection lost. Beginning reconnect...");

            // The SDK reconnect path does not buffer until PerformFullStateSyncIfNeededAsync runs
            // after reconnection, so without this the source would report Synchronized for the whole
            // outage. Deliberately not StartBuffering: that would replace the buffer, and the later
            // StartBuffering on the reconnect path would then discard everything buffered here.
            (_source as SubjectSourceBase)?.ReportConnectionLost();
```

`_source` is already a field on `SessionManager` (used for `_source.ReconnectionMetrics`). If it is typed as the concrete client source, call `ReportConnectionLost()` directly without the cast.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "FullyQualifiedName~OutageStateTests"
```

Expected: PASS.

- [ ] **Step 5: Run the full OPC UA suite**

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests
```

Expected: all pass. This suite exercises reconnect heavily, so a regression here means the transition is firing on a path it should not.

- [ ] **Step 6: Add the same coverage for MQTT and WebSocket**

Repeat Steps 1 to 5 for `src/Namotion.Interceptor.Mqtt.Tests` and `src/Namotion.Interceptor.WebSocket.Tests`, using each project's own fixture and `IFaultInjectable`. Those two buffer at loss detection, so their tests should pass without any production change; they exist to stop a future connector from silently reintroducing the OPC UA defect.

For MQTT, assert only that the state returns to `Synchronized`, and add a comment that MQTT's `Synchronized` means subscriptions established rather than retained values received.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.OpcUa/ src/Namotion.Interceptor.OpcUa.Tests/ src/Namotion.Interceptor.Mqtt.Tests/ src/Namotion.Interceptor.WebSocket.Tests/
git commit -m "Report connection loss from the OPC UA keep-alive handler

OnKeepAlive hands off to BeginReconnect without buffering, and buffering happens
only afterwards in PerformFullStateSyncIfNeededAsync, so the writer-driven
transitions never fired for the SDK auto-reconnect window. Outage tests added
for all three client connectors so this cannot silently return."
```

---

## Task 13: Documentation

**Files:**
- Create: `docs/connectors-source-monitoring.md`
- Modify: `docs/connectors.md`, `docs/hosting.md`, `docs/tracking.md`
- Delete: `docs/superpowers/specs/2026-07-03-source-sync-state-design.md`

**Interfaces:**
- Consumes: everything.
- Produces: nothing.

Section order is usage first, mechanism second, so a reader who only wants a live tree stops after two sections.

- [ ] **Step 1: Write the feature page**

Create `docs/connectors-source-monitoring.md` with these sections in this order:

1. **Getting started (DI).** The two code blocks below, nothing else. No `SourceMonitor` appears.
2. **Waiting on part of the tree.** The one-line anchor change, plus what "in scope" means and that a sibling branch's failing source does not block.
3. **Reading per-property state.** `GetSourceState()` and what each value means after the wait.
4. **What Synchronized means per protocol.** OPC UA and WebSocket perform an explicit read. MQTT reaches `Synchronized` when subscriptions are established, because `LoadInitialStateAsync` returns `null` and MQTT has no end-of-retained signal in 3.1.1 or 5.0. Link to issue #418. State plainly that raising QoS does not change this.
5. **Applications that create sources at runtime.** The parameterless overload and the three-statement `ExecuteAsync`, with why the barrier is there. `DeferWaitCompletion()` for later batches.
6. **Observing changes.** `StateChanged` for a held source, the monitor stream for aggregate consumers, and the `CurrentState` versus `NewState` rule with its one-line reason.
7. **The state model, transitions, and delivery contract.** Including that `Stopped` is terminal and a stopped source is never restarted.
8. **Breaking change for custom `ISubjectSource` implementers.** The four new members; recommend deriving from `SubjectSourceBase`.
9. **Worked sample: availability attributes.** Stored `ConnectionState`, derived `IsAvailable`, and the updater. The updater applies `event.CurrentState` for the four property kinds and calls `property.GetSourceState()` per entry on `StateChanged`, because `CurrentState` there is the source's state and says nothing about any property. Note that this makes its per-source index only need to be a superset.

Section 1 code:

```csharp
var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithSourceMonitoring(builder.Services)
    .WithHostedServices(builder.Services);

var root = new Root(context);
builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectClientSource<Root>("opc.tcp://localhost:4840", "opc");
builder.Services.AddHostedService<Worker>();
```

```csharp
internal sealed class Worker(Root root) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await root.WaitForSynchronizationAsync(stoppingToken);
        // every source in the tree has finished its initial load
    }
}
```

Section 5 code:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await LoadAsync(stoppingToken);
    await _context.WaitForPendingHostedServiceActionsAsync(stoppingToken);
    _context.CompleteSourceRegistration();
}
```

- [ ] **Step 2: Update the three existing pages**

`docs/connectors.md`: add a short linking section under Sources pointing at the new page, and add `WithSourceMonitoring()` to the context recipe.

`docs/hosting.md`: document `WaitForPendingHostedServiceActionsAsync` as a barrier over the attach queue, noting it covers work already queued and that later attaches post new actions. Cross-link the source monitoring page rather than duplicating it.

`docs/tracking.md`: document `GetCurrentValue<TValue>()` in the Delivery Guarantees section, next to the existing advice to re-read the property, since it is the API that advice has been missing.

- [ ] **Step 3: Verify the samples compile**

Every code block in section 1 and section 5 must be real. Check the type and method names against the built assemblies:

```bash
dotnet build src/Namotion.Interceptor.slnx
```

- [ ] **Step 4: Delete the spec**

```bash
git rm docs/superpowers/specs/2026-07-03-source-sync-state-design.md
```

The design is now implemented and documented; the spec was scaffolding.

- [ ] **Step 5: Full verification**

```bash
dotnet build src/Namotion.Interceptor.slnx
dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"
```

Expected: build clean with warnings as errors, all unit tests pass. Read the actual output before claiming success.

- [ ] **Step 6: Commit**

```bash
git add docs/
git commit -m "Document source monitoring across the four packages it touches"
```

---

## Rebase note

PR #399 rewrites `SubjectPropertyChange.cs`, touches `docs/connectors.md`, both `VerifyChecksTests.PublicApi.verified.txt` files, `SubjectSourceBaseTests.cs`, and `ChangeQueueProcessorTests.cs`. It is the larger change and lands first. After rebasing onto it, regenerate both API snapshots rather than hand-merging them, and re-run the full unit suite before continuing.
