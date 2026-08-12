# Scheduled per-property change subscriptions implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add scheduled, serialized, error-isolated per-property change delivery, plus a faithful `IObservable<T>` adapter over the existing synchronous channel.

**Architecture:** Three layers over one channel. Layer 0 is the released synchronous `property.Subscribe(callback)`. Layer 1 is `GetSynchronousChangeObservable()`, an `IObservable<SubjectPropertyChange>` with layer 0's contract exactly. Layer 2 is a hand-rolled dispatcher, `ScheduledPropertySubscription`, that queues changes on the writer thread and drains them in bounded batches on a caller-supplied `IScheduler`, serialized per subscription, with observer and scheduler exceptions routed to an `onError` callback instead of into the write.

**Tech Stack:** C# 13, .NET 9, System.Reactive 6.1.0 (`IScheduler` only, no Rx operators in the dispatcher), xUnit.

**Spec:** `docs/superpowers/specs/2026-08-12-scheduled-property-subscriptions-design.md` (revision 3). Read the "The dispatch protocol", "Faulting is one-shot and shares its flag with disposal", and "`ExecutionContext` flow is suppressed" sections before Task 4.

## Global Constraints

- Work in the worktree `/Users/ricosuter/Projects/GitHub/Namotion.Interceptor-scheduled-subscriptions`, branch `scheduled-property-subscriptions`.
- Build: `dotnet build src/Namotion.Interceptor.slnx`. Test: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests`.
- `Directory.Build.props` sets nullable enabled and warnings as errors. A warning fails the build.
- Test naming is `When<Condition>_Then<ExpectedBehavior>`, with explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No hardcoded `Task.Delay` or `Thread.Sleep` in tests. Use `AsyncTestHelpers.WaitUntilAsync`, `ManualResetEventSlim`, `CountdownEvent`, or the controllable scheduler from Task 3.
- Every test class that creates per-property subscriptions carries `[Collection(PerPropertySubscriptionCollection.Name)]` and a constructor calling `PropertyChangeSubscriptions.ResetForTests()`.
- No em dashes in docs, XML comments, or commit messages.
- No AI attribution in commit messages. No `Co-Authored-By` trailers.
- Comments explain only the non-obvious. Do not restate what the code says.
- `Namotion.Interceptor.Tracking` must not gain any package reference. It references System.Reactive and the core project only.
- `src/Namotion.Interceptor.Tracking/Namotion.Interceptor.Tracking.csproj` already has `<InternalsVisibleTo Include="Namotion.Interceptor.Tracking.Tests" />`, so tests can reach `internal` members.

## File Structure

**Production**
- Create `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs`: internal `IObservable<SubjectPropertyChange>` adapter for one property (layer 1).
- Create `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs`: the public handle and the whole dispatch protocol (layer 2). All queue, counter, state, and scheduling logic lives here and nowhere else.
- Modify `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`: five new public methods, thin wrappers over the two files above.
- Modify `src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs` and `PropertyChangeCallback.cs`: XML docs only.

**Tests** (all in `src/Namotion.Interceptor.Tracking.Tests/Change/`)
- Modify `PerPropertySubscriptionConventionsTests.cs`: marker list.
- Create `TestSchedulers.cs`: `ControllableScheduler`, `ThrowingScheduler`, `BlackHoleScheduler`, `RecordingScheduler`.
- Create `SynchronousChangeObservableTests.cs`: layer 1.
- Create `ScheduledPropertySubscriptionProtocolTests.cs`: drain, counter, state machine, faults, ambient context.
- Create `ScheduledPropertySubscriptionTests.cs`: delivery semantics, guards, lifecycle, thread economy.
- Modify `VerifyChecksTests.PublicApi.verified.txt`: accepted snapshot.

**Docs and benchmarks**
- Modify `docs/tracking.md`.
- Modify `src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs`.

---

### Task 1: Close the conventions-test hole

The conventions test fails any test file that touches per-property subscription state without joining the serialized collection. Its marker list does not mention any name this plan introduces, so the Task 2 and Task 4 test files would run in parallel with everything else and corrupt the process-wide subscription count. This must land before any new test file exists.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionConventionsTests.cs:8-20`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing consumed by later tasks in code, but every later test file depends on this guard being live.

- [ ] **Step 1: Add the three markers**

Replace the `SensitiveMarkers` array:

```csharp
    // SubscribeToPath (PR #381) builds on per-property subscriptions; listed ahead of its arrival.
    private static readonly string[] SensitiveMarkers =
    [
        "PropertyChangeSubscriptions.",
        "PropertyChangeSubscription.Create",
        "SubscribeToProperty",
        "SubscribeToPath",
        "IPropertyChangeObserver",
        "PropertyChangeCallback",
        // The scheduled channel and the observable adapter install real per-property subscriptions
        // without naming any type above: the observable is subscribed in Rx form
        // (.Subscribe(change => ...)), which matches none of the lambda markers below. The observable
        // marker matches the construction expression rather than the bare type name, which is a
        // substring of the unrelated context-level GetPropertyChangeObservable.
        "GetSynchronousChangeObservable",
        "ScheduledPropertySubscription",
        "new PropertyChangeObservable",
        // The low-level PropertyReference.Subscribe overloads taking an inline callback name none
        // of the types above, so match the lambda form itself (both `in` spellings).
        ".Subscribe((in ",
        ".Subscribe(static (in ",
    ];
```

- [ ] **Step 2: Run the conventions test**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PerPropertySubscriptionConventionsTests"`
Expected: PASS, 1 test. No file uses the new names yet, so there are no offenders.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionConventionsTests.cs
git commit -m "test: Guard the scheduled subscription names in the collection convention"
```

---

### Task 2: Layer 1, the synchronous change observable

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs` (append one method before `ResolveDirectPropertyName`)
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/SynchronousChangeObservableTests.cs`

**Interfaces:**
- Consumes: the released `PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, PropertyChangeCallback)`.
- Produces: `public static IObservable<SubjectPropertyChange> GetSynchronousChangeObservable(this PropertyReference property)`. Task 4 does not use it; it is an independent deliverable.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/SynchronousChangeObservableTests.cs`:

