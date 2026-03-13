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
    do:
      StartRecordingTouchedProperties()         // push recording frame
      result = getter.Invoke(subject)            // evaluates "FirstName + LastName"
        → ReadProperty(FirstName)               // recorder captures FirstName
        → ReadProperty(LastName)                // recorder captures LastName
      dependenciesChanged = StoreRecordedTouchedProperties(FullName, data)
        // sets data.RequiredProperties = [FirstName, LastName]
        // adds FullName to FirstName.UsedByProperties
        // adds FullName to LastName.UsedByProperties
        // returns true (deps changed from null to [FirstName, LastName])
    while (dependenciesChanged)                  // second iteration: deps unchanged, returns false
    data.LastKnownValue = result
    SetWriteTimestampUtcTicks(...)
```

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
  data = FirstName.TryGetDerivedPropertyData()
  // Self-recalculation: if this is a derived-with-setter property, recalculate it
  if data.RequiredProperties != null && Metadata.SetValue != null:
    RecalculateDerivedProperty(FirstName, timestamp)
  // Cascade: recalculate all dependent derived properties
  if data.UsedByProperties has entries:
    for each dependent (e.g., FullName):
      RecalculateDerivedProperty(FullName, timestamp)
```

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
      do:
        StartRecordingTouchedProperties()
        newValue = getter.Invoke(subject)
        dependenciesChanged = StoreRecordedTouchedProperties(FullName, data)
      while (dependenciesChanged)                // re-evaluate if deps changed
      data.LastKnownValue = newValue
      SetWriteTimestampUtcTicks(timestamp)       // inherits trigger's timestamp
      WithSource(null):                          // marks as internal recalculation
        SetPropertyValueWithInterception(newValue, oldValue, NoOpWriteDelegate)
      RaisePropertyChanged("FullName")           // INotifyPropertyChanged integration
    finally:
      data.IsRecalculating = false
```

Key details of the change notification:
- **Timestamp inheritance**: The derived property receives the same timestamp as the write that triggered the recalculation, ensuring consistent timestamps within a mutation context.
- **`WithSource(null)`**: Wraps the notification in a scope that clears any external source context. This marks the change as an internal recalculation, preventing source transaction handlers from writing it back to an external source.
- **`NoOpWriteDelegate`**: Since derived properties have no backing field, the write delegate is a no-op (`static (_, _) => { }`). The call to `SetPropertyValueWithInterception` exists solely to fire the change notification through the interceptor chain (observable, queue, etc.) with the correct old and new values.
- **`IRaisePropertyChanged`**: If the subject implements `IRaisePropertyChanged`, `RaisePropertyChanged` is called to support standard `INotifyPropertyChanged` data binding.

### 5. Detach (cleanup)

When a subject is removed from the object graph, `DetachProperty` cleans up both directions:

**Case 1 — Derived property detached** (e.g., FullName is detached):
```
DetachProperty(FullName)
  lock(data)
    data.IsAttached = false
    for each dependency in data.RequiredProperties:
      dependency.UsedByProperties.Remove(FullName)   // CAS
```

**Case 2 — Source property detached** (e.g., FirstName is detached):
```
DetachProperty(FirstName)
  for each derived in FirstName.UsedByProperties:
    lock(derivedData)
      remove FirstName from derivedData.RequiredProperties
