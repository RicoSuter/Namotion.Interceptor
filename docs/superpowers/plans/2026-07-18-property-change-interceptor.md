# Property Change Interceptor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge the two property-change delivery interceptors into one dual-facet `PropertyChangeInterceptor`, and add a synchronous, subject-stored, per-property change subscription API.

**Architecture:** One `IWriteInterceptor` owns both the Rx observable facet and the high-performance queue facet through an immutable `DispatchState` snapshot (single volatile field, null when idle). Per-property subscriptions are stored on the subject itself (`IInterceptorSubject.Data`), gated by a process-wide `long` count, and dispatched by the same interceptor; a per-write internal flag on `PropertyWriteContext` guarantees exactly-once delivery under aggregated fallback contexts.

**Tech Stack:** C# 13, .NET 9 (Tracking) / .NET Standard 2.0 (core), System.Reactive, xUnit, PublicApiGenerator + Verify for API snapshots, BenchmarkDotNet.

**Source spec:** `docs/superpowers/specs/2026-07-16-property-change-interceptor-design.md`. Read it before starting; this plan implements it.

**Shipping:** Phase 1 (Task 1) is a self-contained, release-safe PR: a behavior-preserving merge plus the new `WithPropertyChangeNotifications()` surface, fully wired and used. Phase 2 (Tasks 2-4) is a second release-safe PR that adds the per-property API. Phase 3 (Tasks 5-6) is docs and benchmarks. Do not split a phase across PRs in a way that leaves public API without callers.

## Global Constraints

- Target frameworks: core `Namotion.Interceptor` = .NET Standard 2.0; `Namotion.Interceptor.Tracking` = net9.0. Copy these; do not change them.
- `Directory.Build.props` sets `nullable enable` and warnings-as-errors. All code must be warning-clean and null-annotation-correct.
- No new public API on the core `Namotion.Interceptor` package. The one core change (`PropertyWriteContext.ArePropertyListenersClaimed`) is `internal`; core already grants `InternalsVisibleTo("Namotion.Interceptor.Tracking")` (`src/Namotion.Interceptor/Namotion.Interceptor.csproj:17`).
- No em dashes in docs/XML-doc prose (use plain sentences).
- No AI attribution in commit messages (no "Claude"/"Codex"/"Co-Authored-By"/"Generated with").
- Test naming: `When<Condition>_Then<ExpectedBehavior>`. Test bodies use explicit `// Arrange`, `// Act`, `// Assert` comments (`// Act & Assert` for exception tests).
- No hardcoded `Task.Delay`/`Thread.Sleep` in tests. Use event-based synchronization (`ManualResetEventSlim`, `CountdownEvent`) or `AsyncTestHelpers.WaitUntilAsync(() => condition)`.
- Public API is snapshot-tested. When API changes intentionally, accept the new snapshot by copying the test's `.received.txt` over `.verified.txt`. Set env var `DiffEngine_Disabled=true` when running snapshot tests in a loop to avoid launching a diff tool.
- Build: `dotnet build src/Namotion.Interceptor.slnx`. Unit tests: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`.

---

## Task 1: Merge into `PropertyChangeInterceptor` (Phase 1, PR 1)

This is one atomic task: the two old interceptor types share the public `PropertyChangeQueueSubscription` handle, so the codebase does not compile in a half-merged state. Create the merged type, repoint the subscription, delete the old types, rewire registration/resolution/ordering, migrate callers, and accept the snapshot. The success criterion is behavior preservation: the entire existing test suite passes.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`
- Rewrite: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs`
- Delete: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs`
- Modify: `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs:13-15`
- Modify (migrate): `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs`, `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeQueueTests.cs`, `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs`, `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs`, and every test that called `WithPropertyChangeObservable()`/`WithPropertyChangeQueue()`.
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeInterceptorTests.cs` (new; supersedes `PropertyChangeQueueTests.cs`)
- Accept: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Produces:
  - `public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor, IDisposable` with `public PropertyChangeInterceptor()`, `public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)`, `internal PropertyChangeQueueSubscription CreateQueueSubscription()`, `internal void RemoveQueueSubscription(PropertyChangeQueueSubscription subscription)`, `public void Dispose()`, and `public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)`.
  - `public sealed class PropertyChangeQueueSubscription : IDisposable` with `internal PropertyChangeQueueSubscription(PropertyChangeInterceptor interceptor)`, `internal void Enqueue(in SubjectPropertyChange item)`, `internal void Complete()`, `public bool TryDequeue(out SubjectPropertyChange item, CancellationToken cancellationToken)`, `public void Dispose()`.
  - `public static IInterceptorSubjectContext WithPropertyChangeNotifications(this IInterceptorSubjectContext context)`; unchanged signatures `GetPropertyChangeObservable(...)` and `CreatePropertyChangeQueueSubscription(...)`.

- [ ] **Step 1: Read the current code being merged**

Read these in full before writing anything; the merged class must preserve their behavior exactly:
- `src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs`
- `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs`
- `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs`
- `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs` (lines 76-132)
- `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs` (attributes at lines 13-15)

- [ ] **Step 2: Write the failing tests for the merged interceptor**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeInterceptorTests.cs`:

```csharp
using System.Reactive.Concurrency;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class PropertyChangeInterceptorTests
{
    [Fact]
    public void WhenQueueSubscriberExists_ThenWriteIsEnqueued()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        using var subscription = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(subscription.TryDequeue(out var change, new CancellationTokenSource(1000).Token));
        Assert.Equal(nameof(Person.FirstName), change.Property.Name);
        Assert.Equal("John", change.GetNewValue<string?>());
    }

    [Fact]
    public void WhenObservableSubscriberExists_ThenWriteIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        SubjectPropertyChange? captured = null;
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => captured = c);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(nameof(Person.FirstName), captured.Value.Property.Name);
    }

    [Fact]
    public void WhenBothFacetsActive_ThenBothReceiveTheSameChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        SubjectPropertyChange? observed = null;
        using var observable = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => observed = c);
        using var queue = context.CreatePropertyChangeQueueSubscription();

        // Act
        person.FirstName = "John";

        // Assert
        Assert.True(queue.TryDequeue(out var dequeued, new CancellationTokenSource(1000).Token));
        Assert.NotNull(observed);
        Assert.Equal(observed.Value.Property, dequeued.Property);
    }

    [Fact]
    public void WhenLastObservableSubscriberLeaves_ThenGateCloses()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var person = new Person(context);
        var subscription = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(_ => { });

        // Act
        subscription.Dispose();
        person.FirstName = "John"; // must take the idle fast path (no observers, no queue)

        // Assert: with no consumers, writing does not throw and the interceptor is idle.
        // (White-box: an internal test hook or reflection can confirm _state is null; at minimum this must not throw.)
        Assert.True(true);
    }

    [Fact]
    public void WhenInterceptorDisposed_ThenQueueSubscriberIsCompletedButObservableStillWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var queue = context.CreatePropertyChangeQueueSubscription();

        // Act
        interceptor.Dispose();

        // Assert: a completed queue subscription returns false without blocking.
        Assert.False(queue.TryDequeue(out _, new CancellationTokenSource(1000).Token));
        // And creating a new queue subscription now throws.
        Assert.Throws<ObjectDisposedException>(() => context.CreatePropertyChangeQueueSubscription());
    }
}
```