```csharp
using System.Reactive.Linq;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SynchronousChangeObservableTests
{
    public SynchronousChangeObservableTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenObservableIsNotSubscribed_ThenNoSubscriptionIsInstalled()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);

        // Act
        _ = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTwoObserversSubscribe_ThenEachInstallsItsOwnSubscriptionAndBothReceive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();
        var first = new List<string?>();
        var second = new List<string?>();

        using var firstSubscription = observable.Subscribe(change => first.Add(change.GetNewValue<string?>()));
        using var secondSubscription = observable.Subscribe(change => second.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Equal(2, PropertyChangeSubscriptions.ReadSubscriptionCount());
        Assert.Equal(["Rico"], first);
        Assert.Equal(["Rico"], second);
    }

    [Fact]
    public void WhenRxHandleIsDisposed_ThenTheUnderlyingSubscriptionIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var received = new List<string?>();
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Subscribe(change => received.Add(change.GetNewValue<string?>()));

        // Act
        subscription.Dispose();
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenHandlerThrows_ThenItPropagatesToTheWriterAndTheSubscriptionStaysLive()
    {
        // Arrange: layer 1 inherits layer 0's contract rather than softening it. A throw reaching the
        // writer, and a subscription that survives it, is what pins the decision not to derive from
        // ObservableBase<T>, whose AutoDetachObserver would dispose on throw.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Subscribe(_ =>
            {
                deliveries++;
                throw new InvalidOperationException("handler failed");
            });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "one");
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "two");
        Assert.Equal(2, deliveries);
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenComposedWithTakeOne_ThenTheUnderlyingSubscriptionIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .GetSynchronousChangeObservable()
            .Take(1)
            .Subscribe(change => received.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "one";
        person.FirstName = "two";

        // Assert
        Assert.Equal(["one"], received);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenObserverIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var observable = new PropertyReference(person, nameof(Person.FirstName)).GetSynchronousChangeObservable();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => observable.Subscribe(null!));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenCalledTwice_ThenEachCallReturnsADistinctInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act
        var first = property.GetSynchronousChangeObservable();
        var second = property.GetSynchronousChangeObservable();

        // Assert: nothing may key observables by identity.
        Assert.NotSame(first, second);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~SynchronousChangeObservableTests"`
Expected: build FAILS with `CS1061: 'PropertyReference' does not contain a definition for 'GetSynchronousChangeObservable'`.

- [ ] **Step 3: Create the observable**

Create `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Exposes a single property's changes as an <see cref="IObservable{T}"/> so Rx operators compose over a
/// per-property subscription. Subscribing installs one underlying per-property subscription per observer.
/// </summary>
/// <remarks>
/// Implements <see cref="IObservable{T}"/> directly rather than deriving from <c>ObservableBase&lt;T&gt;</c>
/// or being produced by an Rx operator. Both of those wrap observers in a decorator that disposes the
/// subscription when the handler throws, which would diverge from the contract of
/// <see cref="PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, IPropertyChangeObserver)"/>
/// that this type deliberately mirrors.
/// </remarks>
internal sealed class PropertyChangeObservable(PropertyReference property) : IObservable<SubjectPropertyChange>
{
    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // OnError is never raised: the per-property channel has no error signal, and a throwing observer
        // is the observer's own problem, exactly as for an unscheduled subscription.
        return property.Subscribe((in SubjectPropertyChange change) => observer.OnNext(change));
    }
}
```

- [ ] **Step 4: Add the extension method**

In `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`, insert after the `SubscribeToProperty` callback overload (currently ending at line 82) and before `ResolveDirectPropertyName`:

```csharp
    /// <summary>
    /// Exposes a single property's changes as an observable, so Rx operators compose over a per-property
    /// subscription. Each subscriber installs its own underlying subscription, and each call returns a
    /// distinct instance.
    /// </summary>
    /// <remarks>
    /// Delivery keeps the contract of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver)"/>
    /// exactly: synchronous, on the writing thread, possibly concurrent, and a throwing handler propagates
    /// back into the setter. It is that channel wearing an <see cref="IObservable{T}"/>, not a safer one.
    /// The context-level <c>GetPropertyChangeObservable</c> reschedules onto a scheduler by default and is
    /// therefore not the same thing.
    /// <para>
    /// Two hazards when composing. <c>ObserveOn</c> dedicates a private thread per subscription when the
    /// scheduler advertises <c>ISchedulerLongRunning</c>, which both <c>Scheduler.Default</c> and
    /// <c>TaskPoolScheduler</c> do, so composing it per property is unaffordable. And an exception reaching
    /// an <c>ObserveOn</c> sink escapes a scheduler work item, which on the thread pool is unhandled and
    /// terminates the process. Prefer the scheduler overloads of <c>Subscribe</c>, which have neither.
    /// </para>
    /// <para>
    /// The sequence never completes and never signals OnError, so operators that wait for completion, such
    /// as <c>ToTask</c> and <c>LastAsync</c>, never return.
    /// </para>
    /// </remarks>
    /// <param name="property">The property to observe.</param>
    public static IObservable<SubjectPropertyChange> GetSynchronousChangeObservable(this PropertyReference property)
        => new PropertyChangeObservable(property);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~SynchronousChangeObservableTests"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Accept the public API snapshot**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL, with a `VerifyChecksTests.PublicApi.received.txt` written next to the verified file.

Inspect the diff, confirm the only added line is the `GetSynchronousChangeObservable` signature, then accept it:

```bash
mv src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs \
        src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/SynchronousChangeObservableTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "feat: Expose a single property's changes as a synchronous observable"
```

---

### Task 3: Test schedulers

Four schedulers the later tasks need. `ControllableScheduler` is what makes the protocol tests deterministic instead of raced. `ThrowingScheduler` and `BlackHoleScheduler` reproduce the two scheduler-failure shapes no real scheduler produces on demand. `RecordingScheduler` is how a test asserts that nothing escaped into a work item, which cannot be asserted directly because an escape on `Scheduler.Default` kills the test host rather than failing the test.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Change/TestSchedulers.cs`
- Test: same file, exercised by Tasks 4 to 7. Its own behaviour is pinned by the two tests below.

**Interfaces:**
- Produces:
  - `ControllableScheduler : IScheduler` with `int QueuedCount { get; }`, `int ScheduleCallCount { get; }`, `bool RunOne()`, `int RunAll()`, `void RunUntilIdle()`.
  - `ThrowingScheduler : IScheduler` whose `Schedule` always throws `ObjectDisposedException`.
  - `BlackHoleScheduler : IScheduler` whose `Schedule` accepts and never runs, with `int ScheduleCallCount { get; }`.
  - `RecordingScheduler(IScheduler inner) : IScheduler` with `IReadOnlyList<Exception> Escaped { get; }` and `int ScheduleCallCount { get; }`.

