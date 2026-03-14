# Derived Property Change Detection: Internal Design

This document describes the internal architecture of the derived property tracking system in `Namotion.Interceptor.Tracking`. For user-facing documentation, see [tracking.md](../tracking.md).

## Overview

Derived properties are computed properties marked with `[Derived]`. They are not intercepted (no partial backing field), but their dependencies on intercepted properties are automatically tracked. When any dependency changes, the derived property is recalculated and a change notification is fired.

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }   // Intercepted (partial)
    public partial string LastName { get; set; }    // Intercepted (partial)

    [Derived]
    public string FullName => $"{FirstName} {LastName}";  // Not partial, just a regular property
}
```

The source generator detects `[Derived]` and sets `IsDerived = true` on `SubjectPropertyMetadata`, but does not generate a backing field. The getter body is the user's original C# expression.

Derived properties can also be added at runtime via `RegisteredSubject.AddDerivedProperty<T>(name, getValue, setValue)`. These work identically with the dependency tracking system — the getter/setter lambdas are wrapped in interception calls and dependencies are recorded on first evaluation.

## Interceptor Ordering

`DerivedPropertyChangeHandler` is annotated with `[RunsBefore(typeof(LifecycleInterceptor))]`. This ensures `AttachProperty` runs before lifecycle handlers, so derived property dependencies are recorded and the initial value is cached before other handlers see the property.

## Components

The system consists of five internal types, all in `Namotion.Interceptor.Tracking.Change`:

| Type | Role |
|------|------|
| `DerivedPropertyChangeHandler` | Main interceptor. Implements `IReadInterceptor`, `IWriteInterceptor`, `IPropertyLifecycleHandler`. Coordinates recording, recalculation, and cleanup. |
| `DerivedPropertyData` | Per-property state. Stores forward/backward dependencies, cached value, and lifecycle flags. Stored in `Subject.Data` under key `"ni.dpd"`. |
| `DerivedPropertyDependencies` | Lock-free copy-on-write collection for backward dependencies (`UsedByProperties`). Uses CAS for thread-safe mutation. |
| `DerivedPropertyRecorder` | Thread-static recording buffer. Captures which properties are read during getter evaluation. Uses `ArrayPool` to avoid allocations in steady state. |
| `DerivedPropertyChangeHandlerExtensions` | Extension methods for accessing `DerivedPropertyData` via `PropertyReference`. |

## Dependency Graph

The system maintains a bidirectional dependency graph:

```
Forward (RequiredProperties):        Backward (UsedByProperties):
  FullName → [FirstName, LastName]     FirstName → [FullName]
                                       LastName  → [FullName]
```

- **Forward links** (`data.RequiredProperties`): Which properties a derived property reads. Stored as `PropertyReference[]`, replaced atomically under `lock(data)`.
- **Backward links** (`data.UsedByProperties`): Which derived properties depend on this property. Stored as `DerivedPropertyDependencies`, updated via lock-free CAS.

Both directions are needed:
- Forward links enable cleanup when a derived property is detached (remove itself from all dependencies' backward links).
- Backward links enable recalculation when a source property is written (find all affected derived properties).

Dependencies can span subjects. For example, `Car.AveragePressure` depends on each `Tire.Pressure`. The dependency graph links properties across subject boundaries:

```
Forward:                                  Backward:
  Car.AveragePressure → [Tire0.Pressure,    Tire0.Pressure → [Car.AveragePressure]
                         Tire1.Pressure,    Tire1.Pressure → [Car.AveragePressure]
                         ...]               ...
```

When a tire is replaced, the old tire's `Pressure` is removed from `AveragePressure.RequiredProperties` (via recalculation recording the new dependency set) and the new tire's `Pressure` is added.

## Data Flow

### 1. Attach (initialization)

When a subject is created with a context, `LifecycleInterceptor` fires `AttachProperty` for each property. For derived properties, `DerivedPropertyChangeHandler.AttachProperty` runs:

```
AttachProperty(FullName)
  lock(data)
    data.IsAttached = true
    try:
      generationBefore = Volatile.Read(_writeGeneration)
      StartRecordingTouchedProperties()
      result = getter.Invoke(subject)              // evaluates "FirstName + LastName"
        → ReadProperty(FirstName)                 // recorder captures FirstName
        → ReadProperty(LastName)                  // recorder captures LastName
      StoreRecordedTouchedProperties(FullName, data)
        // sets data.RequiredProperties = [FirstName, LastName]
        // adds FullName to FirstName.UsedByProperties
        // adds FullName to LastName.UsedByProperties
      if Volatile.Read(_writeGeneration) != generationBefore:
        // Concurrent write detected — fall back to stabilization loop
        do:
          StartRecordingTouchedProperties()
          result = getter.Invoke(subject)
          dependenciesChanged = StoreRecordedTouchedProperties(FullName, data)
        while (dependenciesChanged)
    finally:
      DiscardActiveRecording()                     // always clears recorder buffer
    data.LastKnownValue = result
    SetWriteTimestampUtcTicks(...)