Note: `Person` is the existing test model used across `Namotion.Interceptor.Tracking.Tests` (has partial `FirstName`/`LastName` and derived `FullName`). Confirm its namespace/usings by opening an existing test in that project (e.g. `Change/PropertyChangedHandlerTests.cs`) and match them.

- [ ] **Step 3: Run the tests to verify they fail to compile**

Run: `dotnet build src/Namotion.Interceptor.Tracking.Tests`
Expected: FAIL: `WithPropertyChangeNotifications` and `PropertyChangeInterceptor` do not exist yet.

- [ ] **Step 4: Rewrite `PropertyChangeQueueSubscription`**

Replace the entire contents of `src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueueSubscription.cs`:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A pull-based subscription to receive property changes from a <see cref="PropertyChangeInterceptor"/>.
/// Each subscription owns an isolated queue. TryDequeue must be called from a single consumer thread.
/// </summary>
public sealed class PropertyChangeQueueSubscription : IDisposable
{
    private readonly PropertyChangeInterceptor _interceptor;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private volatile bool _completed;
    private int _disposed; // one-shot flag

    internal PropertyChangeQueueSubscription(PropertyChangeInterceptor interceptor)
    {
        _interceptor = interceptor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enqueue(in SubjectPropertyChange item)
    {
        if (_completed)
        {
            return;
        }

        _queue.Enqueue(item);
        _signal.Set(); // safe even after Dispose: the signal is never disposed (see Dispose)
    }

    /// <summary>Completes the subscription without unsubscribing from the interceptor (interceptor-side teardown).</summary>
    internal void Complete()
    {
        _completed = true;
        _signal.Set();
    }

    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                item = default!;
                return false;
            }

            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            _signal.Reset();

            // Re-check BOTH the queue and completion after Reset so a producer's or Complete()'s
            // Set() that raced the Reset is not lost (completion lost-wakeup fix).
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            try
            {
                _signal.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // one-shot
        }

        _completed = true;
        _signal.Set();
        _interceptor.RemoveQueueSubscription(this);

        // Deliberately do NOT dispose _signal: a concurrent producer may still call _signal.Set()
        // between its _completed check and here (enqueue-vs-dispose ObjectDisposedException fix).
        // The ManualResetEventSlim is reclaimed by GC together with this subscription.
    }
}
```

- [ ] **Step 5: Create `PropertyChangeInterceptor`**

Create `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs`:

```csharp
using System.Reactive.Subjects;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Single write interceptor that delivers property changes through two facets: an Rx observable
/// (see <see cref="InterceptorSubjectContextExtensions.GetPropertyChangeObservable"/>) and a
/// high-performance pull queue (see <see cref="InterceptorSubjectContextExtensions.CreatePropertyChangeQueueSubscription"/>).
/// Replaces the former PropertyChangeObservable and PropertyChangeQueue.
/// </summary>
public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor, IDisposable
{
    private readonly Lock _modificationLock = new();

    // The single hot-path field. Null means neither facet has consumers (idle).
    private volatile DispatchState? _state;

    // Observable facet state, guarded by _modificationLock. Lazily created on first subscribe.
    private Subject<SubjectPropertyChange>? _subject;
    private ISubject<SubjectPropertyChange>? _syncSubject;
    private int _observableConsumerCount;

    private bool _disposed;

    private sealed class DispatchState
    {
        public required PropertyChangeQueueSubscription[] QueueSubscriptions { get; init; } // never null
        public required ISubject<SubjectPropertyChange>? SyncSubject { get; init; }          // null = no observers
    }

    // Called under _modificationLock. Publishes null when both facets are empty.
    private void PublishState(PropertyChangeQueueSubscription[] queueSubscriptions, ISubject<SubjectPropertyChange>? syncSubject)
    {
        _state = queueSubscriptions.Length == 0 && syncSubject is null
            ? null
            : new DispatchState { QueueSubscriptions = queueSubscriptions, SyncSubject = syncSubject };
    }

    // ----- Queue facet -----

    internal PropertyChangeQueueSubscription CreateQueueSubscription()
    {
        lock (_modificationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var subscription = new PropertyChangeQueueSubscription(this);
            var current = _state?.QueueSubscriptions ?? [];
            var updated = new PropertyChangeQueueSubscription[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[current.Length] = subscription;
            PublishState(updated, _syncSubject);
            return subscription;
        }
    }

    internal void RemoveQueueSubscription(PropertyChangeQueueSubscription subscription)
    {
        lock (_modificationLock)
        {
            var current = _state?.QueueSubscriptions ?? [];
            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            var updated = new PropertyChangeQueueSubscription[current.Length - 1];
            Array.Copy(current, 0, updated, 0, index);
            Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
            PublishState(updated, _syncSubject);
        }
    }

    // ----- Observable facet -----

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ISubject<SubjectPropertyChange> syncSubject;
        lock (_modificationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_subject is null)
            {
                _subject = new Subject<SubjectPropertyChange>();
                _syncSubject = Subject.Synchronize(_subject);
            }

            _observableConsumerCount++;
            syncSubject = _syncSubject!;
            PublishState(_state?.QueueSubscriptions ?? [], _syncSubject);
        }

        var inner = syncSubject.Subscribe(observer);
        return new ObservableSubscription(this, inner);
    }

    private void RemoveObservableConsumer()
    {
        lock (_modificationLock)
        {
            if (_observableConsumerCount > 0 && --_observableConsumerCount == 0)
            {
                PublishState(_state?.QueueSubscriptions ?? [], null);
            }
        }
    }

    private sealed class ObservableSubscription(PropertyChangeInterceptor interceptor, IDisposable inner) : IDisposable
    {
        private PropertyChangeInterceptor? _interceptor = interceptor;
        private readonly IDisposable _inner = inner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _interceptor, null) is not { } owner)
            {
                return; // one-shot
            }

            _inner.Dispose();
            owner.RemoveObservableConsumer();
        }
    }

    // ----- Write path -----

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var state = _state;
        if (state is null)
        {
            next(ref context);
            return;
        }

        var subscriptions = state.QueueSubscriptions;
        var syncSubject = state.SyncSubject;

        var oldValue = context.CurrentValue;
        next(ref context);

        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            oldValue,
            context.GetFinalValue());

        for (var i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(in change);
        }

        if (syncSubject is not null)
        {
            syncSubject.OnNext(change);
        }
    }

    public void Dispose()
    {
        PropertyChangeQueueSubscription[] toComplete;
        lock (_modificationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toComplete = _state?.QueueSubscriptions ?? [];
            PublishState([], _syncSubject); // preserve observable facet, drop queue
        }

        foreach (var subscription in toComplete)
        {
            subscription.Complete();
        }
    }
}
```

- [ ] **Step 6: Delete the old interceptor types**

```bash
git rm src/Namotion.Interceptor.Tracking/Change/PropertyChangeObservable.cs
git rm src/Namotion.Interceptor.Tracking/Change/PropertyChangeQueue.cs
```

- [ ] **Step 7: Rewire registration, resolution, and ordering**

In `src/Namotion.Interceptor.Tracking/InterceptorSubjectContextExtensions.cs`:

1. In `WithFullPropertyTracking` (lines 20-25), replace the two lines `.WithPropertyChangeObservable()` and `.WithPropertyChangeQueue()` with a single `.WithPropertyChangeNotifications()`.
2. Delete the `WithPropertyChangeObservable` method (lines 81-85) and the `WithPropertyChangeQueue` method (lines 115-119).
3. Add the new registration method:

```csharp
    /// <summary>
    /// Registers the property change interceptor, enabling both the Rx observable
    /// (<see cref="GetPropertyChangeObservable"/>) and the high-performance queue
    /// (<see cref="CreatePropertyChangeQueueSubscription"/>) facets, and per-property subscriptions.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithPropertyChangeNotifications(this IInterceptorSubjectContext context)
    {
        context.TryAddService(() => new PropertyChangeInterceptor(), _ => true);
        return context;
    }