- [ ] **Step 1: Write the failing test**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/TestSchedulersTests.cs`:

```csharp
using System.Reactive.Concurrency;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class TestSchedulersTests
{
    [Fact]
    public void WhenWorkIsScheduled_ThenItRunsOnlyWhenTheTestPumpsIt()
    {
        // Arrange
        var scheduler = new ControllableScheduler();
        var ran = 0;

        // Act
        scheduler.Schedule(0, (_, _) => { ran++; return System.Reactive.Disposables.Disposable.Empty; });

        // Assert
        Assert.Equal(0, ran);
        Assert.Equal(1, scheduler.QueuedCount);
        Assert.True(scheduler.RunOne());
        Assert.Equal(1, ran);
        Assert.False(scheduler.RunOne());
    }

    [Fact]
    public void WhenScheduledWorkThrows_ThenTheRecordingSchedulerCapturesItInsteadOfLettingItEscape()
    {
        // Arrange
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);

        // Act
        scheduler.Schedule(0, (_, _) => throw new InvalidOperationException("boom"));
        inner.RunAll();

        // Assert
        var escaped = Assert.Single(scheduler.Escaped);
        Assert.IsType<InvalidOperationException>(escaped);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~TestSchedulersTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'ControllableScheduler' could not be found`.

- [ ] **Step 3: Write the schedulers**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/TestSchedulers.cs`:

```csharp
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Runs scheduled work only when the test pumps it, so an interleaving is chosen rather than raced for.
/// </summary>
internal sealed class ControllableScheduler : IScheduler
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public int QueuedCount
    {
        get { lock (_gate) { return _queue.Count; } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        lock (_gate)
        {
            _queue.Enqueue(() => action(this, state));
        }

        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    /// <summary>Runs at most one queued work item. Returns false when the queue was empty.</summary>
    public bool RunOne()
    {
        Action? work;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                return false;
            }

            work = _queue.Dequeue();
        }

        work();
        return true;
    }

    /// <summary>Runs every item queued at entry, without following items those items queue.</summary>
    public int RunAll()
    {
        Action[] batch;
        lock (_gate)
        {
            batch = _queue.ToArray();
            _queue.Clear();
        }

        foreach (var work in batch)
        {
            work();
        }

        return batch.Length;
    }

    /// <summary>Runs items, including ones scheduled by earlier items, until nothing is left.</summary>
    public void RunUntilIdle()
    {
        while (RunOne())
        {
        }
    }
}

/// <summary>Reproduces a scheduler disposed before the subscription: Schedule throws on the writer thread.</summary>
internal sealed class ThrowingScheduler : IScheduler
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => throw new ObjectDisposedException(nameof(ThrowingScheduler));
}

/// <summary>
/// Reproduces a scheduler disposed while a drain was already queued: Schedule succeeds and the work item
/// never runs. This is the half of the scheduler-failure space the design cannot recover from.
/// </summary>
internal sealed class BlackHoleScheduler : IScheduler
{
    private int _scheduleCallCount;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return Disposable.Empty;
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}

/// <summary>
/// Wraps a scheduler and records anything that escapes a work item. On a real pool scheduler such an escape
/// is unhandled and terminates the test host, so it can only be asserted by catching it here first.
/// </summary>
internal sealed class RecordingScheduler(IScheduler inner) : IScheduler
{
    private readonly List<Exception> _escaped = [];
    private int _scheduleCallCount;

    public DateTimeOffset Now => inner.Now;

    public int ScheduleCallCount => Volatile.Read(ref _scheduleCallCount);

    public IReadOnlyList<Exception> Escaped
    {
        get { lock (_escaped) { return _escaped.ToArray(); } }
    }

    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
    {
        Interlocked.Increment(ref _scheduleCallCount);
        return inner.Schedule(state, (scheduler, innerState) =>
        {
            try
            {
                return action(scheduler, innerState);
            }
            catch (Exception exception)
            {
                lock (_escaped)
                {
                    _escaped.Add(exception);
                }

                return Disposable.Empty;
            }
        });
    }

    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);

    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => Schedule(state, action);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~TestSchedulersTests"`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Change/TestSchedulers.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/TestSchedulersTests.cs
git commit -m "test: Add controllable, throwing, black-hole and recording schedulers"
```

---

### Task 4: The dispatcher, enqueue and bounded drain

The protocol core. Nothing here touches disposal or faults; Task 5 adds those.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionProtocolTests.cs`

**Interfaces:**
- Consumes: `PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, IPropertyChangeObserver)`.
- Produces:
  - `internal static ScheduledPropertySubscription Create(PropertyReference property, IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError)`
  - `public int PendingCount { get; }`
  - `internal const int MaxBatch = 1024;`
  - `internal int WorkInProgressForTests { get; }`
  - `internal int ReentrancyCountForTests { get; }`
  - `internal static bool EnableReentrancyInstrumentation` (static, test-only switch)
  - `public void Dispose()` (a stub in this task, completed in Task 5)

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionProtocolTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionProtocolTests : IDisposable
{
    public ScheduledPropertySubscriptionProtocolTests()
    {
        PropertyChangeSubscriptions.ResetForTests();
        ScheduledPropertySubscription.EnableReentrancyInstrumentation = true;
    }

    public void Dispose() => ScheduledPropertySubscription.EnableReentrancyInstrumentation = false;

    [Fact]
    public void WhenChangesAreWritten_ThenOneDrainIsScheduledAndDeliversThemInOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        person.FirstName = "thre";

        // Assert: only the zero-to-one transition schedules, so three writes cost one work item.
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(3, subscription.PendingCount);

        scheduler.RunUntilIdle();
        Assert.Equal(["one", "two", "thre"], received);
    }

    [Fact]
    public void WhenTheQueueDrains_ThenTheCounterSettlesToZeroAndTheQueueIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        // Act
        for (var i = 0; i < 50; i++)
        {
            person.FirstName = i.ToString();
        }

        scheduler.RunUntilIdle();

        // Assert: this pairing is what pins the settle. The re-entrancy counter cannot substitute for it.
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenMoreThanOneBatchIsQueued_ThenTheDrainYieldsAndHandsOffInsteadOfLooping()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler);

        var total = ScheduledPropertySubscription.MaxBatch + 10;

        // Act
        for (var i = 0; i < total; i++)
        {
            person.FirstName = i.ToString();
        }

        var firstBatch = scheduler.RunOne();

        // Assert: the first work item stopped at the budget and queued a successor rather than
        // draining to empty, which is what keeps it from holding a scheduler thread.
        Assert.True(firstBatch);
        Assert.Equal(ScheduledPropertySubscription.MaxBatch, delivered);
        Assert.Equal(2, scheduler.ScheduleCallCount);

        scheduler.RunUntilIdle();
        Assert.Equal(total, delivered);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public async Task WhenManyWritersRaceOneProperty_ThenTheObserverIsNeverReenteredAndNothingIsLost()
    {
        // Arrange
        const int writers = 8;
        const int writesPerWriter = 2_000;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var delivered = 0;

        using var allDelivered = new CountdownEvent(writers * writesPerWriter);
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    delivered++; // deliberately unsynchronized: serialization is the contract under test
                    allDelivered.Signal();
                },
                System.Reactive.Concurrency.Scheduler.Default);

        // Act
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < writesPerWriter; i++)
            {
                person.FirstName = $"{writer}-{i}";
            }
        })));

        Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(30)), $"only {delivered} of {writers * writesPerWriter} arrived");

        // Assert
        Assert.Equal(0, subscription.ReentrancyCountForTests);
        Assert.Equal(writers * writesPerWriter, delivered);
        Assert.Equal(0, subscription.PendingCount);
    }

    [Fact]
    public void WhenTheObserverThrows_ThenTheCounterStillSettlesAndDeliveryContinues()
    {
        // Arrange: the settle is in a finally and counts dequeues, so an escape from Deliver cannot
        // pin the counter. This is the defect the design convicts ObserveOn of.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var errors = new List<Exception>();
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    if (deliveries == 1)
                    {
                        throw new InvalidOperationException("observer failed");
                    }
                },
                scheduler,
                errors.Add);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(2, deliveries);
        Assert.Single(errors);
        Assert.Equal(0, subscription.WorkInProgressForTests);
        Assert.Equal(0, subscription.PendingCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionProtocolTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'ScheduledPropertySubscription' could not be found` and `CS1501: No overload for method 'Subscribe' takes 2 arguments`.

- [ ] **Step 3: Write the dispatcher**

Create `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A per-property subscription whose deliveries run on a scheduler instead of on the writing thread,
/// serialized within the subscription, with observer and scheduler exceptions reported rather than
/// propagated into the write. Disposal is mandatory.
/// </summary>
public sealed class ScheduledPropertySubscription : IDisposable
{
    private const int Live = 0;
    private const int Disposed = 1;
    private const int Faulted = 2;

    /// <summary>
    /// Deliveries per scheduler work item before the drain hands off to a fresh one. Without a budget the
    /// drain would hold its scheduler thread for as long as a writer outruns the observer, which starves
    /// sibling subscriptions and unrelated pool work. 1024 costs one work item per 1024 changes.
    /// </summary>
    internal const int MaxBatch = 1024;

    // Re-entrancy accounting is test-only: two interlocked operations per delivery is not a cost the
    // production path should pay for an assertion.
    internal static bool EnableReentrancyInstrumentation;

    // Cached and static so no closure or delegate is allocated per Schedule call.
    private static readonly Func<IScheduler, ScheduledPropertySubscription, IDisposable> DrainAction =
        static (_, subscription) =>
        {
            subscription.Drain();
            return Disposable.Empty;
        };

    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly IScheduler _scheduler;

    private IPropertyChangeObserver? _observer;
    private Action<Exception>? _onError;
    private IDisposable? _upstream;

    private int _state;
    private int _wip;
    private int _inDeliver;
    private int _reentrancyCount;

    private ScheduledPropertySubscription(IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError)
    {
        _observer = observer;
        _scheduler = scheduler;
        _onError = onError;
    }

    /// <summary>
    /// Changes accepted but not yet dequeued, excluding one currently being delivered. Exact only when read
    /// from a quiescent state, for the same reason <see cref="PropertyChangeQueueSubscription.Count"/> is.
    /// The queue is unbounded, so this is how a consumer on a hot property observes a growing backlog
    /// instead of discovering it through memory pressure.
    /// </summary>
    public int PendingCount => _queue.Count;

    internal int WorkInProgressForTests => Volatile.Read(ref _wip);

    internal int ReentrancyCountForTests => Volatile.Read(ref _reentrancyCount);

    internal static ScheduledPropertySubscription Create(
        PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError)
    {
        var subscription = new ScheduledPropertySubscription(observer, scheduler, onError);

        // Installs the upstream and can start delivering before the assignment below.
        var upstream = property.Subscribe(new Forwarder(subscription));
        Volatile.Write(ref subscription._upstream, upstream);

        // A change arriving during Subscribe can fault the subscription through a throwing scheduler, and
        // that transition saw a null upstream. Releasing here is what stops it leaking.
        if (Volatile.Read(ref subscription._state) != Live)
        {
            Interlocked.Exchange(ref subscription._upstream, null)?.Dispose();
        }

        return subscription;
    }

    private void Enqueue(in SubjectPropertyChange change)
    {
        if (Volatile.Read(ref _state) != Live)
        {
            return;
        }

        // Enqueue before the increment: TryDequeue then cannot report empty while the counter is positive,
        // because ConcurrentQueue spins on a reserved-but-unpublished slot instead. Reversing these is a
        // liveness bug, not a correctness one, and it shows up as drains that find nothing.
        _queue.Enqueue(change);
        if (Interlocked.Increment(ref _wip) == 1)
        {
            ScheduleDrain();
        }
    }

    private void Drain()
    {
        var processed = 0;
        try
        {
            // A count hint only. Item visibility comes from the queue, and the settling Add below is what
            // makes a stale read safe.
            var pending = Volatile.Read(ref _wip);
            while (processed < pending && processed < MaxBatch)
            {
                if (Volatile.Read(ref _state) != Live)
                {
                    return;
                }

                if (!_queue.TryDequeue(out var change))
                {
                    break;
                }

                processed++; // counts the dequeue, not the delivery, so an escape leaves the counter consistent
                Deliver(in change);
            }
        }
        finally
        {
            if (Interlocked.Add(ref _wip, -processed) != 0 && Volatile.Read(ref _state) == Live)
            {
                ScheduleDrain();
            }
        }
    }

    private void ScheduleDrain()
    {
        try
        {
            // Scheduling happens inside the write, so without suppression the observer would inherit the
            // writer's ambient AsyncLocal state, including SubjectTransaction.CurrentTransaction, and a whole
            // batch would run under whichever writer enqueued first.
            if (ExecutionContext.IsFlowSuppressed())
            {
                _scheduler.Schedule(this, DrainAction);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _scheduler.Schedule(this, DrainAction);
                }
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
            TransitionOutOfLive(Faulted);
        }
    }

    private void Deliver(in SubjectPropertyChange change)
    {
        // Read into a local so a disposal racing this delivery cannot null-reference it, matching
        // PropertyChangeSubscription.Dispatch.
        var observer = Volatile.Read(ref _observer);
        if (observer is null)
        {
            return;
        }

        var instrumented = EnableReentrancyInstrumentation;
        if (instrumented && Interlocked.Increment(ref _inDeliver) != 1)
        {
            Interlocked.Increment(ref _reentrancyCount);
        }

        try
        {
            observer.OnChange(in change);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            if (instrumented)
            {
                Interlocked.Decrement(ref _inDeliver);
            }
        }
    }

    private void ReportError(Exception exception)
    {
        var onError = Volatile.Read(ref _onError);
        if (onError is null)
        {
            return;
        }

        try
        {
            onError(exception);
        }
        catch
        {
            // The handler added to observe failures must not become one. An escape here would leave a
            // scheduler work item, which on the thread pool is unhandled and terminates the process.
        }
    }

    private bool TransitionOutOfLive(int target)
    {
        if (Interlocked.CompareExchange(ref _state, target, Live) != Live)
        {
            return false;
        }

        Volatile.Write(ref _observer, null);
        Volatile.Write(ref _onError, null);

        // Releasing through the upstream's own one-shot Dispose is what makes the process-wide gate
        // decrement unreachable twice when a fault races a disposal.
        Interlocked.Exchange(ref _upstream, null)?.Dispose();

        // Queued changes each pin a subject and its boxed values, and these handles get parked in DI
        // containers, so they are released rather than retained.
        _queue.Clear();
        return true;
    }

    public void Dispose() => TransitionOutOfLive(Disposed);

    private sealed class Forwarder(ScheduledPropertySubscription owner) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => owner.Enqueue(in change);
    }
}
```

- [ ] **Step 4: Add the two `PropertyReference` overloads the tests call**

In `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`, add `using System.Reactive.Concurrency;` at the top, and insert these after `GetSynchronousChangeObservable`:

```csharp
    /// <summary>
    /// Subscribes to changes of a single property and delivers them on <paramref name="scheduler"/> instead
    /// of on the writing thread, one at a time and in dispatch order.
    /// </summary>
    /// <remarks>
    /// Same ownership and dormancy contract as
    /// <see cref="Subscribe(PropertyReference, IPropertyChangeObserver)"/>, with four differences that follow
    /// from delivery being scheduled. Within this subscription the observer is never re-entered, so it needs
    /// no synchronization of its own; an observer, closure, or <paramref name="onError"/> delegate shared
    /// across several subscriptions is still invoked concurrently. An exception from the observer cannot
    /// reach the writer and is reported to <paramref name="onError"/>, leaving the subscription live. A
    /// change still queued when the subscription is disposed is dropped. And a change accepted before the
    /// subject detaches is still delivered afterwards, which disposal is not: dormancy stops acceptance, not
    /// the drain.
    /// <para>
    /// The queue is unbounded. A writer faster than the observer grows it without limit, and every buffered
    /// change keeps its subject alive; watch <see cref="ScheduledPropertySubscription.PendingCount"/>, or
    /// compose <c>Sample</c> on <see cref="GetSynchronousChangeObservable"/> for a hot property. An observer
    /// that writes the property it observes never drains, quietly, where the unscheduled overload would
    /// raise a StackOverflowException.
    /// </para>
    /// <para>
    /// The caller owns the scheduler and must dispose subscriptions before it. A schedule that throws is
    /// reported to <paramref name="onError"/> and faults the subscription; a schedule that succeeds and whose
    /// work item never runs cannot be detected, and that subscription goes quiet. Give a subscription its own
    /// scheduler or use <c>Scheduler.Default</c>: ambient <c>AsyncLocal</c> state is suppressed per work item,
    /// but a scheduler whose worker thread was created by someone else exposes the state frozen at that
    /// thread's creation.
    /// </para>
    /// </remarks>
    /// <param name="property">The property to subscribe to.</param>
    /// <param name="observer">The observer, invoked on <paramref name="scheduler"/>.</param>
    /// <param name="scheduler">The scheduler each change is delivered on. Synchronous schedulers are rejected.</param>
    /// <param name="onError">Invoked when the observer or the scheduler throws; the exception is swallowed
    /// when null, which also makes a permanently throwing observer invisible. It must not throw, may run
    /// after Dispose returns, is serialized per subscription rather than per delegate, and can run
    /// synchronously on the writer thread under a transaction commit lock, so it must not write properties,
    /// start a transaction, or block.</param>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scheduler);
        ThrowIfSynchronous(scheduler);

        return ScheduledPropertySubscription.Create(property, observer, scheduler, onError);
    }

    /// <summary>Delegate overload of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.</summary>
    public static ScheduledPropertySubscription Subscribe(
        this PropertyReference property,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return property.Subscribe(new DelegateObserver(callback), scheduler, onError);
    }

    private static void ThrowIfSynchronous(IScheduler scheduler)
    {
        // Only the two singletons are detectable. Any scheduler that runs actions inline has the same
        // hazard, including DisableOptimizations wrappers over these, and cannot be rejected.
        if (ReferenceEquals(scheduler, ImmediateScheduler.Instance)
            || ReferenceEquals(scheduler, CurrentThreadScheduler.Instance))
        {
            throw new ArgumentException(
                "A synchronous scheduler delivers on the writing thread, so one writer's setter can end up " +
                "draining every other writer's changes and its latency becomes unbounded. Use " +
                "property.Subscribe(callback) for synchronous delivery.",
                nameof(scheduler));
        }
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionProtocolTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs \
        src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionProtocolTests.cs
git commit -m "feat: Add the scheduled per-property dispatcher with a bounded drain"
```

---

### Task 5: Disposal, faults, and the process-wide gate

Task 4 wrote `TransitionOutOfLive` and `Dispose`; this task pins their behaviour under the races that motivated them, and adds the fault-path tests.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionProtocolTests.cs` (append)
- Modify: `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs` only if a test fails

**Interfaces:**
- Consumes: everything Task 4 produced, plus `ThrowingScheduler`, `BlackHoleScheduler` and `RecordingScheduler` from Task 3.
- Produces: no new members.

- [ ] **Step 1: Write the failing tests**

Append to `ScheduledPropertySubscriptionProtocolTests`:

```csharp
    [Fact]
    public void WhenDisposedBeforeTheQueueDrains_ThenQueuedChangesAreDroppedAndReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        person.FirstName = "one";
        person.FirstName = "two";
        Assert.Equal(2, subscription.PendingCount);

        // Act
        subscription.Dispose();
        scheduler.RunUntilIdle();

        // Assert: dropped, and released rather than retained behind the handle.
        Assert.Empty(received);
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenDisposedTwice_ThenTheProcessWideCountIsDecrementedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);
        var scheduler = new ControllableScheduler();

        using var keepAlive = new PropertyReference(other, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        // Act
        subscription.Dispose();
        subscription.Dispose();

        // Assert: a double decrement would zero the process-wide gate and silently stop per-property
        // delivery for every other live subscription in the host.
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTheSchedulerThrows_ThenItIsReportedAndDoesNotReachTheWriter()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var errors = new List<Exception>();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler(), errors.Add);

        // Act
        person.FirstName = "one";

        // Assert: the setter returned normally, and the fault released the subscription exactly once.
        Assert.Equal("one", person.FirstName);
        Assert.IsType<ObjectDisposedException>(Assert.Single(errors));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTheSchedulerThrowsAndTheSubscriptionIsAlreadyDisposed_ThenTheCountDoesNotDrift()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var other = new Person(context);
        var scheduler = new ControllableScheduler();

        using var keepAlive = new PropertyReference(other, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, scheduler);

        var errors = new List<Exception>();
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => { }, new ThrowingScheduler(), errors.Add);

        // Act: dispose first, then provoke the fault path.
        subscription.Dispose();
        person.FirstName = "one";

        // Assert: acceptance stopped at dispose, so no fault fires and the gate is untouched.
        Assert.Empty(errors);
        Assert.Equal(1, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenTheSchedulerAcceptsWorkAndNeverRunsIt_ThenTheSubscriptionGoesQuietWithoutReporting()
    {
        // Arrange: pins the documented limit rather than a promise. There is no cheap liveness escape
        // that does not add a timer per subscription.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new BlackHoleScheduler();
        var errors = new List<Exception>();
        var delivered = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => delivered++, scheduler, errors.Add);

        // Act
        for (var i = 0; i < 20; i++)
        {
            person.FirstName = i.ToString();
        }

        // Assert
        Assert.Equal(0, delivered);
        Assert.Empty(errors);
        Assert.Equal(1, scheduler.ScheduleCallCount);
        Assert.Equal(20, subscription.PendingCount);
    }

    [Fact]
    public void WhenOnErrorThrows_ThenNothingEscapesIntoTheSchedulerAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler,
                _ => throw new InvalidOperationException("error handler failed"));

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
        Assert.Equal(0, subscription.WorkInProgressForTests);
    }

    [Fact]
    public void WhenTheObserverThrowsWithNoErrorHandler_ThenItIsSwallowedAndDeliveryContinues()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var inner = new ControllableScheduler();
        var scheduler = new RecordingScheduler(inner);
        var deliveries = 0;

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe(
                (in SubjectPropertyChange _) =>
                {
                    deliveries++;
                    throw new InvalidOperationException("observer failed");
                },
                scheduler);

        // Act
        person.FirstName = "one";
        person.FirstName = "two";
        inner.RunUntilIdle();

        // Assert
        Assert.Empty(scheduler.Escaped);
        Assert.Equal(2, deliveries);
    }

    [Fact]
    public void WhenTheWriterCarriesAmbientState_ThenTheObserverDoesNotSeeIt()
    {
        // Arrange: an unsuppressed ExecutionContext would let a delivery observe the writer's
        // SubjectTransaction.CurrentTransaction and mutate a pooled, already-returned dictionary.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var ambient = new AsyncLocal<string?>();
        string? observed = "not-run";

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => observed = ambient.Value, scheduler);

        // Act
        ambient.Value = "writer-scope";
        person.FirstName = "one";
        ambient.Value = null;
        scheduler.RunUntilIdle();

        // Assert
        Assert.Null(observed);
    }
```

- [ ] **Step 2: Run tests to verify they fail or pass**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionProtocolTests"`
Expected: 13 tests. The eight new ones exercise code Task 4 already wrote, so they should PASS. If any fails, the Task 4 implementation is wrong; fix `ScheduledPropertySubscription.cs` rather than weakening the test.

Note on `WhenTheWriterCarriesAmbientState_...`: `ControllableScheduler` captures nothing itself, so this test passes only because `ScheduleDrain` suppresses flow before calling `Schedule`. To confirm the test is real, temporarily delete the `SuppressFlow` branch, re-run, and see it fail with `"writer-scope"`. Restore it afterwards.

- [ ] **Step 3: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionProtocolTests.cs \
        src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs
git commit -m "test: Pin disposal, fault and ambient-state behaviour of the dispatcher"
```

---

### Task 6: The typed `SubscribeToProperty` overloads and argument guards

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionTests.cs` (create)

**Interfaces:**
- Consumes: `ScheduledPropertySubscription.Create`, `ResolveDirectPropertyName`, `DelegateObserver`.
- Produces: the two `SubscribeToProperty` scheduled overloads named in the spec's API surface.

- [ ] **Step 1: Write the failing tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionTests.cs`:

```csharp
using System.Linq.Expressions;
using System.Reactive.Concurrency;

using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class ScheduledPropertySubscriptionTests
{
    public ScheduledPropertySubscriptionTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSubscribedByTypedSelector_ThenChangesAreDeliveredOnTheScheduler()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = person.SubscribeToProperty(
            x => x.FirstName,
            (in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()),
            scheduler);

        // Act
        person.FirstName = "Rico";

        // Assert
        Assert.Empty(received);
        scheduler.RunUntilIdle();
        Assert.Equal(["Rico"], received);
    }

    [Fact]
    public void WhenSelectorIsNotADirectPropertyAccess_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => person.SubscribeToProperty(
            x => x.Father!.FirstName,
            (in SubjectPropertyChange _) => { },
            scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenAnyScheduledSubscribeArgumentIsNull_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();

        // Act & Assert: a bare null is ambiguous between the observer and callback overloads, so each
        // needs its own cast, exactly as the unscheduled guard test does.
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (IPropertyChangeObserver)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (PropertyChangeCallback)null!, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, null!));
        Assert.Throws<ArgumentNullException>(() => ((Person)null!).SubscribeToProperty(x => x.FirstName, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Throws<ArgumentNullException>(() => person.SubscribeToProperty((Expression<Func<Person, string?>>)null!, (in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenSchedulerIsSynchronous_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        // Act & Assert: both spellings are reference-equal to the singletons.
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, ImmediateScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.Immediate));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, CurrentThreadScheduler.Instance));
        Assert.Throws<ArgumentException>(() => property.Subscribe((in SubjectPropertyChange _) => { }, Scheduler.CurrentThread));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }

    [Fact]
    public void WhenPropertyIsNotIntercepted_ThenThrowsAndCountStaysZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            new PropertyReference(person, "NotAProperty").Subscribe((in SubjectPropertyChange _) => { }, scheduler));
        Assert.Equal(0, PropertyChangeSubscriptions.ReadSubscriptionCount());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionTests"`