```

On the common path (single-threaded construction), `_writeGeneration` is unchanged and the loop is skipped entirely — zero extra getter evaluations. If a concurrent write is detected, the full stabilization loop runs for correctness.

### 2. Read (recording)

When a property is read through the interceptor chain, `ReadProperty` checks if a recording is active:

```
ReadProperty(FirstName)
  result = next(ref context)                     // get actual value
  if (_recorder?.IsRecording)
    _recorder.TouchProperty(FirstName)           // add to current recording frame
  return result
```

The recorder is thread-static (`[ThreadStatic]`), so recordings on different threads don't interfere.

### 3. Write (recalculation trigger)

When a source property is written, `WriteProperty` triggers recalculation of all dependents:

```
WriteProperty(FirstName = "Jane")
  next(ref context)                              // write the value
  Volatile.Write(_writeGeneration, _writeGeneration + 1)  // signal for AttachProperty/RecalculateDerivedProperty
  data = FirstName.TryGetDerivedPropertyData()
  if data is null → return                       // fast path: no tracking data
  // Self-recalculation: if this is a derived-with-setter property, recalculate it
  if data.RequiredProperties != null && Metadata.SetValue != null:
    RecalculateDerivedProperty(FirstName, timestamp)
  // Cascade: recalculate all dependent derived properties
  usedByProperties = Volatile.Read(data.UsedByProperties)  // acquire fence for ARM64 visibility
  if usedByProperties has entries:
    for each dependent (e.g., FullName):
      RecalculateDerivedProperty(FullName, timestamp)
```

The `_writeGeneration` increment uses `Volatile.Write` (release semantics) so that `AttachProperty`/`RecalculateDerivedProperty` can detect concurrent writes via `Volatile.Read` (acquire semantics). The non-atomic increment (`_writeGeneration + 1`) is intentional — lost increments from concurrent writers are harmless because the counter still differs from the "before" snapshot.

**Derived properties with setters** (created via `AddDerivedProperty<T>(name, getValue, setValue)`) have both a getter and a setter. The setter modifies internal state as a side effect, but the actual property value is always determined by the getter. When the setter is called, `WriteProperty` detects `RequiredProperties != null && SetValue != null` and triggers recalculation to re-evaluate the getter and fire a change notification with the correct computed value.

### 4. Recalculation

```
RecalculateDerivedProperty(FullName, timestamp)
  lock(data)
    if data.IsRecalculating → return             // re-entrancy guard
    if !data.IsAttached → return                  // zombie prevention
    data.IsRecalculating = true
    try:
      oldValue = data.LastKnownValue
      generationBefore = Volatile.Read(_writeGeneration)
      StartRecordingTouchedProperties()
      newValue = getter.Invoke(subject)
      dependenciesChanged = StoreRecordedTouchedProperties(FullName, data)
      if dependenciesChanged && Volatile.Read(_writeGeneration) != generationBefore:
        // Concurrent write during evaluation — stabilization loop
        do:
          StartRecordingTouchedProperties()
          newValue = getter.Invoke(subject)
          dependenciesChanged = StoreRecordedTouchedProperties(FullName, data)
        while (dependenciesChanged)
      data.LastKnownValue = newValue
      SetWriteTimestampUtcTicks(timestamp)       // inherits trigger's timestamp
      WithSource(null):                          // marks as internal recalculation
        SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate)
      RaisePropertyChanged("FullName")           // INotifyPropertyChanged integration
    finally:
      DiscardActiveRecording()                   // always clears recorder buffer
      data.IsRecalculating = false