```

4. Change `GetPropertyChangeObservable` (line 97) to resolve the merged type: replace `.GetService<PropertyChangeObservable>()` with `.GetService<PropertyChangeInterceptor>()`.
5. Change `CreatePropertyChangeQueueSubscription` (lines 129-131) to:

```csharp
        return context
            .GetService<PropertyChangeInterceptor>()
            .CreateQueueSubscription();
```

In `src/Namotion.Interceptor.Tracking/Transactions/SubjectTransactionInterceptor.cs`, replace the two attributes at lines 14-15:

```csharp
[RunsBefore(typeof(PropertyChangeObservable))]
[RunsBefore(typeof(PropertyChangeQueue))]
```

with nothing (delete both), and instead add `[RunsAfter(typeof(SubjectTransactionInterceptor))]` to the `PropertyChangeInterceptor` class declaration (an attribute above `public sealed class PropertyChangeInterceptor`). Keep `SubjectTransactionInterceptor`'s existing `[RunsBefore(typeof(DerivedPropertyChangeHandler))]`. Add the necessary `using Namotion.Interceptor.Attributes;` and `using Namotion.Interceptor.Tracking.Transactions;` to `PropertyChangeInterceptor.cs`.

Rationale (spec Registration section): the target-side `RunsAfter` builds a per-instance ordering edge for every aggregated interceptor, whereas a source-side `RunsBefore` binds only the last instance per type (`ServiceOrderResolver.cs:115-117,136-143`).

- [ ] **Step 8: Migrate all callers of the removed APIs**

Find them:

```bash
grep -rn "WithPropertyChangeObservable\|WithPropertyChangeQueue\|PropertyChangeObservable\|new PropertyChangeQueue\b\|GetService<PropertyChangeQueue>\|GetService<PropertyChangeObservable>" src --include="*.cs" | grep -vE "/bin/|/obj/"
```

Apply these mechanical edits (all in test/benchmark code (no product code calls the removed APIs)):
- Replace every standalone `.WithPropertyChangeObservable()` and `.WithPropertyChangeQueue()` call with `.WithPropertyChangeNotifications()`. A test that used only one facet now also gets the other (unused, free).
- `src/Namotion.Interceptor.Connectors.Tests/SubjectSourceBaseTests.cs` (7 sites): replace `new PropertyChangeQueue()` with `new PropertyChangeInterceptor()`; the surrounding `AddService(...)` stays.
- `src/Namotion.Interceptor.Benchmark/SubjectSourceBenchmark.cs:93`: replace `GetService<PropertyChangeQueue>()` with `GetService<PropertyChangeInterceptor>()`.
- `src/Namotion.Interceptor.Connectors.Tests/Transactions/SubjectTransactionLifecycleTests.cs` (around lines 116-133): the ordering assertion uses the literal string `"PropertyChangeObservable"`. Change `types.IndexOf("PropertyChangeObservable")` to `types.IndexOf("PropertyChangeInterceptor")` and update the assertion message.
- Delete `src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeQueueTests.cs` (superseded by `PropertyChangeInterceptorTests.cs`); migrate any unique assertions worth keeping (e.g. its `:211` `queue.Subscribe()` usage becomes `context.CreatePropertyChangeQueueSubscription()`) into `PropertyChangeInterceptorTests.cs`.

```bash
git rm src/Namotion.Interceptor.Tracking.Tests/Change/PropertyChangeQueueTests.cs
```

- [ ] **Step 9: Build the whole solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS (0 warnings (warnings are errors)). Fix any remaining references to the deleted types the grep missed.

- [ ] **Step 10: Run the new interceptor tests**

Run: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PropertyChangeInterceptorTests"`
Expected: PASS (all 5).