Expected: build FAILS with `CS1501: No overload for method 'SubscribeToProperty' takes 3 arguments`.

- [ ] **Step 3: Add the two overloads**

In `PropertyChangeSubscriptionExtensions.cs`, insert after the scheduled `Subscribe` callback overload:

```csharp
    /// <summary>
    /// Strongly-typed scheduled subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToProperty(x => x.Temperature, observer, scheduler)</c>.
    /// </summary>
    /// <remarks>
    /// Same contract as
    /// <see cref="Subscribe(PropertyReference, IPropertyChangeObserver, IScheduler, Action{Exception})"/>,
    /// and the same selector restriction as
    /// <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.
    /// </remarks>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(propertySelector);

        var name = ResolveDirectPropertyName(propertySelector);
        return new PropertyReference(subject, name).Subscribe(observer, scheduler, onError);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver, IScheduler, Action{Exception})"/>.</summary>
    public static ScheduledPropertySubscription SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback,
        IScheduler scheduler,
        Action<Exception>? onError = null)
        where TSubject : IInterceptorSubject
    {
        // Wrapping first would bypass the callback null guard and fail on a writer thread at dispatch time.
        ArgumentNullException.ThrowIfNull(callback);
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback), scheduler, onError);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionTests"`
Expected: PASS, 5 tests.

