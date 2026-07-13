# Source Write Lock Ownership

**Date:** 2026-07-13
**Status:** Approved

## Problem

`SubjectSourceExtensions.WriteChangesInBatchesAsync` serializes outbound writes to sources that do not support concurrent writes. The lock lives in a static `ConditionalWeakTable<ISubjectSource, SourceWriteLock>` keyed by source instance, with a TODO noting that the `SemaphoreSlim` is never explicitly disposed.

The actual costs of the current design:

- Static mutable state with non-obvious ownership (the lock's lifetime is implicit via the weak table).
- A table lookup on every write batch, which is on the hot write path.
- The TODO itself is based on a wrong premise: `SemaphoreSlim` has no finalizer and holds no unmanaged resources unless `AvailableWaitHandle` is accessed, which this code never does. There is no leak today; collected sources release the semaphore as plain managed memory.

Separately, `ISupportsConcurrentWrites` is a bare marker interface consumed in exactly one place (the same extension method) to skip the lock.

## Decision

Move the write lock onto the source instance and remove the marker interface.

### `ISubjectSource` gains one member

```csharp
/// <summary>
/// Gets the semaphore used by <see cref="SubjectSourceExtensions.WriteChangesInBatchesAsync"/>
/// to serialize writes to this source. Return <c>null</c> when the source handles concurrent
/// writes itself and needs no external synchronization. The semaphore must be created with
/// initial and maximum count 1 and is owned by the source for its entire lifetime.
/// </summary>
SemaphoreSlim? WriteLock { get; }
```

`null` replaces the `ISupportsConcurrentWrites` marker. Keeping both would allow contradictory states (marker present but lock non-null, or the reverse), so the marker interface is deleted.

`SemaphoreSlim` is exposed directly rather than behind a wrapper type. `ISubjectSource` is conceptually an internal integration interface, and the XML docs state that the lock is reserved for `WriteChangesInBatchesAsync`.

### `SubjectSourceBase` implements it

```csharp
public virtual SemaphoreSlim? WriteLock { get; } = new(1, 1);
```

Virtual so a derived connector that becomes concurrent-safe can override it to return `null`, which is the capability the marker interface provided.

### The extension reads the property

`WriteChangesInBatchesAsync` replaces the `source is ISupportsConcurrentWrites` check and the weak table lookup with:

```csharp
var writeLock = source.WriteLock;
if (writeLock is null)
{
    return await WriteChangesInBatchesCoreAsync(source, changes, cancellationToken).ConfigureAwait(false);
}
```

The wait, cancellation-to-failure conversion, and `finally { Release() }` logic stay as they are. The `ConditionalWeakTable`, the internal `SourceWriteLock` class, and the TODO comment are deleted.

### No disposal, by design

`SubjectSourceBase.Dispose()` does not dispose the semaphore, and the interface docs do not ask implementers to. Two reasons:

1. There is nothing to release. The semaphore never creates its `AvailableWaitHandle`, so `Dispose` would be a no-op on resources.
2. Disposing would introduce a real race that does not exist today: `SourceTransactionWriter` can be awaiting `WaitAsync` on another thread when the host disposes the source, and both `WaitAsync` and the `finally` block's `Release()` throw `ObjectDisposedException` on a disposed `SemaphoreSlim`.

The deterministic cleanup requested by the TODO is achieved through ownership: the lock's lifetime is exactly the source instance's lifetime, with no static registry involved.

## Alternatives rejected

- **Lock on `SubjectSourceBase` only, extension type-checks for the base class.** Direct `ISubjectSource` implementers would need a fallback, and the only fallbacks are keeping the weak table or silently not serializing. Both defeat the purpose.
- **Context-scoped or registration-based lock registry.** Replaces one lookup table with another plus attach/detach choreography, and a plain dictionary leaks worse than the weak table unless detach is guaranteed.
- **Dispose the semaphore in `SubjectSourceBase.Dispose()`.** Buys zero resources and creates the `ObjectDisposedException` race described above.

## Affected code

Production (`Namotion.Interceptor.Connectors`):

- `ISubjectSource.cs`: add `WriteLock` property; update `WriteChangesAsync` remarks that currently reference `ISupportsConcurrentWrites`.
- `ISupportsConcurrentWrites.cs`: delete.
- `SubjectSourceExtensions.cs`: remove weak table, `SourceWriteLock` class, TODO, marker check; read `source.WriteLock`; update XML remarks.
- `SubjectSourceBase.cs`: add virtual `WriteLock` property.

Tests and benchmarks:

- `Namotion.Interceptor.Connectors.Tests/SubjectSourceExtensionsTests.cs`: fake sources implement `WriteLock`; `ConcurrentTestSource` drops the marker interface and returns `null`.
- `Namotion.Interceptor.Benchmark/SubjectTransactionBenchmark.cs`: direct `ISubjectSource` implementer gains the member.
- `Namotion.Interceptor.Connectors.Tests/VerifyChecksTests.PublicApi.verified.txt`: accept new snapshot (added property, removed interface).
- `SubjectSourceBase` subclasses (`TestSubjectSource` in tests and benchmarks) need no changes.

Production connectors (OPC UA, MQTT, WebSocket) all derive from `SubjectSourceBase` and need no changes.

## Behavior

Unchanged. Sources without a `null` lock get serialized writes exactly as before; sources returning `null` get concurrent writes exactly as marker implementers did. Existing serialization and concurrency tests in `SubjectSourceExtensionsTests` continue to cover both paths. The only observable difference is one fewer table lookup per write batch.

## Testing

- Existing tests: write serialization (max 1 concurrent), concurrent opt-out (multiple concurrent), cancellation during semaphore wait. These cover the new code paths without modification beyond the fake-source member additions.
- New test: a `SubjectSourceBase`-derived source overriding `WriteLock` to `null` gets concurrent writes (replaces the marker-interface variant of that guarantee).
- Public API snapshot test validates the interface change intentionally.