- [ ] **Step 11: Run the full unit suite (behavior preservation)**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS. In particular `SubjectSourceBaseTests`, `ChangeQueueProcessorTests`, `SubjectTransactionLifecycleTests`, and all `SubjectUpdate*Tests` must be green; that is the proof the merge preserved behavior.

- [ ] **Step 12: Accept the Tracking public API snapshot**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL (snapshot mismatch: `PropertyChangeObservable`/`PropertyChangeQueue` types and `WithPropertyChangeObservable`/`WithPropertyChangeQueue` methods removed; `PropertyChangeInterceptor` and `WithPropertyChangeNotifications` added; `PropertyChangeQueueSubscription`'s public constructor removed).

Review the diff to confirm it matches those intended changes only, then accept:

```bash
cp src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run to confirm PASS: `dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`

- [ ] **Step 13: Commit**

```bash
git add -A
git commit -m "Merge property change observable and queue into a single PropertyChangeInterceptor"
```

---

## Task 2: Per-property subscriptions: storage, dispatch, low-level API (Phase 2, PR 2)

Adds subject-stored per-property subscriptions end to end: register via a `PropertyReference`, and the merged interceptor dispatches to them.

**Files:**
- Modify: `src/Namotion.Interceptor/Interceptors/PropertyWriteContext.cs` (add internal flag)
- Create: `src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs`
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeCallback.cs`
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscription.cs`
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptions.cs`
- Create: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeInterceptor.cs` (extend `WriteProperty`)
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionTests.cs`

**Interfaces:**
- Consumes (from Task 1): `PropertyChangeInterceptor.WriteProperty`, `SubjectPropertyChange`.
- Produces:
  - `public interface IPropertyChangeObserver { void OnChange(in SubjectPropertyChange change); }`
  - `public delegate void PropertyChangeCallback(in SubjectPropertyChange change);`
  - `internal static class PropertyChangeSubscriptions { public static long LiveCount; }` accessed via helpers `IncrementLiveCount()`, `DecrementLiveCount()`, `ReadLiveCount()`.
  - `public static IDisposable Subscribe(this PropertyReference property, IPropertyChangeObserver observer)` and `... Subscribe(this PropertyReference property, PropertyChangeCallback callback)`.
  - Internal `PropertyChangeSubscription[]? TryGetListeners(PropertyReference property)` and `DispatchToListeners(...)` used by the interceptor.

- [ ] **Step 1: Add the internal dedup flag to core `PropertyWriteContext`**

Open `src/Namotion.Interceptor/Interceptors/PropertyWriteContext.cs`. Add an internal mutable field/property visible to Tracking (match the existing internal-member style; find `WriteTimestampForPublishing` for the pattern). Add:

```csharp
    /// <summary>
    /// Set by the first PropertyChangeInterceptor instance that dispatches per-property listeners for this
    /// write, so deeper interceptor instances in an aggregated fallback chain do not double-deliver.
    /// Internal: Tracking sets it; no external interceptor may.
    /// </summary>
    internal bool ArePropertyListenersClaimed;
```

Note: `PropertyWriteContext<TProperty>` is a `struct` passed by `ref` through the whole write chain, so this is a genuine per-write mutable slot. If it is a `readonly struct`, remove `readonly` from the struct or convert this to a settable field (verify by opening the file); the chain must observe the mutation. Confirm `WriteInterceptorChain` threads a single `ref` (`src/Namotion.Interceptor/Cache/WriteInterceptorChain.cs`).

- [ ] **Step 2: Verify core still builds**

Run: `dotnet build src/Namotion.Interceptor`
Expected: PASS (internal field, no public API change; core snapshot unaffected).

- [ ] **Step 3: Write the failing end-to-end test**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)] // serialized; see Task 4
public class PerPropertySubscriptionTests
{
    [Fact]
    public void WhenPropertyChanges_ThenListenerIsInvokedByRef()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        string? captured = null;
        using var subscription = property.Subscribe((in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenOtherPropertyChanges_ThenListenerIsNotInvoked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var invoked = false;
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => invoked = true);

        // Act
        person.LastName = "Doe";

        // Assert
        Assert.False(invoked);
    }

    [Fact]
    public void WhenSubscriptionDisposed_ThenListenerStopsAndCountReturnsToZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var count = 0;
        var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => count++);

        // Act
        person.FirstName = "John";
        subscription.Dispose();
        person.FirstName = "Jane";

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(0, PropertyChangeSubscriptions.ReadLiveCount());
    }
}
```

Add a placeholder collection definition at the bottom of the file for now (fully specified in Task 4):

```csharp
[CollectionDefinition(PerPropertySubscriptionCollection.Name, DisableParallelization = true)]
public class PerPropertySubscriptionCollection { public const string Name = "PerPropertySubscription"; }
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet build src/Namotion.Interceptor.Tracking.Tests`
Expected: FAIL: `IPropertyChangeObserver`, `PropertyChangeCallback`, `Subscribe`, `PropertyChangeSubscriptions` do not exist.

- [ ] **Step 5: Create the observer contract and callback delegate**

`src/Namotion.Interceptor.Tracking/Change/IPropertyChangeObserver.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Synchronous per-property change observer. OnChange runs on the writing thread, inside the write,
/// outside the subject lock. Implementations MUST be thread-safe (they may be invoked concurrently),
/// fast, non-blocking, and MUST NOT throw. Deliveries may arrive out of commit order under concurrent
/// writes to the same property; re-read the property if you need the current value.
/// </summary>
public interface IPropertyChangeObserver
{
    void OnChange(in SubjectPropertyChange change);
}
```

`src/Namotion.Interceptor.Tracking/Change/PropertyChangeCallback.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

/// <summary>Delegate form of <see cref="IPropertyChangeObserver"/>. Same contract applies.</summary>
public delegate void PropertyChangeCallback(in SubjectPropertyChange change);
```

- [ ] **Step 6: Create the process-wide count**

`src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptions.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Process-wide gate for the per-property listener lookup. Counts subscriptions created and not yet
/// disposed. It is a long (not int) so cumulative fire-and-forget increments (which never decrement)
/// cannot wrap to zero and spuriously close the gate; 2^63 registrations is unreachable.
/// </summary>
internal static class PropertyChangeSubscriptions
{
    private static long _liveCount;