If `WhenAnyScheduledSubscribeArgumentIsNull_...` fails on the `subject` or `propertySelector` null cases, note the ordering: `ArgumentNullException.ThrowIfNull(subject)` must run before `ResolveDirectPropertyName`, which is already the case above.

- [ ] **Step 5: Accept the public API snapshot**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL with a received file.

Confirm the diff adds exactly the four scheduled overloads and the `ScheduledPropertySubscription` type with `PendingCount` and `Dispose`, then:

```bash
mv src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run the same filter. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs \
        src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionTests.cs \
        src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
git commit -m "feat: Add scheduled SubscribeToProperty overloads and reject synchronous schedulers"
```

---

### Task 7: Delivery semantics and lifecycle

The three highest-risk promises, plus dormancy, shared observers, and thread economy.

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionTests.cs` (append)

**Interfaces:**
- Consumes: everything from Tasks 3 to 6, plus the existing `BlockingWriteInterceptor`.
- Produces: nothing.

- [ ] **Step 1: Write the failing tests**

Append to `ScheduledPropertySubscriptionTests`:

```csharp
    [Fact]
    public void WhenObserverThrows_ThenTheSetterReturnsNormally()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var scheduler = new ControllableScheduler();

        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => throw new InvalidOperationException("boom"), scheduler);

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal("one", person.FirstName);
    }

    [Fact]
    public void WhenScheduledObserverThrows_ThenAnUnscheduledListenerOnTheSamePropertyStillFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var scheduler = new ControllableScheduler();
        var unscheduled = new List<string?>();

        using var scheduled = property.Subscribe(
            (in SubjectPropertyChange _) => throw new InvalidOperationException("boom"),
            scheduler);
        using var plain = property.Subscribe((in SubjectPropertyChange change) => unscheduled.Add(change.GetNewValue<string?>()));

        // Act
        person.FirstName = "one";
        scheduler.RunUntilIdle();

        // Assert: the scheduled observer's failure cannot suppress another channel on the same write.
        Assert.Equal(["one"], unscheduled);
    }

    [Fact]
    public async Task WhenWriteCommitsAfterSubscribeReturns_ThenItIsDelivered()
    {
        // Arrange: the blocker parks the writer after PropertyChangeInterceptor's pre-commit work and
        // before the terminal commit, so the subscription installs mid-write.
        var blocker = new BlockingWriteInterceptor();
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        context.WithService(() => blocker);
        var person = new Person(context);
        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        var writer = Task.Run(() => person.FirstName = "John");
        Assert.True(blocker.EnteredInnerChain.Wait(TimeSpan.FromSeconds(10)));

        // Act
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);
        blocker.ProceedWithCommit.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["John"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedWithChangesQueued_ThenThoseChangesAreStillDelivered()
    {
        // Arrange: dormancy stops acceptance, not the drain. This is the one place the scheduled path
        // does not inherit the unscheduled semantics, and it is the opposite of disposal.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        child.FirstName = "one";

        // Act
        parent.Father = null;
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["one"], received);
    }

    [Fact]
    public void WhenSubjectIsDetachedAndReattached_ThenTheSubscriptionRevives()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;

        var scheduler = new ControllableScheduler();
        var received = new List<string?>();

        using var subscription = new PropertyReference(child, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange change) => received.Add(change.GetNewValue<string?>()), scheduler);

        parent.Father = null;
        child.FirstName = "dorm";
        scheduler.RunUntilIdle();
        Assert.Empty(received);

        // Act
        parent.Father = child;
        child.FirstName = "live";
        scheduler.RunUntilIdle();

        // Assert
        Assert.Equal(["live"], received);
    }

    [Fact]
    public void WhenOneObserverIsSharedAcrossTwoSubscriptions_ThenTheyAreNotSerializedWithEachOther()
    {
        // Arrange: the guarantee is per subscription, not per observer instance. Two subscriptions each
        // drain independently, so a shared observer is invoked from both.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var person = new Person(context);
        var firstScheduler = new ControllableScheduler();
        var secondScheduler = new ControllableScheduler();
        var calls = 0;

        void Handler(in SubjectPropertyChange _) => calls++;

        using var first = new PropertyReference(person, nameof(Person.FirstName)).Subscribe(Handler, firstScheduler);
        using var second = new PropertyReference(person, nameof(Person.LastName)).Subscribe(Handler, secondScheduler);

        // Act
        person.FirstName = "one";
        person.LastName = "two";
        firstScheduler.RunUntilIdle();
        secondScheduler.RunUntilIdle();

        // Assert
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WhenManyPropertiesAreSubscribed_ThenNoThreadIsDedicatedPerSubscription()
    {
        // Arrange
        const int subscriptionCount = 100;

        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        var people = Enumerable.Range(0, subscriptionCount).Select(_ => new Person(context)).ToList();
        var threadIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        using var allDelivered = new CountdownEvent(subscriptionCount);
        var subscriptions = people
            .Select(person => new PropertyReference(person, nameof(Person.FirstName))
                .Subscribe(
                    (in SubjectPropertyChange _) =>
                    {
                        threadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                        allDelivered.Signal();
                    },
                    Scheduler.Default))
            .ToList();

        try
        {
            // Act
            foreach (var person in people)
            {
                person.FirstName = "Rico";
            }

            Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(30)));

            // Assert: the thread-per-subscription regression produces exactly subscriptionCount distinct
            // ids. This does not catch an unbounded drain, which occupies threads without adding ids.
            Assert.True(
                threadIds.Count < subscriptionCount / 2,
                $"{threadIds.Count} distinct delivery threads for {subscriptionCount} subscriptions");
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }
```

- [ ] **Step 2: Run tests to verify**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~ScheduledPropertySubscriptionTests"`
Expected: PASS, 12 tests.