```

## Dependency Updates (`StoreRecordedTouchedProperties`)

This method is called under `lock(data)` and maintains both forward and backward links:

1. **Fast path**: If `previousDeps.SequenceEqual(recordedDeps)` → return `false` (no allocation).
2. **Slow path**: Replace `data.RequiredProperties` with new array.
   - For each old dependency no longer used: `Remove(derivedProperty)` from its `UsedByProperties`.
   - For each new dependency not previously tracked: `Add(derivedProperty)` to its `UsedByProperties`.
   - Return `true` (caller should re-evaluate).

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

### No cross-property nested locking

`lock(data)` is only acquired on the derived property's own data. Backward link updates use CAS, not locks. This eliminates deadlock risk from lock ordering.

## Concurrency Scenarios

### Re-entrancy during derived-with-setter recalculation

Derived properties with setters (added via `AddDerivedProperty<T>(name, getValue, setValue)`) create a re-entrancy path: `RecalculateDerivedProperty` calls `SetPropertyValueWithInterception`, which re-enters `WriteProperty`, which would call `RecalculateDerivedProperty` again. Since `lock(data)` is re-entrant for the same thread, the lock alone doesn't prevent this.

The `data.IsRecalculating` flag guards against this. The re-entrant call sees `IsRecalculating = true` inside the lock and returns immediately. Cross-thread callers block on the lock and see `false` after the first thread releases — so they proceed normally.

### Conditional dependencies and concurrent writes

When a derived property has conditional dependencies (e.g., `Display => UseFirstName ? FirstName : LastName`), the dependency set changes based on runtime state. A concurrent write to a newly-added dependency could land between getter evaluation and backlink registration — the write would not trigger recalculation because the backlink isn't registered yet.

The stabilization loop handles this. `StoreRecordedTouchedProperties` returns `true` when the dependency set changed, causing the `do/while` loop to re-evaluate the getter:

```csharp
do
{
    StartRecordingTouchedProperties();
    newValue = getter.Invoke(subject);
    dependenciesChanged = StoreRecordedTouchedProperties(derivedProperty, data);
}
while (dependenciesChanged);
```

After the first iteration registers backlinks, any concurrent write to a dependency triggers recalculation via the normal `UsedByProperties` path and blocks on `lock(data)`. The second iteration catches writes that happened before backlinks were in place. In the common case (stable dependencies), `SequenceEqual` returns `true` on the first iteration and the loop runs exactly once with zero overhead.

### Concurrent detach and recalculation

When `WriteProperty` takes a snapshot of `UsedByProperties` and then iterates it, a concurrent `DetachProperty` may remove a derived property's backlinks between the snapshot and the recalculation call. Without protection, `RecalculateDerivedProperty` would re-add the backlinks via `StoreRecordedTouchedProperties`, creating zombie dependencies.

`DetachProperty` Case 1 and `RecalculateDerivedProperty` both acquire `lock(data)` on the same derived property's data, serializing them. `DetachProperty` sets `data.IsAttached = false` inside the lock. `RecalculateDerivedProperty` checks `IsAttached` inside the lock and bails out if false. `AttachProperty` sets `IsAttached = true` to support re-attachment.

Both orderings produce a correct final state:
- **Detach wins lock**: clears `IsAttached`, removes backlinks. Recalculation then sees `!IsAttached` and skips.
- **Recalculation wins lock**: evaluates and re-adds backlinks. Detach then removes them. Final state is clean.

## Correctness Guarantees

### Thread safety: no data corruption

Every piece of shared mutable state is protected by exactly one synchronization mechanism:

| State | Protection | Accessed by |
|-------|-----------|-------------|
| `data.RequiredProperties` | `lock(data)` | `StoreRecordedTouchedProperties`, `DetachProperty` Case 2 |
| `data.LastKnownValue` | `lock(data)` | `RecalculateDerivedProperty`, `AttachProperty` |
| `data.IsRecalculating` | `lock(data)` | `RecalculateDerivedProperty` |
| `data.IsAttached` | `lock(data)` | `DetachProperty` Case 1, `RecalculateDerivedProperty`, `AttachProperty` |
| `data.UsedByProperties` (collection contents) | CAS (copy-on-write) | `StoreRecordedTouchedProperties`, `DetachProperty` Case 1 |
| `data.UsedByProperties` (field itself) | `Interlocked.CompareExchange` | `GetOrCreateUsedByProperties` |
| `_recorder` | `[ThreadStatic]` (no sharing) | `ReadProperty`, `StartRecording`, `StoreRecordedTouchedProperties` |

No cross-property nested locking occurs. `lock(data)` is only acquired on the derived property's own data. Backward link updates to other properties' `UsedByProperties` use CAS, not locks. This eliminates deadlock risk.

### Value correctness: derived value always reflects current state

After all concurrent writes complete and recalculations settle, every derived property's value matches what its getter would return if called with the current source values. This is guaranteed by two mechanisms working together:

1. **Backlink-driven recalculation**: Once a dependency's backward link includes a derived property, any write to that dependency triggers `RecalculateDerivedProperty` via `WriteProperty`. The recalculation acquires `lock(data)`, so concurrent recalculations of the same derived property are serialized — each one sees the most recent source values.

2. **Stabilization loop for dependency changes**: When the dependency set changes (conditional dependencies), there is a window between getter evaluation and backlink registration where a concurrent write to a newly-added dependency would not trigger recalculation. The `do/while` loop closes this window:
   - **First iteration**: evaluates getter, registers backlinks. Any concurrent write that happened *before* backlink registration is not yet visible.
   - **Second iteration**: re-evaluates getter with backlinks now in place. Picks up any writes that landed during the first iteration. If deps are stable, `SequenceEqual` returns true and the loop exits.
   - **After the loop**: backlinks are registered. Any *subsequent* write triggers recalculation via the normal path and blocks on `lock(data)`.

   Together, the loop catches writes from *before* backlink registration, and the lock ensures writes *after* registration are handled. No writes are missed.

### No zombie dependencies after detach

When a derived property is detached, no stale references remain in the dependency graph:

- **Forward links cleaned**: `DetachProperty` Case 1 iterates `data.RequiredProperties` and removes the derived property from each dependency's `UsedByProperties` via CAS `Remove`.
- **Backward links cleaned**: `DetachProperty` Case 2 iterates `data.UsedByProperties` and removes the source property from each dependent's `RequiredProperties` under `lock(derivedData)`.
- **No resurrection**: `DetachProperty` Case 1 acquires `lock(data)` and sets `IsAttached = false`. Any concurrent `RecalculateDerivedProperty` that acquired the lock before detach will complete and re-add backlinks — but detach then removes them. Any recalculation that acquires the lock after detach sees `!IsAttached` and skips, so no backlinks are re-added.

### No memory leaks from cross-subject dependencies

Cross-subject dependencies (e.g., `Car.AveragePressure` → `Tire.Pressure`) create references between subjects via the dependency graph. These are cleaned up in two scenarios:

- **Source replacement** (e.g., replacing a tire): The write to the structural property triggers recalculation of `AveragePressure`. The getter now reads the new tire's `Pressure`, so `StoreRecordedTouchedProperties` removes the old tire from `RequiredProperties` and removes `AveragePressure` from the old tire's `UsedByProperties`. The old tire has no remaining backlinks and can be GC'd.
- **Derived property detach** (e.g., car removed from graph): `DetachProperty` Case 1 removes `AveragePressure` from all tires' `UsedByProperties`. The tires have no remaining references to the car's properties and the car can be GC'd.
- **Source property detach** (e.g., tire removed from graph): `DetachProperty` Case 2 removes the tire's `Pressure` from `AveragePressure.RequiredProperties` under lock. No dangling forward references remain.

In all cases, both forward and backward links are cleaned up, so no cross-subject references prevent garbage collection.

## Performance Characteristics

| Scenario | Allocations | Cost |
|----------|-------------|------|
| Steady-state write (deps unchanged) | Zero | `SequenceEqual` on `RequiredProperties` span |
| Dependency set changes | One `PropertyReference[]` | Differential backward link updates (CAS) |
| Recording | Zero (pooled buffers) | Stack push/pop per frame |
| Recalculation | Zero (beyond getter) | One `lock(data)` + getter invocation |
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