```

The generation check avoids re-evaluation when dependencies change but no concurrent write occurred (e.g., `ChangeAllTires` swaps tire references, changing deps from old tires to new tires). The stabilization loop only runs when a concurrent write is actually detected.

Key details of the change notification:
- **Timestamp inheritance**: The derived property receives the same timestamp as the write that triggered the recalculation, ensuring consistent timestamps within a mutation context.
- **`WithSource(null)`**: Wraps the notification in a scope that clears any external source context. This marks the change as an internal recalculation, preventing source transaction handlers from writing it back to an external source.
- **`NoOpWriteDelegate`**: Since derived properties have no backing field, the write delegate is a no-op (`static (_, _) => { }`). The call to `SetPropertyValueWithInterception` exists solely to fire the change notification through the interceptor chain (observable, queue, etc.) with the correct old and new values.
- **`IRaisePropertyChanged`**: If the subject implements `IRaisePropertyChanged`, `RaisePropertyChanged` is called to support standard `INotifyPropertyChanged` data binding.

### 5. Detach (cleanup)

When a subject is removed from the object graph, `DetachProperty` cleans up both directions:

Both cases are handled in a single `lock(data)` block, followed by Case 2 cleanup outside the lock:

```
DetachProperty(property)
  lock(data)
    data.IsAttached = false
    // Case 1 (derived only): Remove forward dependencies
    if property.IsDerived:
      for each dependency in data.RequiredProperties:   // read under lock
        dependency.UsedByProperties.Remove(property)    // CAS, no nested lock
      data.RequiredProperties = null                    // release forward references
      data.LastKnownValue = null                        // release cached value
    // Case 2 (all properties): Snapshot and clear backward dependencies
    usedBySnapshot = data.UsedByProperties.ItemsArray   // stable copy-on-write snapshot
    data.UsedByProperties = null                        // clear immediately
  // release lock before Case 2 processing to avoid nesting with lock(derivedData)
  for each derived in usedBySnapshot:
    lock(derivedData)
      remove property from derivedData.RequiredProperties