If `WhenSubjectIsDetachedWithChangesQueued_...` fails because no change was queued, check that `WithFullPropertyTracking()` is used (it includes lifecycle) and that the write happens while the child is still attached.

- [ ] **Step 3: Run the whole tracking suite**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests`
Expected: PASS. The conventions test must be green, which proves every new file joined the serialized collection.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Tracking.Tests/Change/ScheduledPropertySubscriptionTests.cs
git commit -m "test: Cover scheduled delivery semantics, dormancy and thread economy"
```

---

### Task 8: Adversarial protocol review gate

The dispatcher is not done until this passes. The previous two drafts of this protocol each had defects that only a hostile reading found, so this is a required step rather than a nicety.

**Files:**
- Read: `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs`
- Modify: whatever the review finds.

**Interfaces:**
- Consumes: the finished dispatcher and its tests.
- Produces: either a clean bill or fixes plus regression tests.

- [ ] **Step 1: Write the hazardous-interleaving argument into the source**

Add a comment block at the top of `ScheduledPropertySubscription` covering each pair, naming the field, the ordering primitive, and the property preserved, in the style of `PropertyChangeSubscription.cs:56-60`:

- enqueue against enqueue
- enqueue against a settling drain
- dispose against enqueue
- dispose against a mid-flight delivery
- fault against dispose
- a throwing `ScheduleDrain` against the counter
- a drain exit against the next drain entry
- subject detach against queued changes

