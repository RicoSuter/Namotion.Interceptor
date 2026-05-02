# Deferred Subject Removal

## Problem

When a subject moves between structural properties within the same update (e.g., DictA → DictB), the `SubjectUpdateApplier` processes properties sequentially. If the source property (DictA) is processed first, the subject is fully detached — removed from both `_knownSubjects` and `_subjectIdToSubject`, with parent/child cleanup executed. When the target property (DictB) is processed, the subject is re-attached from scratch.

During the gap between detach and re-attach:

1. **`_subjectIdToSubject`** is missing the subject → the applier's own `TryGetSubjectById` fails for other properties referencing the same subject within the same update.
2. **`_knownSubjects`** is missing the subject → `TryGetRegisteredSubject()` returns null → the CQP filter (running on a background thread) drops value changes for the subject → permanent divergence.
3. **Parent/child links** are cleaned up → `RegisteredSubject.Parents` and `RegisteredSubjectProperty.Children` are inconsistent until re-attachment completes.

Subject swaps (DictA: X↔Y, DictB: Y↔X) make this unsolvable by processing order — no safe order exists.

## Solution: SuppressRemoval

Add `IDisposable SuppressRemoval()` to `SubjectRegistry`. During suppression, context-detach cleanup is deferred — subjects stay in `_knownSubjects` and `_subjectIdToSubject`, and parent/child cleanup is skipped. On resume, only subjects that are genuinely orphaned (not re-attached by any thread) are cleaned up.

### Thread Model

The suppression counter is `[ThreadStatic]` — each thread independently controls whether its lifecycle-triggered detaches are deferred. This ensures:

- **Thread A** (applier, with scope): ContextDetach is deferred. Subject stays visible in both maps.
- **Thread B** (no scope, concurrent mutation): ContextDetach executes immediately. Not affected by Thread A's scope.

The deferred detaches set is also `[ThreadStatic]` — each thread tracks only its own deferred subjects.

### Resume Logic

On scope dispose (counter reaches zero), for each deferred subject, the resume logic checks **actual state** rather than bookkeeping:

```
lock (_knownSubjects):
    for each subject in s_deferredDetaches:
        if _knownSubjects.TryGetValue(subject, out registered):
            if registered.Parents.Length == 0:
                // Genuinely orphaned — execute full cleanup
                execute parent/child cleanup
                _knownSubjects.Remove(subject)
                _subjectIdToSubject.Remove(subjectId)
            // else: has parents → re-attached by some thread → skip
        // else: not in _knownSubjects → already removed by another thread → skip
    clear s_deferredDetaches
```

Checking actual parent state (rather than tracking re-attachments) makes the design stateless and correct regardless of what other threads did during the suppression window.

### Why This Is Correct

**PropertyReferenceRemoved/Added always run immediately** on all threads, inside `lock(_knownSubjects)`. This means `RegisteredSubject.Parents` is always up-to-date at the moment of resume.

| Scenario | Resume behavior |
|----------|-----------------|
| Move (DictA → DictB) | Subject has parents (DictB added one) → skip removal |
| Genuine removal | Subject has no parents → execute full cleanup |
| Cross-thread reattach | Thread B added parents during window → skip removal |
| Cross-thread removal | Thread B already removed from `_knownSubjects` → skip (not found) |
| Same subject deferred by two threads | First resume cleans up or skips; second finds nothing → skip |

### What Is Deferred

On ContextDetach when `s_suppressRemovalCount > 0`:

- `_knownSubjects.Remove(subject)` — deferred
- `_subjectIdToSubject.Remove(subjectId)` — deferred
- Parent/child cleanup loop (iterating detaching subject's properties, removing parent refs from children, clearing children lists) — deferred

### What Still Runs Immediately

- `PropertyReferenceRemoved` — per-link parent/child removal (always correct, always immediate)
- `PropertyReferenceAdded` — per-link parent/child addition (always correct, always immediate)
- `ContextAttach` — subject registration (if subject is already in `_knownSubjects` due to deferred removal, reuses existing `RegisteredSubject`)

### Call Sites

#### 1. SubjectUpdateApplier (apply-path race)

`SubjectUpdateApplier.ApplyUpdate` wraps the entire structural processing in the scope:

```csharp
var registry = subject.Context.TryGetService<ISubjectRegistry>() as SubjectRegistry;
using (registry?.SuppressRemoval())
{
    // All structural processing (root path + remaining subjects)
}
```

By the time `ApplyUpdate` returns, the scope is disposed, deferred cleanup has run, and registry state is fully consistent with model state.

This protects against subject moves within a single update (e.g., DictA → DictB). Because `_knownSubjects` is a shared data structure, the deferred removal on the applier thread also keeps subjects visible to the CQP filter running on a background flush thread.

#### 2. Server-side CQP filter (local-mutation race)

The applier call site does NOT protect against local structural mutations on the server (or any participant). When the mutation engine directly writes structural properties (e.g., `target.Items = newItems`), the lifecycle processes the detach immediately — no `SuppressRemoval()` scope is active.

**Race sequence (observed in cycle 453 of ConnectorTester):**

1. Structural mutation thread: `target.Items = newDictionary` (without subject X) → lifecycle processes diff → X detached → X removed from `_knownSubjects`
2. Value mutation thread: `node.DecimalValue = 1517170.33` (where node == X) → lifecycle fires → CQP captures change → CQP filter calls `TryGetRegisteredProperty()` → returns null → **change dropped**
3. X may later be re-added (or not), but the value change is permanently lost

**The server-side CQP filter** (`WebSocketSubjectHandler.CreateChangeQueueProcessor`) currently drops unregistered property changes:

```csharp
propertyFilter: propertyReference =>
    propertyReference.TryGetRegisteredProperty() is { } property &&
    (_configuration.PathProvider?.IsPropertyIncluded(property) ?? true),
```

This same pattern exists in MQTT (`MqttSubjectServerBackgroundService.IsPropertyIncluded`).

**Fix:** When `TryGetRegisteredProperty()` returns null, check if the subject has a valid subject ID (proving it was previously registered). If so, the unregistration is momentary — let the value change through. Structural updates handle graph consistency; value changes on momentarily-unregistered subjects are safe to broadcast.

```csharp
propertyFilter: propertyReference =>
{
    if (propertyReference.TryGetRegisteredProperty() is { } property)
    {
        return _configuration.PathProvider?.IsPropertyIncluded(property) ?? true;
    }

    // Momentarily unregistered due to concurrent structural mutation.
    // A subject ID proves prior registration; let the value through.
    // Structural updates handle graph consistency independently.
    return propertyReference.Subject.TryGetSubjectId() is not null;
},
```

**Why this is safe:**
- A subject ID is only assigned when the subject enters the registry. If it has an ID, its properties were previously included by PathProvider.
- Value changes on momentarily-unregistered subjects are either: (a) relevant — the subject is mid-move and will be re-registered, or (b) irrelevant — the subject is genuinely removed, and the structural removal broadcast makes the value change moot.
- The PathProvider configuration does not change at runtime. A property previously included will remain includable.
- Properties on subjects that never had a context cannot reach the CQP (no interceptors fire).

**Why SuppressRemoval alone is insufficient for this race:** `SuppressRemoval()` is ThreadStatic. The structural mutation thread and the CQP flush thread are different threads. Wrapping the structural mutation in a scope would only defer removal on THAT thread. But the issue is that the structural mutation's lifecycle processing happens on the structural mutation thread (which could use `SuppressRemoval`), while the CQP filter runs on the flush thread (which cannot benefit from the structural thread's scope). However, `SuppressRemoval()` keeps subjects in the shared `_knownSubjects` map, which IS visible to the flush thread. So `SuppressRemoval()` on the structural thread WOULD prevent the drop — but only if the application wraps its structural mutations. Since this is application code (not library code), we cannot guarantee it.

