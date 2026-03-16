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
| `PropertyReferenceCollection` | Lock-free copy-on-write collection for backward dependencies (`UsedByProperties`). Uses CAS for thread-safe mutation. |
| `DerivedPropertyRecorder` | Thread-static recording buffer. Captures which properties are read during getter evaluation. Uses `ArrayPool` to avoid allocations in steady state. |
| `DerivedPropertyChangeHandlerExtensions` | Extension methods for accessing `DerivedPropertyData` via `PropertyReference`. |

## Dependency Graph

The system maintains a bidirectional dependency graph:

```
Forward (RequiredProperties):        Backward (UsedByProperties):
  FullName → [FirstName, LastName]     FirstName → [FullName]
                                       LastName  → [FullName]
```

- **Forward links** (`data.RequiredPropertiesSpan`): Which properties a derived property reads. Stored as a private `PropertyReference[]` with separate count, accessed via `ReadOnlySpan` under `lock(data)`. Buffer is reused when capacity is sufficient to avoid allocation on re-evaluation.
- **Backward links** (`data.GetUsedByProperties()`): Which derived properties depend on this property. Stored as private `PropertyReferenceCollection`, updated via lock-free CAS.

The two use different data structures because of their access patterns. Forward links are always read and written under `lock(data)` (the derived property's own data), so a plain array with buffer reuse is sufficient. Backward links are read without any lock (`WriteProperty` iterates dependents via `GetUsedByProperties()` which does `Volatile.Read`) and mutated from multiple lock scopes (`Remove` is called under the *derived* property's lock, not the *source* property's lock — so two derived properties detaching concurrently can call `Remove` on the same source's `UsedByProperties`). This requires the lock-free CAS copy-on-write wrapper.

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
      data.LastKnownValue = EvaluateAndStabilize(data, FullName)
      SetWriteTimestampUtcTicks(...)
    catch:
      // Getter threw — value will be computed on the next dependency write.
```

`EvaluateAndStabilize` handles the dependency recording, generation check, and stabilization loop internally (see Section 4 for details). If the getter throws during initial evaluation (typically during concurrent state transitions), the exception is caught and `LastKnownValue` remains null. The next dependency write triggers recalculation which retries with consistent state.

The generation-based stabilization inside `EvaluateAndStabilize` works as follows:

```
EvaluateAndStabilize(data, FullName)
  try:
    generationBefore = Volatile.Read(_writeGeneration)
    StartRecordingTouchedProperties()
    result = getter.Invoke(subject)              // evaluates "FirstName + LastName"
      → ReadProperty(FirstName)                 // recorder captures FirstName
      → ReadProperty(LastName)                  // recorder captures LastName
    recordedDeps = recorder.FinishRecording()
    dependenciesChanged = data.UpdateDependencies(FullName, recordedDeps, recorder)
    if dependenciesChanged && Volatile.Read(_writeGeneration) != generationBefore:
      // Concurrent write detected — fall back to stabilization loop
      do:
        StartRecordingTouchedProperties()
        result = getter.Invoke(subject)
        recordedDeps = recorder.FinishRecording()
        dependenciesChanged = data.UpdateDependencies(FullName, recordedDeps, recorder)
      while (dependenciesChanged)
    return result
  finally:
    DiscardActiveRecording()                     // always clears recorder buffer
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
  Interlocked.Increment(_writeGeneration)        // signal for AttachProperty/RecalculateDerivedProperty
  data = FirstName.TryGetDerivedPropertyData()
  if data is null → return                       // fast path: no tracking data
  // Self-recalculation: if this is a derived-with-setter property, recalculate it
  if data.HasRequiredProperties && Metadata.SetValue != null:
    RecalculateDerivedProperty(FirstName, timestamp)
  // Cascade: recalculate all dependent derived properties
  usedByItems = data.GetUsedByProperties()                      // Volatile.Read + Items span
  if usedByItems has entries:
    for each dependent (e.g., FullName):
      RecalculateDerivedProperty(FullName, timestamp)