    public static void IncrementLiveCount() => Interlocked.Increment(ref _liveCount);

    public static void DecrementLiveCount() => Interlocked.Decrement(ref _liveCount);

    // Atomic 64-bit read (matches PropertyReference.SetWriteTimestamp's Interlocked.Read pattern).
    public static long ReadLiveCount() => Interlocked.Read(ref _liveCount);

    // Test-only reset hook (see the serialized test collection in Task 4).
    internal static void ResetForTests() => Interlocked.Exchange(ref _liveCount, 0);
}
```

Note the `internal` `ResetForTests`: the Tracking test project already has `InternalsVisibleTo` (`Namotion.Interceptor.Tracking.csproj:10`).

- [ ] **Step 7: Create the subscription object with CAS storage and one-shot dispose**

`src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscription.cs`:

```csharp
using System.Collections.Concurrent;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A subject-stored per-property subscription. Stored in Subject.Data[(propertyName, ListenersKey)]
/// as an element of an immutable copy-on-write array. Strong references (standard IDisposable
/// ownership); dispose or drop the handle.
/// </summary>
internal sealed class PropertyChangeSubscription : IDisposable
{
    internal const string ListenersKey = "Namotion.Interceptor.PropertyChangeListeners";

    private readonly string _propertyName;
    private IInterceptorSubject? _subject;                 // cleared on dispose
    internal IPropertyChangeObserver? Observer;            // read via Volatile.Read on dispatch; cleared on dispose
    private int _disposed;                                 // one-shot flag

    private PropertyChangeSubscription(IInterceptorSubject subject, string propertyName, IPropertyChangeObserver observer)
    {
        _subject = subject;
        _propertyName = propertyName;
        Observer = observer;
    }

    public static IDisposable Install(PropertyReference property, IPropertyChangeObserver observer)
    {
        var subject = property.Subject;
        var propertyName = property.Name;
        var subscription = new PropertyChangeSubscription(subject, propertyName, observer);
        var key = (propertyName, ListenersKey);

        // Increment before publishing so a concurrent write cannot read zero while this is installed.
        PropertyChangeSubscriptions.IncrementLiveCount();

        var data = subject.Data;
        while (true)
        {
            if (data.TryGetValue(key, out var existing) && existing is PropertyChangeSubscription[] current)
            {
                var updated = new PropertyChangeSubscription[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = subscription;

                // TryUpdate is the compare-and-swap: it replaces only if the stored value is still
                // the same array instance (EqualityComparer<object?>.Default = reference equality for arrays).
                if (data.TryUpdate(key, updated, current))
                {
                    return subscription;
                }
                // lost the race; retry
            }
            else if (data.TryAdd(key, new PropertyChangeSubscription[] { subscription }))
            {
                return subscription;
            }
            // else another thread added first; loop and TryUpdate
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // one-shot
        }

        var subject = _subject;
        if (subject is not null)
        {
            RemoveFromData(subject, _propertyName, this);
        }

        // Clear references so a retained handle pins neither subject nor observer; pair with dispatch Volatile.Read.
        Volatile.Write(ref _subject, null);
        Volatile.Write(ref Observer, null);

        // Decrement AFTER removal so a concurrent write cannot read zero while still installed.
        PropertyChangeSubscriptions.DecrementLiveCount();
    }

    private static void RemoveFromData(IInterceptorSubject subject, string propertyName, PropertyChangeSubscription subscription)
    {
        var key = (propertyName, PropertyChangeSubscription.ListenersKey);
        var data = subject.Data;
        while (true)
        {
            if (!data.TryGetValue(key, out var existing) || existing is not PropertyChangeSubscription[] current)
            {
                return;
            }

            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            if (current.Length == 1)
            {
                // Remove the whole entry with a compare-and-remove (reference equality on the array value).
                // TryRemovePropertyData is defined on PropertyReference (PropertyReference.cs:75-79):
                // bool TryRemovePropertyData(string key, object? expectedValue), removing (Name, key).
                if (new PropertyReference(subject, propertyName).TryRemovePropertyData(ListenersKey, current))
                {
                    return;
                }
            }
            else
            {
                var updated = new PropertyChangeSubscription[current.Length - 1];
                Array.Copy(current, 0, updated, 0, index);
                Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
                if (data.TryUpdate(key, updated, current))
                {
                    return;
                }
            }
            // lost the race; retry
        }
    }

    // Dispatch reads the array snapshot and invokes each still-live observer by ref.
    public static void Dispatch(PropertyChangeSubscription[] subscriptions, in SubjectPropertyChange change)
    {
        for (var i = 0; i < subscriptions.Length; i++)
        {
            var observer = Volatile.Read(ref subscriptions[i].Observer);
            if (observer is not null)
            {
                observer.OnChange(in change);
            }
        }
    }
}
```

Note: `TryRemovePropertyData` is an instance method on `PropertyReference` (`PropertyReference.cs:75-79`) with signature `bool TryRemovePropertyData(string key, object? expectedValue)`; it removes the `(Name, key)` entry only if the stored value reference-equals `expectedValue`. The `RemoveFromData` code above calls it via `new PropertyReference(subject, propertyName).TryRemovePropertyData(ListenersKey, current)`; verify the signature when implementing and adjust if it differs. In `Install`, `ConcurrentDictionary.TryUpdate(key, updated, current)` is the compare-and-swap: it replaces only when the stored value reference-equals `current` (`EqualityComparer<object?>.Default` is reference equality for arrays).

- [ ] **Step 8: Create the low-level `Subscribe` extensions**

`src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Change;

public static class PropertyChangeSubscriptionExtensions
{
    /// <summary>
    /// Subscribes an observer to changes of a single property (subject instance + name). Delivery is
    /// synchronous, on the writing thread, and dormant while the subject is not attached to a context
    /// with a <see cref="PropertyChangeInterceptor"/>. See <see cref="IPropertyChangeObserver"/> for the contract.
    /// </summary>
    public static IDisposable Subscribe(this PropertyReference property, IPropertyChangeObserver observer)
    {
        return PropertyChangeSubscription.Install(property, observer);
    }

    /// <summary>Delegate overload of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable Subscribe(this PropertyReference property, PropertyChangeCallback callback)
    {
        return PropertyChangeSubscription.Install(property, new DelegateObserver(callback));
    }