```

The single lock ensures `IsAttached`, forward cleanup (Case 1), and backward snapshot (Case 2) are atomic — no window where a concurrent thread could see `IsAttached=false` but `UsedByProperties` still populated.

The lock serializes with `StoreRecordedTouchedProperties`' backlink Add (which also acquires `lock(depData)` on the same object). This ensures either:
- The backlink was added before the snapshot → we see it and clean up the forward reference.
- `StoreRecordedTouchedProperties` acquires the lock after us → sees `IsAttached = false` → skips the Add.

## Dependency Updates (`StoreRecordedTouchedProperties`)

This method is called under `lock(data)` (the derived property's data) and maintains both forward and backward links:

1. **Fast path**: If `previousDeps.SequenceEqual(recordedDeps)` → clear recorder, return `false` (no allocation).
2. **Slow path**:
   - Copy recorded dependencies to an owned array (`newItems = recordedDeps.ToArray()`).
   - Clear the recorder immediately (`ClearLastRecording()`). After this point the `recordedDeps` span is invalidated; all subsequent code uses `newItems`.
   - Replace `data.RequiredProperties` with the new array.
   - For each old dependency no longer used: `Remove(derivedProperty)` from its `UsedByProperties` (CAS).
   - For each new dependency not previously tracked: `lock(depData)` → check `depData.IsAttached` → if attached, `Add(derivedProperty)` to its `UsedByProperties`. The lock serializes with `DetachProperty` Case 2 on the same dependency data.
   - If any backlink Add was skipped (dependency detaching), filter the detached dependencies from `RequiredProperties` and clean their backward links. This prevents forward reference leaks when no future recalculation would clean them up.
   - Return `true` if dependencies changed (caller should re-evaluate), `false` if the filtered result matches the previous set (prevents infinite stabilization loops when a getter keeps reading a detaching dependency).

The `bool` return drives the stabilization loop in `RecalculateDerivedProperty` and `AttachProperty`.

## Concurrency Model

### Per-property lock

`lock(data)` on `DerivedPropertyData` serializes:
- Concurrent recalculations of the same derived property
- Recalculation vs. detach (prevents zombie resurrection)
- `RequiredProperties` reads and writes

This is a fine-grained lock (per derived property), so different derived properties can recalculate concurrently.

### Lock-free backward links

`DerivedPropertyDependencies` uses copy-on-write with CAS:

```csharp
internal bool Add(in PropertyReference item)
{
    while (true)
    {
        var snapshot = Volatile.Read(ref _items);
        if (Array.IndexOf(snapshot, item) >= 0)
            return false;                        // already present

        var newArr = new PropertyReference[snapshot.Length + 1];
        Array.Copy(snapshot, newArr, snapshot.Length);
        newArr[^1] = item;

        if (ReferenceEquals(
            Interlocked.CompareExchange(ref _items, newArr, snapshot), snapshot))
            return true;
        // CAS failed — retry
    }
}
```

This is necessary because multiple derived properties on different threads may concurrently add themselves to the same source property's `UsedByProperties`.

### Thread-static recorder

`DerivedPropertyRecorder` is `[ThreadStatic]`, so each thread has its own instance. This avoids synchronization during recording. The recorder supports nesting (stack-based frames) for derived properties that read other derived properties.

### Lock ordering follows the dependency DAG

`RecalculateDerivedProperty` and `StoreRecordedTouchedProperties` nest locks in the derived → dependency direction: `lock(D_data)` (outer, from `RecalculateDerivedProperty`) → `lock(X_data)` (inner, from the backlink Add loop in `StoreRecordedTouchedProperties`). A deadlock would require a cycle in the lock acquisition order, which would imply a circular dependency in the property graph — but circular getter dependencies cause infinite recursion before any lock is reached, so the graph is always a DAG and deadlock is impossible.

`DetachProperty` uses a single `lock(data)` for all local cleanup (`IsAttached`, forward deps, backward snapshot), then acquires `lock(derivedData)` sequentially for `RequiredProperties` cleanup. Because it never holds two locks simultaneously, it cannot participate in a lock cycle. Case 1's forward cleanup (removing from dependencies' `UsedByProperties`) uses CAS inside the lock — no nested lock acquisition.

## Concurrency Scenarios

### Re-entrancy during derived-with-setter recalculation

Derived properties with setters (added via `AddDerivedProperty<T>(name, getValue, setValue)`) create a re-entrancy path: `RecalculateDerivedProperty` calls `SetPropertyValueWithInterception`, which re-enters `WriteProperty`, which would call `RecalculateDerivedProperty` again. Since `lock(data)` is re-entrant for the same thread, the lock alone doesn't prevent this.

The `data.IsRecalculating` flag guards against this. The re-entrant call sees `IsRecalculating = true` inside the lock and returns immediately. Cross-thread callers block on the lock and see `false` after the first thread releases — so they proceed normally.

### Concurrent write detection via `_writeGeneration`

When a derived property has conditional dependencies (e.g., `Display => UseFirstName ? FirstName : LastName`), the dependency set changes based on runtime state. A concurrent write to a newly-added dependency could land between getter evaluation and backlink registration — the write would not trigger recalculation because the backlink isn't registered yet.

Both `AttachProperty` and `RecalculateDerivedProperty` use a generation-based detection scheme instead of unconditionally re-evaluating:

1. `WriteProperty` increments `_writeGeneration` via `Volatile.Write` (release fence) on every write.
2. `AttachProperty`/`RecalculateDerivedProperty` read `_writeGeneration` via `Volatile.Read` (acquire fence) before and after evaluation.
3. If unchanged → no concurrent write occurred → skip the stabilization loop.
4. If changed → a concurrent write happened → fall back to the full stabilization loop.

The `Volatile.Write`/`Volatile.Read` pair forms a release-acquire synchronization: if the counter change is observed, all prior writes (including the committed property value) are guaranteed visible to the reading thread.

The non-atomic increment (`_writeGeneration + 1`) is intentional. Two concurrent writers may both read the same value and write the same incremented value ("lost increment"). This is harmless — the counter still differs from the "before" snapshot, so the concurrent write is detected.

In the common case (stable dependencies or single-threaded construction), the generation is unchanged and the loop is skipped — zero extra getter evaluations. The stabilization loop only runs when a concurrent write is actually detected.

#### Cross-context limitation

`_writeGeneration` is an instance field on `DerivedPropertyChangeHandler`, which is per-context. When dependencies span contexts (e.g., via context inheritance), writes through a different context's handler increment a different counter. This means a concurrent write from another context may not be detected by the generation check.

This is acceptable because the concurrent write still triggers `RecalculateDerivedProperty` via the normal backlink path (`UsedByProperties`), which blocks on `lock(data)` and corrects the value after the current evaluation completes. The result is a brief stale window (between the first evaluation completing and the blocked recalculation running) rather than a permanent inconsistency.

For single-context usage (the common case), all writes go through the same handler and the generation check is fully accurate — no stale window.

### Concurrent detach and recalculation

Two race conditions must be handled when `DetachProperty` runs concurrently with `RecalculateDerivedProperty` / `StoreRecordedTouchedProperties`:

**Race 1 — Zombie backlink resurrection (Case 1):** `WriteProperty` takes a snapshot of `UsedByProperties` and iterates it. A concurrent `DetachProperty` may remove a derived property's backlinks between the snapshot and the recalculation call. Without protection, `RecalculateDerivedProperty` would re-add the backlinks, creating zombie dependencies.

`DetachProperty` Case 1 and `RecalculateDerivedProperty` both acquire `lock(data)` on the same derived property's data, serializing them. `DetachProperty` sets `data.IsAttached = false` inside the lock. `RecalculateDerivedProperty` checks `IsAttached` inside the lock and bails out if false. `AttachProperty` sets `IsAttached = true` under lock to support re-attachment.

Both orderings produce a correct final state:
- **Detach wins lock**: clears `IsAttached`, removes backlinks. Recalculation then sees `!IsAttached` and skips.
- **Recalculation wins lock**: evaluates and re-adds backlinks. Detach then removes them. Final state is clean.

**Race 2 — Missed backlink in Case 2 snapshot:** When a source property X is being detached, `DetachProperty` Case 2 takes a snapshot of `X.UsedByProperties` to find dependent derived properties. Concurrently, `StoreRecordedTouchedProperties` may be adding a new backlink (D → X) to `X.UsedByProperties`. If the Add completes after the snapshot, `DetachProperty` misses D, leaving a stale forward reference (`D.RequiredProperties` contains X) and a stale backward reference (D in `X.UsedByProperties`).

This is solved by locking `X.DerivedPropertyData` in both places:
- `DetachProperty` Case 2: `lock(data)` → set `IsAttached = false` + take snapshot + clear `UsedByProperties` → release lock.
- `StoreRecordedTouchedProperties` backlink loop: `lock(depData)` → check `IsAttached` → if true, Add → release lock.

Since both operations lock the same object (`X.DerivedPropertyData`), they are fully serialized:
- **DetachProperty wins lock**: sets `IsAttached = false`, takes snapshot (D not present), clears `UsedByProperties`. `StoreRecordedTouchedProperties` then acquires the lock, sees `IsAttached = false`, skips the Add. The skipped dependency is also filtered from `RequiredProperties` by the post-loop cleanup.
- **StoreRecordedTouchedProperties wins lock**: sees `IsAttached = true`, adds D to `UsedByProperties`. `DetachProperty` then acquires the lock, takes snapshot (D is present), cleans up D's `RequiredProperties`.

No window exists where a backlink is added but not visible to `DetachProperty`.

## Correctness Guarantees

### Thread safety: no data corruption

Every piece of shared mutable state is protected by exactly one synchronization mechanism:

| State | Protection | Accessed by |
|-------|-----------|-------------|
| `data.RequiredProperties` | `lock(data)` | `StoreRecordedTouchedProperties`, `DetachProperty` Case 2 |
| `data.LastKnownValue` | `lock(data)` | `RecalculateDerivedProperty`, `AttachProperty`, `DetachProperty` Case 1 |
| `data.IsRecalculating` | `lock(data)` | `RecalculateDerivedProperty` |
| `data.IsAttached` | `lock(data)` | `DetachProperty` Case 1 + Case 2, `RecalculateDerivedProperty`, `AttachProperty`, `StoreRecordedTouchedProperties` (backlink loop) |
| `data.UsedByProperties` (collection contents) | CAS (copy-on-write) | `StoreRecordedTouchedProperties`, `DetachProperty` Case 1 |
| `data.UsedByProperties` (field itself) | `lock(data)` + `Interlocked.CompareExchange` | `DetachProperty` Case 2 (nulls under lock), `GetOrCreateUsedByProperties` (CAS create) |
| `_recorder` | `[ThreadStatic]` (no sharing) | `ReadProperty`, `StartRecording`, `StoreRecordedTouchedProperties` |
| `_writeGeneration` | `Volatile.Write` (release) / `Volatile.Read` (acquire) | `WriteProperty` (increment), `AttachProperty` + `RecalculateDerivedProperty` (check) |

Nested locks occur in `StoreRecordedTouchedProperties`: `lock(D_data)` (outer, from `RecalculateDerivedProperty`) → `lock(X_data)` (inner, backlink Add). The acquisition order follows the dependency DAG (derived → source). Circular dependencies would cause infinite recursion in getters before any lock is reached, so deadlock is impossible. `DetachProperty` uses a single `lock(data)` for all local cleanup, then acquires `lock(derivedData)` sequentially (never nested), so it cannot participate in a lock cycle.

### Value correctness: derived value always reflects current state

After all concurrent writes complete and recalculations settle, every derived property's value matches what its getter would return if called with the current source values. This is guaranteed by three mechanisms working together:

1. **Backlink-driven recalculation**: Once a dependency's backward link includes a derived property, any write to that dependency triggers `RecalculateDerivedProperty` via `WriteProperty`. The recalculation acquires `lock(data)`, so concurrent recalculations of the same derived property are serialized — each one sees the most recent source values.

2. **Generation-based concurrent write detection**: `WriteProperty` increments `_writeGeneration` on every write (`Volatile.Write`, release fence). `AttachProperty` and `RecalculateDerivedProperty` read the counter before and after evaluation (`Volatile.Read`, acquire fence). If unchanged, no concurrent write occurred and re-evaluation is skipped. If changed, the stabilization loop runs to catch writes that landed between getter evaluation and backlink registration.

3. **Stabilization loop (on concurrent write detection)**: When the generation check detects a concurrent write AND the dependency set changed, the `do/while` loop re-evaluates until dependencies stabilize:
   - **First iteration**: evaluates getter, registers backlinks.
   - **Subsequent iterations**: re-evaluate with backlinks in place, catching writes that happened before backlinks were registered. Each iteration that acquires backlinks ensures any *further* concurrent write to those dependencies triggers recalculation via the normal path and blocks on `lock(data)`.
   - The loop exits when `StoreRecordedTouchedProperties` returns `false` (deps unchanged).

   In the common case (no concurrent writes, or stable dependencies), the generation check avoids the loop entirely — zero extra getter evaluations.

**Cross-context note**: `_writeGeneration` is per-handler (per-context). Concurrent writes from a different context may not be detected by the generation check. These writes are still handled correctly via backlink-driven recalculation (mechanism 1), which blocks on `lock(data)` and corrects the value. The result is a brief stale window rather than a permanent inconsistency. See "Cross-context limitation" above for details.

### No zombie dependencies after detach

When a property is detached, no stale references remain in the dependency graph:

- **Forward links cleaned (Case 1)**: Inside the single `lock(data)`, `DetachProperty` iterates `data.RequiredProperties` and removes the derived property from each dependency's `UsedByProperties` via CAS `Remove` (no nested lock). Then sets `RequiredProperties = null` and `LastKnownValue = null`.
- **Backward links cleaned (Case 2)**: Inside the same `lock(data)`, `DetachProperty` takes a `UsedByProperties` snapshot and clears `UsedByProperties = null`. After releasing the lock, it iterates the snapshot and removes the source property from each dependent's `RequiredProperties` under `lock(derivedData)`.
- **Atomic state transition**: The single lock ensures `IsAttached = false`, forward cleanup (Case 1), and backward snapshot (Case 2) happen atomically. No concurrent thread can observe `IsAttached = false` while `UsedByProperties` is still populated.
- **No resurrection (Case 1)**: `DetachProperty` sets `IsAttached = false` inside the lock. Any concurrent `RecalculateDerivedProperty` that acquired the lock before detach will complete and re-add backlinks — but detach then removes them. Any recalculation that acquires the lock after detach sees `!IsAttached` and skips, so no backlinks are re-added.
- **No missed backlinks (Case 2)**: The `lock(data)` serializes with `StoreRecordedTouchedProperties`' backlink Add (which also locks `depData`). Any backlink added before the lock is in the snapshot; any Add attempt after the lock sees `IsAttached = false` and skips. Skipped backlinks also trigger a filter that removes the detached dependency from `RequiredProperties`, preventing forward reference leaks even when no future recalculation would clean them up.

### No memory leaks from cross-subject dependencies

Cross-subject dependencies (e.g., `Car.AveragePressure` → `Tire.Pressure`) create references between subjects via the dependency graph. These are cleaned up in three scenarios:

- **Source replacement** (e.g., replacing a tire): The write to the structural property triggers recalculation of `AveragePressure`. The getter now reads the new tire's `Pressure`, so `StoreRecordedTouchedProperties` removes the old tire from `RequiredProperties` and removes `AveragePressure` from the old tire's `UsedByProperties`. The old tire has no remaining backlinks and can be GC'd.
- **Derived property detach** (e.g., car removed from graph): `DetachProperty` Case 1 removes `AveragePressure` from all tires' `UsedByProperties`, and sets `RequiredProperties = null` and `LastKnownValue = null`. The tires have no remaining references to the car's properties and the car can be GC'd.
- **Source property detach** (e.g., tire removed from graph): `DetachProperty` Case 2 takes a snapshot of `UsedByProperties` under lock and clears it, then removes the tire's `Pressure` from `AveragePressure.RequiredProperties` under `lock(derivedData)`. If a concurrent `StoreRecordedTouchedProperties` was adding a backlink, the lock serialization ensures it is either in the snapshot (cleaned up) or skipped (filtered from `RequiredProperties`). No dangling forward or backward references remain.

In all cases, both forward and backward links are cleaned up immediately, so no cross-subject references prevent garbage collection.

## Performance Characteristics

| Scenario | Allocations | Cost |
|----------|-------------|------|
| Steady-state write (deps unchanged) | Zero | `SequenceEqual` on `RequiredProperties` span + one `Volatile.Write` (~1ns) |
| Dependency set changes, no concurrent write | One `PropertyReference[]` | Differential backward link updates, generation check skips re-evaluation |
| Dependency set changes + concurrent write | One `PropertyReference[]` | Above + stabilization loop (re-evaluation until deps stabilize) |
| Dependency set changes + concurrent detach | Two `PropertyReference[]` | Above + filter allocation for detached deps (rare) |
| Recording | Zero (pooled buffers) | Stack push/pop per frame |
| Recalculation | Zero (beyond getter) | One `lock(data)` + getter invocation + two `Volatile.Read` (~2ns) |
| Attach (common path) | Zero (beyond initial) | One getter invocation + two `Volatile.Read` (~2ns) |
| Backward link read (`UsedByProperties.Items`) | Zero | Returns stable `ReadOnlySpan` snapshot |

## Interaction with Other Interceptors

### Equality check (`WithEqualityCheck`)

When `PropertyValueEqualityCheckHandler` is registered (included in `WithFullPropertyTracking`), it compares old and new values before the write proceeds. If they are equal, the entire interceptor chain is skipped — no `WriteProperty` call, no dependent recalculation. This prevents redundant cascading recalculations when a source property is set to its current value.

The equality check also applies to the `SetPropertyValueWithInterception` call during recalculation. If the derived property's new computed value equals the old value, the change notification is suppressed.

### Transactions

During transaction capture (`SubjectTransaction.HasActiveTransaction && !IsCommitting`), dependent recalculations are suppressed. Derived properties are recalculated when the transaction commits and replays the writes. Additionally, derived property writes are never captured in transactions (`SubjectTransactionInterceptor` checks `!context.Property.Metadata.IsDerived`), since derived values are always computed from their dependencies.

## Design Notes

### Derived-to-derived dependencies are flattened

Derived properties that depend on other derived properties work correctly, but the intermediate derived property does not appear in the dependency graph. Instead, the dependencies are "flattened" to the underlying source properties.

```csharp
[Derived]
public string FullName => $"{FirstName} {LastName}";

[Derived]
public string FullNameWithPrefix => $"Mr. {FullName}";
```

When `FullNameWithPrefix` is evaluated, it calls `FullName`'s getter, which reads `FirstName` and `LastName` through the interceptor chain. The recorder captures `FirstName` and `LastName` as direct dependencies of `FullNameWithPrefix`. So when `FirstName` changes, both `FullName` and `FullNameWithPrefix` are recalculated (both appear in `FirstName.UsedByProperties`).

`FullName` itself does not appear in `FullNameWithPrefix.RequiredProperties` or in `FullName.UsedByProperties`, because derived property getters are plain C# property accesses that don't go through the interceptor chain.