The CQP filter resilience fix is defense-in-depth: it makes the CQP filter itself tolerant of momentary unregistration, regardless of whether the cause is an applier-path move or a local structural mutation. Combined with `SuppressRemoval()` on the applier (which prevents the applier's own `TryGetSubjectById` failures and parent/child inconsistency), both gaps are closed.

### Nested Scopes

The counter supports nesting. First dispose decrements to N-1 (no processing). Last dispose decrements to 0 (processes all deferred detaches). This allows composing scoped operations without premature cleanup.

### Performance Impact

- **Per-write overhead during suppression**: Zero. The only check is `s_suppressRemovalCount > 0` (ThreadStatic read, no lock needed — already inside `lock(_knownSubjects)` in `HandleLifecycleChange`).
- **Resume overhead**: One iteration over `s_deferredDetaches` inside `lock(_knownSubjects)`. For typical update sizes (10-100 subjects), negligible.
- **No suppression active**: Zero overhead. The `s_suppressRemovalCount` check is a single comparison against zero.

## Implementation Notes

### Lock ordering

All lock acquisitions follow the same order — no deadlock risk:

1. `_applyUpdateLock` (outermost, WebSocket server only)
2. `_attachedSubjects` (lifecycle, per-context)
3. `_knownSubjects` (registry, per-context)

`SuppressRemoval()` only increments a ThreadStatic counter (no lock). `ResumeRemoval()` acquires `lock(_knownSubjects)` — always the innermost lock. The CQP flush thread only reads `_knownSubjects` (via `TryGetRegisteredProperty`), never holds the outer locks.

### TryGetSubjectId() is thread-safe and stable

The CQP filter fallback depends on `TryGetSubjectId()` returning a non-null value for previously-registered subjects. This is safe because:

- Subject IDs are stored in `subject.Data` — a `ConcurrentDictionary` (lock-free reads).
- IDs are set once via `GetOrAddSubjectId()`/`SetSubjectId()` and **never cleared**. The registry only removes the reverse mapping `_subjectIdToSubject[id]`; the subject's own data is untouched.
- If this invariant is ever broken (IDs cleared on removal), the CQP filter fallback would silently fail. This must not happen.

### MQTT write handler has a secondary TryGetRegisteredProperty check

The MQTT server's `BroadcastChangesAsync` (line ~235) has its own `TryGetRegisteredProperty()` check that runs AFTER the CQP filter. Even if the CQP filter lets the change through, this secondary check drops it:

```csharp
var registeredProperty = change.Property.TryGetRegisteredProperty();
if (registeredProperty is not { CanContainSubjects: false })
    continue; // drops BOTH structural AND unregistered
```

This must be updated alongside the CQP filter. For unregistered subjects, fall back to `SubjectPropertyMetadata.Type` to determine `CanContainSubjects`. The plan's Task 5 Step 2 covers this — do not skip it.

The WebSocket server does NOT have this issue — it routes changes through `SubjectUpdateFactory` which has Fix 9's fallback.

## Known Limitations

### Structural property changes on unregistered subjects are still dropped

The `SubjectUpdateFactory` fallback (Fix 9, line 126-154) only handles **value** properties when `registeredProperty` is null. Structural properties (collections, dictionaries, object references) are skipped because serializing them requires enumerating child subject IDs via `RegisteredSubjectProperty.Children`.

This means: if a subject is momentarily unregistered AND a concurrent thread writes to its **structural** property (e.g., `X.Items = newDict`), the CQP filter lets it through (Fix 17 Part 2) but the factory silently drops it.

**Why this is acceptable:** This requires concurrent structural writes to the same subject from two independent threads, neither using `SuppressRemoval()`. In the ConnectorTester, the mutation engine uses a single thread for structural mutations per participant. The only concurrent structural source is the applier, which uses `SuppressRemoval` (keeping subjects registered). The race is near-zero probability.

**Future fix if observed:** Extend the factory fallback to handle structural properties using `SubjectPropertyMetadata` (available on `subject.Properties[name]`). `SubjectPropertyMetadata.Type` supports the same `IsSubjectDictionaryType()`/`IsSubjectCollectionType()`/`IsSubjectReferenceType()` checks, and `SubjectPropertyMetadata.GetValue` can read the current value. The harder part is recursive child processing (`ProcessSubjectComplete`) and `CompleteSubjectIds` tracking, which would need a parallel implementation using `SubjectPropertyMetadata` instead of `RegisteredSubjectProperty`.

### Double-reconnection in cycle 453 logs is benign

The cycle 453 logs show two heartbeat gap detections and two reconnections at 04:12:11-04:12:12. Investigation confirmed these are **two separate `WebSocketSubjectClientSource` instances** (client-a and client-b), not one client reconnecting twice. Each client has its own `_connectionLock`, `ExecuteAsync` loop, and property ownership on its own object graph. The simultaneous heartbeat gaps occurred because the server's sequence advanced while both clients fell behind during client-a's chaos events. Not a bug — no fix needed.

## Files To Modify

| File | Change |
|------|--------|
| `SubjectRegistry.cs` | Add ThreadStatic fields, modify `HandleLifecycleChange` detach path, add `SuppressRemoval()`/resume logic |
| `SubjectUpdateApplier.cs` | Wrap apply in `using (registry?.SuppressRemoval())` |
| `WebSocketSubjectHandler.cs` | Make server CQP filter resilient to momentarily-unregistered subjects |
| `MqttSubjectServerBackgroundService.cs` | Same CQP filter resilience fix |
| `SubjectIdTests.cs` | Unit tests for suppression/resume |
| `StableIdApplyTests.cs` | Integration test for subject move during apply |

## Documentation Updates

| File | Change |
|------|--------|
| `docs/registry.md` | Add "Deferred Subject Removal" section explaining `SuppressRemoval()` API |
| `docs/connectors-subject-updates.md` | Add section explaining deferred removal during structural apply |
| `docs/plans/fixes.md` | Close "Open Problem" and add as Fix 17 (SuppressRemoval + CQP filter resilience) |