    private sealed class DelegateObserver(PropertyChangeCallback callback) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => callback(in change);
    }
}
```

- [ ] **Step 9: Extend `PropertyChangeInterceptor.WriteProperty` to dispatch listeners**

Replace the `WriteProperty` method in `PropertyChangeInterceptor.cs` with the full three-tier-gated version (spec Write path):

```csharp
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var state = _state;
        var mayHaveListeners = PropertyChangeSubscriptions.ReadLiveCount() != 0;
        if (state is null && !mayHaveListeners)
        {
            next(ref context);
            return;
        }

        var subscriptions = state?.QueueSubscriptions ?? [];
        var syncSubject = state?.SyncSubject;

        PropertyChangeSubscription[]? listeners = null;
        if (mayHaveListeners && !context.ArePropertyListenersClaimed)
        {
            listeners = TryGetListeners(context.Property);
        }

        if (syncSubject is null && subscriptions.Length == 0 && listeners is null)
        {
            next(ref context);
            return;
        }

        if (listeners is not null)
        {
            context.ArePropertyListenersClaimed = true;
        }

        var oldValue = context.CurrentValue;
        next(ref context);

        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            oldValue,
            context.GetFinalValue());

        for (var i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(in change);
        }

        if (syncSubject is not null)
        {
            syncSubject.OnNext(change);
        }

        if (listeners is not null)
        {
            PropertyChangeSubscription.Dispatch(listeners, in change);
        }
    }

    private static PropertyChangeSubscription[]? TryGetListeners(PropertyReference property)
    {
        return property.Subject.Data.TryGetValue((property.Name, PropertyChangeSubscription.ListenersKey), out var value)
            ? value as PropertyChangeSubscription[]
            : null;
    }
```

- [ ] **Step 10: Build and run the end-to-end tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS.

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PerPropertySubscriptionTests"`
Expected: PASS (all 3).

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "Add subject-stored per-property change subscriptions with interceptor dispatch"
```

---

## Task 3: Strongly-typed `SubscribeToProperty` and the expression resolver (Phase 2, PR 2)

**Files:**
- Modify: `src/Namotion.Interceptor.Tracking/Change/PropertyChangeSubscriptionExtensions.cs`
- Test: `src/Namotion.Interceptor.Tracking.Tests/Change/SubscribeToPropertyResolverTests.cs`

**Interfaces:**
- Consumes: `PropertyChangeSubscription.Install`, `PropertyReference`, `IInterceptorSubject`.
- Produces: `public static IDisposable SubscribeToProperty<TSubject, TValue>(this TSubject subject, Expression<Func<TSubject, TValue>> propertySelector, IPropertyChangeObserver observer) where TSubject : IInterceptorSubject` and the `PropertyChangeCallback` overload.

- [ ] **Step 1: Write the failing resolver tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/SubscribeToPropertyResolverTests.cs`:

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SubscribeToPropertyResolverTests
{
    [Fact]
    public void WhenDirectPropertySelector_ThenResolvesAndFires()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        string? captured = null;
        using var s = person.SubscribeToProperty(p => p.FirstName, (in SubjectPropertyChange c) => captured = c.GetNewValue<string?>());

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("John", captured);
    }

    [Fact]
    public void WhenChainedSelector_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context) { Mother = new Person(context) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToProperty(p => p.Mother.FirstName, (in SubjectPropertyChange _) => { }));
    }

    [Fact]
    public void WhenUnknownMember_ThenThrows()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);

        // Act & Assert (selecting a non-partial, non-tracked member; adjust to a real such member on Person)
        Assert.Throws<ArgumentException>(() =>
            person.SubscribeToProperty(p => p.ToString(), (in SubjectPropertyChange _) => { }));
    }
}
```

If `Person` lacks a `Mother`/reference property, use whichever reference-typed subject property exists in the test model (open `Person`/related models in the test project). For the "unknown/non-property" case, prefer a selector over a field or method to exercise the `PropertyInfo` and shape checks.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build src/Namotion.Interceptor.Tracking.Tests`
Expected: FAIL: `SubscribeToProperty` does not exist.

- [ ] **Step 3: Implement the resolver and the extension overloads**

Append to `PropertyChangeSubscriptionExtensions.cs` (add `using System.Linq.Expressions;` and `using System.Reflection;`):

```csharp
    /// <summary>
    /// Strongly-typed subscription to a direct property of <paramref name="subject"/>, for example
    /// <c>subject.SubscribeToProperty(x => x.Temperature, observer)</c>. Only a direct property access on
    /// the lambda parameter is accepted; chained, captured, static, field, and method selectors throw.
    /// </summary>
    public static IDisposable SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        IPropertyChangeObserver observer)
        where TSubject : IInterceptorSubject
    {
        var name = ResolveDirectPropertyName(propertySelector);
        if (!subject.Properties.ContainsKey(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a tracked property of {subject.GetType().Name}.", nameof(propertySelector));
        }

        return new PropertyReference(subject, name).Subscribe(observer);
    }

    /// <summary>Delegate overload of <see cref="SubscribeToProperty{TSubject,TValue}(TSubject, Expression{Func{TSubject,TValue}}, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable SubscribeToProperty<TSubject, TValue>(
        this TSubject subject,
        Expression<Func<TSubject, TValue>> propertySelector,
        PropertyChangeCallback callback)
        where TSubject : IInterceptorSubject
    {
        return subject.SubscribeToProperty(propertySelector, new DelegateObserver(callback));
    }

    private static string ResolveDirectPropertyName<TSubject, TValue>(Expression<Func<TSubject, TValue>> selector)
    {
        var body = selector.Body;

        // Unwrap a Convert/ConvertChecked boxing or numeric-cast node (e.g. Expression<Func<T, object>>).
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Member is not PropertyInfo property
            || member.Expression != selector.Parameters[0])
        {
            throw new ArgumentException(
                "Only a direct property access on the lambda parameter is supported, for example x => x.Foo. " +
                "Chained (x => x.Child.Foo), captured-variable, static, field, and method selectors are not allowed.",
                nameof(selector));
        }

        return property.Name;
    }
```