```

The `_writeGeneration` increment uses `Interlocked.Increment` (full fence) so that each concurrent writer produces a unique counter value — no lost increments. `AttachProperty`/`RecalculateDerivedProperty` detect concurrent writes via `Volatile.Read` (acquire semantics). The full fence from `Interlocked.Increment` pairs with `Volatile.Read`'s acquire semantics to guarantee that committed property values are visible when the counter change is observed.

**Derived properties with setters** (created via `AddDerivedProperty<T>(name, getValue, setValue)`) have both a getter and a setter. The setter modifies internal state as a side effect, but the actual property value is always determined by the getter. When the setter is called, `WriteProperty` detects `HasRequiredProperties && SetValue != null` and triggers recalculation to re-evaluate the getter and fire a change notification with the correct computed value.

### 4. Recalculation

```
RecalculateDerivedProperty(FullName, timestamp)
  lock(data)
    if data.IsRecalculating → return             // re-entrancy guard
    if !data.IsAttached → return                  // zombie prevention
    data.IsRecalculating = true
    try:
      oldValue = data.LastKnownValue
      try:
        newValue = EvaluateAndStabilize(data, FullName)
      catch:
        return                                   // keep LastKnownValue, skip notification
      data.LastKnownValue = newValue
      sequence = ++data.RecalculationSequence    // monotonic counter for stale detection
      SetWriteTimestampUtcTicks(timestamp)       // inherits trigger's timestamp
    finally:
      data.IsRecalculating = false
  // Notifications fired OUTSIDE lock(data) to avoid deadlock with
  // LifecycleInterceptor's lock(_attachedSubjects).
  // Two guards prevent stale notifications:
  if sequence != Volatile.Read(data.RecalculationSequence) → return  // superseded
  if !ReferenceEquals(newValue, Volatile.Read(data.LastKnownValue)) → return  // overwritten
  WithSource(null):                              // marks as internal recalculation
    SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate)
  RaisePropertyChanged("FullName")               // INotifyPropertyChanged integration
```

The getter is evaluated inside `EvaluateAndStabilize`, which handles dependency recording, the generation check, and the stabilization loop. If the getter throws (typically during concurrent state transitions), the exception is caught and `LastKnownValue` remains unchanged. The concurrent writer's `WriteProperty` cascade will re-trigger recalculation with consistent state after the lock is released.

The generation check avoids re-evaluation when dependencies change but no concurrent write occurred (e.g., `ChangeAllTires` swaps tire references, changing deps from old tires to new tires). The stabilization loop only runs when a concurrent write is actually detected.

Key details of the change notification:
- **Notifications outside lock**: `SetPropertyValueWithInterception` and `RaisePropertyChanged` are fired after releasing `lock(data)`. This prevents a deadlock between `lock(data)` (held during recalculation) and `lock(_attachedSubjects)` (acquired by `LifecycleInterceptor.WriteProperty` when `TProperty` is `object`). Two guards prevent stale notifications: a `RecalculationSequence` check (skips if a newer recalculation completed) and a `ReferenceEquals` check on `LastKnownValue` (skips if another thread overwrote the value). See the "Deadlock prevention" section for details.
- **Timestamp inheritance**: The derived property receives the same timestamp as the write that triggered the recalculation, ensuring consistent timestamps within a mutation context.
- **`WithSource(null)`**: Wraps the notification in a scope that clears any external source context. This marks the change as an internal recalculation, preventing source transaction handlers from writing it back to an external source.
- **`NoOpWriteDelegate`**: Since derived properties have no backing field, the write delegate is a no-op (`static (_, _) => { }`). The call to `SetPropertyValueWithInterception` exists solely to fire the change notification through the interceptor chain (observable, queue, etc.) with the correct old and new values.
- **`IRaisePropertyChanged`**: If the subject implements `IRaisePropertyChanged`, `RaisePropertyChanged` is called to support standard `INotifyPropertyChanged` data binding.

### 5. Detach (cleanup)

When a subject is removed from the object graph, `DetachProperty` cleans up both directions.

`DetachProperty` uses `TryGetDerivedPropertyData()` and returns early if no tracking data exists. This avoids `ConcurrentDictionary.GetOrAdd` allocations for source properties that were never dependencies — a significant performance optimization when detaching subjects with many non-dependency properties (e.g., detaching 1000 cars where only 1 of 18 properties per car participates in derived property tracking).

For properties with tracking data, both cases are handled in a single `lock(data)` block, followed by Case 2 cleanup outside the lock:

```
DetachProperty(property)
  data = property.TryGetDerivedPropertyData()
  if data is null → return                       // skip untracked properties
  lock(data)
    usedBySnapshot = data.DetachAndSnapshotUsedBy(property)
      // sets IsAttached = false
      // Case 1 (derived only): removes from each dependency's UsedByProperties (CAS)
      // clears RequiredProperties and LastKnownValue
      // Case 2: snapshots UsedByProperties.ItemsArray, clears UsedByProperties
  // release lock before Case 2 processing to avoid nesting with lock(derivedData)
  for each derived in usedBySnapshot:
    lock(derivedData)
      derivedData.RemoveRequiredProperty(property)