- [ ] **Step 2: Dispatch the review**

Dispatch a subagent with this prompt, substituting the worktree path:

> Adversarially review the dispatch protocol in `src/Namotion.Interceptor.Tracking/Change/ScheduledPropertySubscription.cs` against `docs/superpowers/specs/2026-08-12-scheduled-property-subscriptions-design.md`. Do not modify files. Write throwaway probes outside the repo if useful. Find an interleaving that loses a change, delivers one twice, re-enters the observer within one subscription, exits a drain with work outstanding, spins without progress, holds a scheduler thread indefinitely, leaks the upstream subscription, or decrements the process-wide count more than once. Check the `finally` settle, the `processed++` placement, the `MaxBatch` handoff, the `CompareExchange` state machine, the `Create` upstream-assignment race, and `ExecutionContext` suppression including `IsFlowSuppressed`. Report findings most severe first with a concrete failure scenario each, or state plainly that you found none.

- [ ] **Step 3: Apply findings**

For each confirmed finding, add a failing test first, then fix, then re-run. If none, record that in the commit message.

- [ ] **Step 4: Run the full solution**

Run: `dotnet build src/Namotion.Interceptor.slnx` then `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "docs: Record the dispatch protocol's interleaving argument in source"
```

---

### Task 9: Documentation

**Files:**
- Modify: `docs/tracking.md`
- Modify: `src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeCallback.cs`

**Interfaces:**
- Consumes: the finished API.
- Produces: nothing.

- [ ] **Step 1: Amend `IPropertyChangeObserver` XML docs**

Replace the summary in `src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs`:

```csharp
/// <summary>
/// Per-property change observer. What it must guarantee depends on how it was subscribed.
/// <para>
/// Unscheduled, through <c>Subscribe(property, observer)</c>: OnChange runs on the writing thread, inside
/// the write, outside the subject lock. Implementations MUST be thread-safe (they may be invoked
/// concurrently), fast, non-blocking, and MUST NOT throw, because a throw propagates out of the setter and
/// suppresses later deliveries for that write.
/// </para>
/// <para>
/// Scheduled, through <c>Subscribe(property, observer, scheduler, onError)</c>: OnChange runs on the
/// scheduler and MAY throw, which is reported to onError and leaves the subscription live. It is never
/// re-entered within one subscription, so it needs no synchronization of its own, but one instance shared
/// across several subscriptions is still invoked concurrently and must synchronize.
/// </para>
/// Deliveries may arrive out of commit order under concurrent writes to the same property in both cases;
/// re-read the property if you need the current value.
/// </summary>
```

- [ ] **Step 2: Amend `PropertyChangeCallback` XML docs**

Give the delegate the same split, pointing at `IPropertyChangeObserver` for the detail rather than repeating it.

- [ ] **Step 3: Add the scheduled-delivery subsection to `docs/tracking.md`**

After the Per-Property Subscriptions section's "Ownership and lifetime" paragraph, add a `#### Scheduled delivery` subsection covering: the four overloads; serialization being per subscription; error isolation and the `onError` contract's four axes; the rejection of synchronous schedulers and why `property.Subscribe(callback)` is the synchronous option; the unbounded queue and `PendingCount`; the detach asymmetry; disposal dropping and releasing queued changes; the caller owning scheduler lifetime; and ambient context not flowing.

Paragraphs go on one line. No em dashes.

- [ ] **Step 4: Add the fifth channel-table row**

In the table at `docs/tracking.md:155-164`, after the `ChangeQueueProcessor` row:

```markdown
| Scheduled per-property callback | conditional (a) | arrival | scheduler |
```

Use "arrival" to match the other rows rather than introducing new vocabulary.

- [ ] **Step 5: Amend the two concurrency bullets**

- "Per-property observers are not serialized" (`:144`): note that the scheduled overloads serialize per subscription, and that a shared observer across subscriptions is still concurrent.
- "Throwing synchronous observers suppress later deliveries" (`:145`): note that the scheduled path isolates the observer from the write entirely.

- [ ] **Step 6: Add a "Composing with Rx" note**

Introduce `GetSynchronousChangeObservable`, state that it has layer 0's contract exactly rather than a safer one, and carry both traps: `ObserveOn` dedicating a thread per subscription under `ISchedulerLongRunning`, and an exception reaching an `ObserveOn` sink terminating the process.

- [ ] **Step 7: Verify no em dashes and build**

Run: `grep -c "—" docs/tracking.md src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs src/Namotion.Interceptor.Tracking/Change/PropertyChangeCallback.cs`
Expected: `0` for each file.

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS, no warnings.

- [ ] **Step 8: Commit**

```bash
git add docs/tracking.md \
        src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs \
        src/Namotion.Interceptor.Tracking/Change/PropertyChangeCallback.cs
git commit -m "docs: Document scheduled per-property delivery and its contract"
```

---

### Task 10: Benchmarks

Split deliberately. A live scheduled subscription in the write-throughput benchmark would build a backlog across the millions of operations BenchmarkDotNet runs, and the allocations-per-operation column would then measure the backlog rather than the write.

**Files:**
- Modify: `src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs`

**Interfaces:**
- Consumes: the scheduled `Subscribe` overload and `PendingCount`.
- Produces: nothing.

- [ ] **Step 1: Add the write-side benchmark case**

Add a `ScheduledSubscription` state alongside the existing ones: a `[GlobalSetup]` that installs one scheduled subscription on `Scheduler.Default` with a no-op observer, a `[Benchmark]` that performs one write, and an `[IterationCleanup]` that spins until `PendingCount` is zero so no backlog carries between iterations.

Name it so the column reads as write-side cost, for example `WriteWithScheduledSubscription`.

- [ ] **Step 2: Add the delivery benchmark**

Add a separate `[Benchmark]` with a bounded producer: enqueue a fixed count inside the operation and drain to completion within it, so the measured allocation is per delivery rather than per backlog.

- [ ] **Step 3: Run locally**

Run: `pwsh scripts/benchmark.ps1 -Filter "*PropertyChangeSubscriptionsBenchmark*" -LocalOnly`
Expected: completes. Record the two new rows' allocation figures in the commit message.

The spec predicts roughly 120 bytes per change on `Scheduler.Default` with one change per burst, or about 200 when the writer carries an `AsyncLocal`. A figure far outside that range means the drain is batching differently than intended; investigate before committing.

- [ ] **Step 4: Commit**

```bash
git add src/Namotion.Interceptor.Benchmark/PropertyChangeSubscriptionsBenchmark.cs
git commit -m "perf: Benchmark scheduled subscription write cost and delivery cost separately"
```

---

## Self-Review

**Spec coverage.** Every API-surface member maps to a task: `GetSynchronousChangeObservable` to Task 2, the two `PropertyReference` scheduled overloads and the whole dispatch protocol to Task 4, the two `SubscribeToProperty` overloads and every guard to Task 6, `ScheduledPropertySubscription` and `PendingCount` to Task 4. The spec's dispatch protocol, faulting, and `ExecutionContext` sections map to Tasks 4, 5 and 8. The Testing section's groups map to Tasks 2, 4, 5, 6 and 7. Documentation obligations map to Task 9, benchmarking to Task 10, and the conventions-marker prerequisite to Task 1.

**Two spec test items are deliberately not implemented as written, and this is the gap to accept or close:**
- "Concurrent thread occupancy stays bounded across many saturated subscriptions." The bounded-drain handoff is pinned deterministically instead, by `WhenMoreThanOneBatchIsQueued_ThenTheDrainYieldsAndHandsOffInsteadOfLooping` asserting the scheduler call count grows. A wall-clock occupancy test against `Scheduler.Default` would be exactly the flaky, timing-dependent assertion the project's test conventions forbid.
- "Delivery N+1 observes state written by delivery N across scheduler threads." Covered indirectly by the unsynchronized `delivered++` counter in `WhenManyWritersRaceOneProperty_...`, which would tear if the happens-before chain were broken.

**Placeholder scan.** No TBD, TODO, or "similar to Task N". Task 9's steps 3 and 6 describe prose to write rather than quoting it, which is appropriate for documentation; every code step carries its code.

**Type consistency.** `ScheduledPropertySubscription.Create`, `PendingCount`, `WorkInProgressForTests`, `ReentrancyCountForTests`, `EnableReentrancyInstrumentation`, and `MaxBatch` are declared in Task 4 and used with those exact names in Tasks 5, 6, 7 and 10. `ControllableScheduler.RunOne`, `RunAll`, `RunUntilIdle`, `ScheduleCallCount`, and `QueuedCount`, plus `RecordingScheduler.Escaped` and `BlackHoleScheduler.ScheduleCallCount`, are declared in Task 3 and used with those names afterwards. `ThrowIfSynchronous` and `DelegateObserver` are declared in Task 4 and reused in Task 6.