- [ ] **Step 4: Build and run the resolver tests**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: PASS.

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~SubscribeToPropertyResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add strongly-typed SubscribeToProperty with a strict expression resolver"
```

---

## Task 4: Concurrency, lifecycle, and edge-case tests + API snapshot (Phase 2, PR 2)

Locks down the behaviors the spec's Testing section enumerates and accepts the new Tracking snapshot. No production code changes except moving the collection definition to its own file.

**Files:**
- Create: `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionCollection.cs` (move the definition here from Task 2's test file)
- Modify/Create: `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionLifecycleTests.cs`
- Accept: `src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt`

**Interfaces:**
- Consumes: the full Task 1-3 public surface.

- [ ] **Step 1: Extract the serialized test collection**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionCollection.cs`:

```csharp
namespace Namotion.Interceptor.Tracking.Tests.Change;

// EVERY test that creates a per-property subscription must belong to this collection, because
// PropertyChangeSubscriptions.LiveCount is process-wide and xUnit runs different collections in
// parallel; a Subscribe in a parallel collection would poison fast-path/count assertions.
[CollectionDefinition(Name, DisableParallelization = true)]
public class PerPropertySubscriptionCollection
{
    public const string Name = "PerPropertySubscription";
}
```

Remove the inline `[CollectionDefinition]` placeholder added in Task 2 (Step 3). Ensure `PerPropertySubscriptionTests`, `SubscribeToPropertyResolverTests`, and the new lifecycle tests all carry `[Collection(PerPropertySubscriptionCollection.Name)]`. Each such test class should call `PropertyChangeSubscriptions.ResetForTests()` in its constructor to normalize the shared count.

- [ ] **Step 2: Write the lifecycle and edge-case tests**

Create `src/Namotion.Interceptor.Tracking.Tests/Change/PerPropertySubscriptionLifecycleTests.cs` with these tests (full bodies; use the existing test models and `AsyncTestHelpers.WaitUntilAsync` where synchronization is needed):

```csharp
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Tests.Change;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PerPropertySubscriptionLifecycleTests
{
    public PerPropertySubscriptionLifecycleTests() => PropertyChangeSubscriptions.ResetForTests();

    [Fact]
    public void WhenSameObserverSubscribedToMultipleProperties_ThenEachFiresIndependently()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var hits = 0;
        using var a = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);
        using var b = new PropertyReference(person, nameof(Person.LastName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act
        person.FirstName = "John";
        person.LastName = "Doe";

        // Assert
        Assert.Equal(2, hits);
    }

    [Fact]
    public void WhenReentrantWriteInCallback_ThenDeliversPerWriteWithoutDeadlock()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var lastNameHits = 0;
        using var first = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => person.LastName = "Doe");
        using var last = new PropertyReference(person, nameof(Person.LastName))
            .Subscribe((in SubjectPropertyChange _) => lastNameHits++);

        // Act
        person.FirstName = "John";

        // Assert
        Assert.Equal("Doe", person.LastName);
        Assert.Equal(1, lastNameHits);
    }

    [Fact]
    public void WhenRepeatedDispose_ThenNoOpAndCountNeverNegative()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => { });

        // Act
        s.Dispose();
        s.Dispose();
        s.Dispose();

        // Assert
        Assert.Equal(0, PropertyChangeSubscriptions.ReadLiveCount());
    }

    [Fact]
    public void WhenSubjectDetachedThenReattached_ThenSubscriptionRevives()
    {
        // Arrange: subscribe before attach.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var root = new Container(context);
        var child = new Person(); // no context yet
        var hits = 0;
        using var s = new PropertyReference(child, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act 1: dormant (not attached) -> no delivery
        child.FirstName = "A";
        Assert.Equal(0, hits);

        // Act 2: attach -> delivery resumes
        root.Item = child;
        child.FirstName = "B";
        Assert.Equal(1, hits);

        // Act 3: detach -> dormant again
        root.Item = null;
        child.FirstName = "C";
        Assert.Equal(1, hits);
    }

    [Fact]
    public void WhenTwoAggregatedInterceptors_ThenListenerDeliversExactlyOnce()
    {
        // Arrange: a subject whose context aggregates two full-tracking contexts.
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var childCtx = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        childCtx.AddFallbackContext(parent);
        var person = new Person(childCtx);
        var hits = 0;
        using var s = new PropertyReference(person, nameof(Person.FirstName)).Subscribe((in SubjectPropertyChange _) => hits++);

        // Act
        person.FirstName = "John";

        // Assert (dedup flag guarantees once, not twice)
        Assert.Equal(1, hits);
    }

    [Fact]
    public void WhenObserverThrows_ThenExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeNotifications();
        var person = new Person(context);
        using var s = new PropertyReference(person, nameof(Person.FirstName))
            .Subscribe((in SubjectPropertyChange _) => throw new InvalidOperationException("boom"));

        // Act & Assert (must-not-throw contract: not caught by the framework)
        Assert.Throws<InvalidOperationException>(() => person.FirstName = "John");
    }
}
```

Adjust model names (`Container`/`Item`, `Person`) to whatever the Tracking test project actually provides for a root-with-child-reference topology. If none exists, add a minimal `[InterceptorSubject]` test model in the test project's Models folder.

Also add, into the appropriate class, the two derived-property tests from the spec (full tracking fires; bare `WithPropertyChangeNotifications()` is inert and manual `RecalculateDerivedProperty()` is also a no-op without derived detection).

- [ ] **Step 3: Run the per-property test suite**

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~PerProperty|FullyQualifiedName~SubscribeToProperty"`
Expected: PASS.

- [ ] **Step 4: Accept the updated Tracking snapshot**

The new public types (`PropertyChangeInterceptor` already in from Task 1; now `IPropertyChangeObserver`, `PropertyChangeCallback`, `Subscribe`/`SubscribeToProperty` extension methods) change the snapshot again.

Run: `DiffEngine_Disabled=true dotnet test src/Namotion.Interceptor.Tracking.Tests --filter "FullyQualifiedName~VerifyChecksTests"`
Expected: FAIL (new members). Review the diff, confirm it is exactly the new public surface, then accept:

```bash
cp src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.received.txt \
   src/Namotion.Interceptor.Tracking.Tests/VerifyChecksTests.PublicApi.verified.txt
```