```

The single lock ensures `IsAttached`, forward cleanup (Case 1), and backward snapshot (Case 2) are atomic — no window where a concurrent thread could see `IsAttached=false` but `UsedByProperties` still populated.

The lock serializes with `UpdateDependencies`' backlink Add (which also acquires `lock(depData)` on the same object). This ensures either:
- The backlink was added before the snapshot → we see it and clean up the forward reference.
- `UpdateDependencies` acquires the lock after us → sees `IsAttached = false` → skips the Add.

## Dependency Updates (`DerivedPropertyData.UpdateDependencies`)

This method is called under `lock(data)` (the derived property's data) and maintains both forward and backward links:

1. **Fast path**: If `previousDeps.SequenceEqual(recordedDeps)` → clear recorder, return `false` (no allocation).
2. **Slow path**:
   - Differential backward link update against the recorder span (both spans valid — array not yet modified, recorder not yet cleared).
   - For each old dependency no longer used: `Remove(derivedProperty)` from its `UsedByProperties` (CAS).
   - For each new dependency not previously tracked: `lock(depData)` → check `depData.IsAttached` → if attached, `Add(derivedProperty)` to its `UsedByProperties`. The lock serializes with `DetachProperty` Case 2 on the same dependency data.
   - If no skipped deps: `SetRequiredProperties(recordedDeps)` reuses the existing buffer when capacity is sufficient (zero allocation), then clears the recorder.
   - If any backlink Add was skipped (dependency detaching): copy to owned array, clear recorder, then re-check each dependency under `lock(depData)`. If the dependency was re-attached concurrently (`IsAttached` now true), call idempotent `Add(derivedProperty)` to repair the missing backward link. If still detached, remove from `RequiredProperties` and clean the backward link. Lock ordering is safe (derived → dependency, same direction as the backlink loop). This prevents both forward reference leaks and missing backward links after concurrent re-attachment.
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

`PropertyReferenceCollection` uses copy-on-write with CAS:

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

`RecalculateDerivedProperty` and `UpdateDependencies` nest locks in the derived → dependency direction: `lock(D_data)` (outer, from `RecalculateDerivedProperty`) → `lock(X_data)` (inner, from the backlink Add loop in `UpdateDependencies`). A deadlock would require a cycle in the lock acquisition order, which would imply a circular dependency in the property graph — but circular getter dependencies cause infinite recursion before any lock is reached, so the graph is always a DAG and deadlock is impossible.

`DetachProperty` uses a single `lock(data)` for all local cleanup (`IsAttached`, forward deps, backward snapshot), then acquires `lock(derivedData)` sequentially for `RequiredProperties` cleanup. Because it never holds two locks simultaneously, it cannot participate in a lock cycle. Case 1's forward cleanup (removing from dependencies' `UsedByProperties`) uses CAS inside the lock — no nested lock acquisition.

## Concurrency Scenarios

### Re-entrancy during derived-with-setter recalculation

Derived properties with setters (added via `AddDerivedProperty<T>(name, getValue, setValue)`) create a re-entrancy path: `RecalculateDerivedProperty` calls `SetPropertyValueWithInterception` (outside the lock), which re-enters `WriteProperty`, which would call `RecalculateDerivedProperty` again. The re-entrant call acquires `lock(data)`, sees `IsRecalculating = false` (already cleared), and proceeds. The equality check interceptor bounds this: the getter returns the same value (nothing changed), so the notification is suppressed.

### Deadlock prevention: notifications outside lock

`RecalculateDerivedProperty` fires notifications (`SetPropertyValueWithInterception`, `RaisePropertyChanged`) after releasing `lock(data)`. This prevents a lock ordering inversion between `lock(data)` and `lock(_attachedSubjects)` in `LifecycleInterceptor`:

- **Without this**: Thread A holds `lock(_attachedSubjects)` → `DetachProperty` → wants `lock(data)`. Thread B holds `lock(data)` → `SetPropertyValueWithInterception` → `LifecycleInterceptor.WriteProperty` → wants `lock(_attachedSubjects)`. Deadlock.
- **With this**: `lock(data)` is released before `SetPropertyValueWithInterception`, so Thread B never holds `lock(data)` when acquiring `lock(_attachedSubjects)`.

The tradeoff: concurrent recalculations could produce out-of-order or stale notifications if unmitigated. Two guards prevent this:

1. **`RecalculationSequence` check**: A monotonic counter incremented under `lock(data)` on each recalculation. After releasing the lock, `Volatile.Read` compares the thread's captured sequence against the current value. If a newer recalculation completed in between, the notification is skipped.

2. **`ReferenceEquals` check on `LastKnownValue`**: Even if the sequence check passes (the thread checked before the next recalculation entered the lock), a second guard compares the thread's computed `newValue` reference against `data.LastKnownValue` via `Volatile.Read`. Since `data.LastKnownValue = newValue` stores the same reference inside the lock, a mismatch means another thread overwrote it — the notification is skipped. This works for boxed value types because each getter evaluation produces a distinct boxed reference.

Together, these guards ensure the last notification always reflects the final computed value. In the rare case where both threads pass both checks (their computed values happen to be reference-equal, e.g., both null), the duplicate notification carries the correct value and is harmless.

Additionally, `LifecycleInterceptor.WriteProperty` uses `context.Property.Metadata.Type.CanContainSubjects<TProperty>()` (the declared metadata type) rather than just `CanContainSubjects<TProperty>()` (the generic parameter). `TProperty` is a hint that may be widened to `object` through non-generic paths like `SetPropertyValueWithInterception`, which would cause `CanContainSubjects<object>()` to return `true` for value-type properties (e.g., `decimal`). The metadata type check ensures value-type properties never enter the lifecycle lock.

### Concurrent write detection via `_writeGeneration`

When a derived property has conditional dependencies (e.g., `Display => UseFirstName ? FirstName : LastName`), the dependency set changes based on runtime state. A concurrent write to a newly-added dependency could land between getter evaluation and backlink registration — the write would not trigger recalculation because the backlink isn't registered yet.

Both `AttachProperty` and `RecalculateDerivedProperty` use a generation-based detection scheme instead of unconditionally re-evaluating:

1. `WriteProperty` increments `_writeGeneration` via `Interlocked.Increment` (full fence) on every write.
2. `AttachProperty`/`RecalculateDerivedProperty` read `_writeGeneration` via `Volatile.Read` (acquire fence) before and after evaluation.
3. If unchanged → no concurrent write occurred → skip the stabilization loop.
4. If changed → a concurrent write happened → fall back to the full stabilization loop.

`Interlocked.Increment` provides a full memory fence, pairing with `Volatile.Read`'s acquire semantics: if the counter change is observed, all prior writes (including the committed property value) are guaranteed visible to the reading thread. Each concurrent writer produces a unique counter value — no lost increments.

`_writeGeneration` is a static field, shared across all handler instances. This ensures writes from any context are detected, even when dependencies span contexts (e.g., via context inheritance). The tradeoff is that unrelated writes (from other contexts) may cause false positives — triggering the stabilization loop when no relevant concurrent write occurred. False positives only affect `AttachProperty` and `RecalculateDerivedProperty` when dependencies change; the steady-state write path (`dependenciesChanged = false`) never checks the generation, so there is zero overhead from false positives in the common case. A false positive costs one extra getter evaluation that exits immediately (deps unchanged).

In the common case (stable dependencies or single-threaded construction), the generation is unchanged and the loop is skipped — zero extra getter evaluations. The stabilization loop only runs when a concurrent write is actually detected.

### Concurrent detach and recalculation

Two race conditions must be handled when `DetachProperty` runs concurrently with `RecalculateDerivedProperty` / `UpdateDependencies`:

**Race 1 — Zombie backlink resurrection (Case 1):** `WriteProperty` takes a snapshot of `UsedByProperties` and iterates it. A concurrent `DetachProperty` may remove a derived property's backlinks between the snapshot and the recalculation call. Without protection, `RecalculateDerivedProperty` would re-add the backlinks, creating zombie dependencies.

`DetachProperty` Case 1 and `RecalculateDerivedProperty` both acquire `lock(data)` on the same derived property's data, serializing them. `DetachProperty` sets `data.IsAttached = false` inside the lock. `RecalculateDerivedProperty` checks `IsAttached` inside the lock and bails out if false. `AttachProperty` sets `IsAttached = true` under lock to support re-attachment.

Both orderings produce a correct final state:
- **Detach wins lock**: clears `IsAttached`, removes backlinks. Recalculation then sees `!IsAttached` and skips.
- **Recalculation wins lock**: evaluates and re-adds backlinks. Detach then removes them. Final state is clean.

**Race 2 — Missed backlink in Case 2 snapshot:** When a source property X is being detached, `DetachProperty` Case 2 takes a snapshot of `X.UsedByProperties` to find dependent derived properties. Concurrently, `UpdateDependencies` may be adding a new backlink (D → X) to `X.UsedByProperties`. If the Add completes after the snapshot, `DetachProperty` misses D, leaving a stale forward reference (`D.RequiredProperties` contains X) and a stale backward reference (D in `X.UsedByProperties`).

This is solved by locking `X.DerivedPropertyData` in both places:
- `DetachProperty` Case 2: `lock(data)` → `DetachAndSnapshotUsedBy` sets `IsAttached = false` + takes snapshot + clears `UsedByProperties` → release lock.
- `UpdateDependencies` backlink loop: `lock(depData)` → check `IsAttached` → if true, Add → release lock.

Since both operations lock the same object (`X.DerivedPropertyData`), they are fully serialized:
- **DetachProperty wins lock**: sets `IsAttached = false`, takes snapshot (D not present), clears `UsedByProperties`. `UpdateDependencies` then acquires the lock, sees `IsAttached = false`, skips the Add. The skipped dependency is also filtered from `RequiredProperties` by the post-loop cleanup.
- **UpdateDependencies wins lock**: sees `IsAttached = true`, adds D to `UsedByProperties`. `DetachProperty` then acquires the lock, takes snapshot (D is present), cleans up D's `RequiredProperties`.

No window exists where a backlink is added but not visible to `DetachProperty`.

## Correctness Guarantees

### Thread safety: no data corruption

Every piece of shared mutable state is protected by exactly one synchronization mechanism:

| State | Protection | Accessed by |
|-------|-----------|-------------|
| `data.RequiredProperties` | `lock(data)` | `UpdateDependencies`, `DetachAndSnapshotUsedBy`, `DetachProperty` Case 2 |
| `data.LastKnownValue` | `lock(data)` (write) / `Volatile.Read` (read) | `RecalculateDerivedProperty` (write under lock, read outside lock for stale notification check), `AttachProperty`, `DetachAndSnapshotUsedBy` |
| `data.IsRecalculating` | `lock(data)` | `RecalculateDerivedProperty` |
| `data.IsAttached` | `lock(data)` | `DetachAndSnapshotUsedBy`, `RecalculateDerivedProperty`, `AttachProperty`, `UpdateDependencies` (backlink loop) |
| `data.UsedByProperties` (collection contents) | CAS (copy-on-write) | `UpdateDependencies`, `DetachAndSnapshotUsedBy` |
| `data.UsedByProperties` (field itself) | `lock(data)` + `Interlocked.CompareExchange` | `DetachAndSnapshotUsedBy` (nulls under lock), `AddUsedByProperty` (CAS create) |
| `_recorder` | `[ThreadStatic]` (no sharing) | `ReadProperty`, `StartRecording`, `UpdateDependencies` (via parameter) |
| `data.RecalculationSequence` | `lock(data)` (write) / `Volatile.Read` (read) | `RecalculateDerivedProperty` (increment under lock, read outside lock for stale notification check) |
| `_writeGeneration` (static) | `Interlocked.Increment` (full fence) / `Volatile.Read` (acquire) | `WriteProperty` (increment), `AttachProperty` + `RecalculateDerivedProperty` (check) |

Nested locks occur in `UpdateDependencies`: `lock(D_data)` (outer, from `RecalculateDerivedProperty`) → `lock(X_data)` (inner, backlink Add). The acquisition order follows the dependency DAG (derived → source). Circular dependencies would cause infinite recursion in getters before any lock is reached, so deadlock is impossible. `DetachProperty` uses a single `lock(data)` for all local cleanup (via `DetachAndSnapshotUsedBy`), then acquires `lock(derivedData)` sequentially (never nested), so it cannot participate in a lock cycle. `RecalculateDerivedProperty` releases `lock(data)` before firing notifications via `SetPropertyValueWithInterception`, which may acquire `lock(_attachedSubjects)` in `LifecycleInterceptor` — since `lock(data)` is not held, no cycle is possible.

### Value correctness: derived value always reflects current state

After all concurrent writes complete and recalculations settle, every derived property's value matches what its getter would return if called with the current source values. This is guaranteed by three mechanisms working together:

1. **Backlink-driven recalculation**: Once a dependency's backward link includes a derived property, any write to that dependency triggers `RecalculateDerivedProperty` via `WriteProperty`. The recalculation acquires `lock(data)`, so concurrent recalculations of the same derived property are serialized — each one sees the most recent source values.

2. **Generation-based concurrent write detection**: `WriteProperty` increments `_writeGeneration` on every write (`Interlocked.Increment`, full fence). `AttachProperty` and `RecalculateDerivedProperty` read the counter before and after evaluation (`Volatile.Read`, acquire fence). If unchanged, no concurrent write occurred and re-evaluation is skipped. If changed, the stabilization loop runs to catch writes that landed between getter evaluation and backlink registration.

3. **Stabilization loop (on concurrent write detection)**: When the generation check detects a concurrent write AND the dependency set changed, the `do/while` loop re-evaluates until dependencies stabilize:
   - **First iteration**: evaluates getter, registers backlinks.
   - **Subsequent iterations**: re-evaluate with backlinks in place, catching writes that happened before backlinks were registered. Each iteration that acquires backlinks ensures any *further* concurrent write to those dependencies triggers recalculation via the normal path and blocks on `lock(data)`.
   - The loop exits when `UpdateDependencies` returns `false` (deps unchanged).

   In the common case (no concurrent writes, or stable dependencies), the generation check avoids the loop entirely — zero extra getter evaluations.

Because `_writeGeneration` is static (global), writes from any context are detected — no cross-context blind spots. False positives from unrelated contexts only trigger re-evaluation when deps actually changed, and the re-evaluation exits immediately when deps are stable.

### No zombie dependencies after detach

When a property is detached, no stale references remain in the dependency graph:

- **Forward links cleaned (Case 1)**: Inside the single `lock(data)`, `DetachAndSnapshotUsedBy` iterates `RequiredProperties` and removes the derived property from each dependency's `UsedByProperties` via CAS `Remove` (no nested lock). Then clears `RequiredProperties` and `LastKnownValue`.
- **Backward links cleaned (Case 2)**: Inside the same `lock(data)`, `DetachAndSnapshotUsedBy` takes a `UsedByProperties` snapshot and clears it. After releasing the lock, `DetachProperty` iterates the snapshot and removes the source property from each dependent's `RequiredProperties` under `lock(derivedData)`.
- **Atomic state transition**: The single lock ensures `IsAttached = false`, forward cleanup (Case 1), and backward snapshot (Case 2) happen atomically. No concurrent thread can observe `IsAttached = false` while `UsedByProperties` is still populated.
- **No resurrection (Case 1)**: `DetachProperty` sets `IsAttached = false` inside the lock. Any concurrent `RecalculateDerivedProperty` that acquired the lock before detach will complete and re-add backlinks — but detach then removes them. Any recalculation that acquires the lock after detach sees `!IsAttached` and skips, so no backlinks are re-added.
- **No missed backlinks (Case 2)**: For properties **with** tracking data, `DetachAndSnapshotUsedBy` sets `IsAttached = false` under `lock(data)`. The lock serializes with `UpdateDependencies`' backlink Add (which also locks `depData`). Any backlink added before the lock is in the snapshot; any Add attempt after the lock sees `IsAttached = false` and skips. Skipped backlinks trigger a locked filter that re-checks `IsAttached` under `lock(depData)`: if the dependency was re-attached concurrently, the filter calls idempotent `Add(derivedProperty)` to repair the missing backward link; if still detached, it removes the dependency from `RequiredProperties` and cleans the backward link.
- **Untracked source properties**: For source properties that were **never** dependencies (no `DerivedPropertyData` exists), `DetachProperty` skips them entirely via `TryGetDerivedPropertyData() → null → return`. A theoretical race exists where a concurrent `UpdateDependencies` creates data with `IsAttached = true` after `DetachProperty` exits, leaving a stale forward reference in the derived property's `RequiredProperties`. However, this is safe in practice: for a derived property D to read source property X on subject S, the getter must reach S through the object graph — meaning S is already kept alive by a structural reference from the getter's reachable path (a tracked property, closure capture, etc.), not solely by `RequiredProperties`. The stale forward reference is redundant with the existing structural reference and is cleaned up on the next recalculation of D (when the structural reference changes, D re-records its dependencies and drops X). This tradeoff avoids `ConcurrentDictionary.GetOrAdd` for every untracked property during detach, which profiling showed to be the dominant cost in bulk detach scenarios.

### No memory leaks from cross-subject dependencies

Cross-subject dependencies (e.g., `Car.AveragePressure` → `Tire.Pressure`) create references between subjects via the dependency graph. These are cleaned up in three scenarios:

- **Source replacement** (e.g., replacing a tire): The write to the structural property triggers recalculation of `AveragePressure`. The getter now reads the new tire's `Pressure`, so `UpdateDependencies` removes the old tire from `RequiredProperties` and removes `AveragePressure` from the old tire's `UsedByProperties`. The old tire has no remaining backlinks and can be GC'd.
- **Derived property detach** (e.g., car removed from graph): `DetachAndSnapshotUsedBy` (Case 1) removes `AveragePressure` from all tires' `UsedByProperties`, and clears `RequiredProperties` and `LastKnownValue`. The tires have no remaining references to the car's properties and the car can be GC'd.
- **Source property detach** (e.g., tire removed from graph): For tracked source properties, `DetachAndSnapshotUsedBy` (Case 2) takes a snapshot of `UsedByProperties` under lock and clears it, then `DetachProperty` removes the tire's `Pressure` from `AveragePressure.RequiredProperties` under `lock(derivedData)`. If a concurrent `UpdateDependencies` was adding a backlink, the lock serialization ensures it is either in the snapshot (cleaned up) or skipped (filtered from `RequiredProperties`). For untracked source properties, `DetachProperty` skips them (no data to clean). See "Untracked source properties" above for the theoretical race and why it is safe.

In all cases for tracked properties, both forward and backward links are cleaned up immediately, so no cross-subject references prevent garbage collection.

## Performance Characteristics

| Scenario | Allocations | Cost |
|----------|-------------|------|
| Steady-state write (deps unchanged) | Zero | `SequenceEqual` on `RequiredProperties` span + one `Interlocked.Increment` (~5-10ns) |
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