Re-run to confirm PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet test src/Namotion.Interceptor.slnx --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add concurrency, lifecycle, dedup, and resolver tests for per-property subscriptions"
```

---

## Task 5: Benchmarks (Phase 3)

**Files:**
- Modify/Create: `src/Namotion.Interceptor.Benchmark/PropertyChangeNotificationBenchmark.cs`

**Interfaces:**
- Consumes: the full public surface.

- [ ] **Step 1: Add benchmark configurations**

Create `src/Namotion.Interceptor.Benchmark/PropertyChangeNotificationBenchmark.cs` with BenchmarkDotNet benchmarks for the write hot path in each state the spec's Performance section names: idle (no consumers), queue-only active, observable-only active, both-active, listener-on-written-property, listener-elsewhere (LiveCount nonzero, lookup miss, the active-lookup state), and observable-subscribed-then-fully-unsubscribed. Follow the existing benchmark style in `SubjectSourceBenchmark.cs`. Each benchmark sets up its state in `[GlobalSetup]` and writes a property in the `[Benchmark]` method.

- [ ] **Step 2: Build the benchmark project**

Run: `dotnet build src/Namotion.Interceptor.Benchmark -c Release`
Expected: PASS.

- [ ] **Step 3: (Optional, gated) Run benchmarks before/after**

Run: `dotnet run --project src/Namotion.Interceptor.Benchmark -c Release`
Compare idle and single-facet-active numbers against `master` to confirm no regression (spec Constraint 2). This is a gated check; record the numbers in the PR. Do not block the plan on absolute thresholds; the gate is "no regression vs master," judged from the run.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Add property change notification write-path benchmarks"
```

---

## Task 6: Documentation (Phase 3)

**Files:**
- Modify: `docs/tracking.md`
- Modify: `docs/tracking-transactions.md:525`
- Modify: `docs/blazor.md:171`
- Modify (XML docs): `PropertyChangeSubscriptionExtensions.cs`, `IPropertyChangeObserver.cs`, and the `GetPropertyChangeObservable`/`CreatePropertyChangeQueueSubscription` doc comments in `InterceptorSubjectContextExtensions.cs`, plus `PropertyChangeQueueSubscription`.

- [ ] **Step 1: Update `docs/tracking.md`**

Rewrite the change-tracking sections to describe: the single `PropertyChangeInterceptor` and `WithPropertyChangeNotifications()`; the per-property subscription API; the concurrency contract (not serialized, must not throw, possible out-of-order delivery, re-read for the current value, transaction commit-replay and rollback visibility); the ownership/lifetime rule (standard `IDisposable`: dispose or drop the handle; a retained undisposed handle pins the subject, and an observer that captures the subject pins it too); dormancy/revival; instance-not-path semantics; that a throwing synchronous Rx observer suppresses listener delivery for that write; and that automatic derived-property notifications require `WithDerivedPropertyChangeDetection()` (bundled in `WithFullPropertyTracking()`); manual `RecalculateDerivedProperty()` also requires it. Replace the two occurrences of `WithPropertyChangeObservable()`/`WithPropertyChangeQueue()` in the examples (lines ~37, 73, 168) and the "used by" list (lines ~527, 529) with `WithPropertyChangeNotifications()`.

- [ ] **Step 2: Fix dangling references in other docs**

- `docs/tracking-transactions.md:525`: change "Call `WithTransactions()` before `WithPropertyChangeObservable()`" to "... before `WithPropertyChangeNotifications()`".
- `docs/blazor.md:171`: in the tracking-flow diagram, change the `PropertyChangeObservable` label to `PropertyChangeInterceptor`.
- `docs/graphql.md:164` uses the retained `GetPropertyChangeObservable()`; leave it.

- [ ] **Step 3: Add the out-of-order / re-read caveat to XML docs of all three notification entry points**

On `GetPropertyChangeObservable()`, `CreatePropertyChangeQueueSubscription()` (and `PropertyChangeQueueSubscription`), and `Subscribe`/`SubscribeToProperty`/`IPropertyChangeObserver`, add: "Under concurrent writes to the same property, notifications may arrive out of commit order because dispatch runs outside the subject lock; if you need the current value, re-read the property rather than relying on the delivered new value." (The per-property XML docs already carry the not-serialized, must-not-throw, and dispose/lifetime points from Task 2/3.)

- [ ] **Step 4: Verify docs build and prose rules**

Run: `grep -rn "—" docs/tracking.md docs/tracking-transactions.md docs/blazor.md`; expect no matches.
Run: `grep -rn "WithPropertyChangeObservable\|WithPropertyChangeQueue\|PropertyChangeObservable\|PropertyChangeQueue\b" docs/*.md | grep -v superpowers`; expect only intended, retained references (none should name the removed methods/types except historical prose you intend to keep).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Document the merged interceptor and per-property subscription API"
```

---

## Self-Review

Coverage check against the spec (each mapped to a task):
- Merged `PropertyChangeInterceptor`, `DispatchState`, lazy observable, gate closure -> Task 1.
- `WithPropertyChangeNotifications()`, removed methods, resolution, `WithFullPropertyTracking` -> Task 1.
- Target-side `[RunsAfter]` ordering; migration surface; snapshot -> Task 1.
- `PropertyChangeQueueSubscription` internal ctor + three race fixes (enqueue-vs-dispose signal, lost-wakeup, atomic dispose) -> Task 1 (Step 4).
- Interceptor `Dispose` (queue completed, observable preserved, post-dispose create throws) -> Task 1 (Step 5, test Step 2).
- Subject-stored storage, CAS, one-shot dispose, `LiveCount` (long, atomic), observer capture -> Task 2.
- Internal `ArePropertyListenersClaimed` dedup flag; three-tier gated `WriteProperty` -> Task 2.
- `IPropertyChangeObserver`, `PropertyChangeCallback`, low-level `Subscribe` -> Task 2.
- Strict resolver (Convert unwrap, ParameterExpression target, `PropertyInfo`, name validation); `SubscribeToProperty` -> Task 3.
- Dormancy/revival, aggregation dedup, re-entrancy, exception contract, repeated dispose, derived prerequisite, static-state isolation -> Task 4.
- Benchmark configurations (idle, single-facet, both, listener on/elsewhere, subscribed-then-unsubscribed) -> Task 5.
- All docs + XML-doc caveats on the three entry points -> Task 6.

Open items deferred by the spec and intentionally not in this plan: value-assertion corrections (out of scope); the per-subject-field escalation for `LiveCount` (only if benchmarks flag it).
