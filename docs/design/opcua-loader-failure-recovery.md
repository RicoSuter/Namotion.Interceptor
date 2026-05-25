# OPC UA Loader: Atomic Root Attach for Failure Recovery

## Goal

When the OPC UA loader's discovery phase fails (transient service exception, browse error, cancellation), the root subject's visible state must be identical to its pre-load state. This lets the existing `SubjectSourceBase` retry policy recover from a clean slate.

## Problem

The current loader (PR #313) mutates the root subject and claims source ownership live during discovery. If discovery fails midway, the catch block in `OpcUaSubjectClientSource.StartListeningAsync:174` disposes the session manager but does not release ownership claims or remove dynamic properties already attached to root. On the base class's retry attempt the loader re-encounters its own partial state: `AddProperty` collides with existing entries, `ClaimSource` returns false for already-owned properties, and the load fails permanently. The customer sees a half-built subject graph with phantom properties that read defaults forever and silently drop writes.

## Behavior comparison

### Current loader

```
T0  Root { Motor: null, Pump: null, Sensor: null }    _ownership: {}

T1  Discovery loads Motor live on root
    Root { Motor: Motor{Speed=50,Status="ok"},        _ownership:
           Pump: null, Sensor: null }                   { Root.Motor, Motor.Speed,
                                                         Motor.Status }

T2  Discovery loads Pump partially (live on root)
    Root { Motor: Motor{...},                         _ownership: +Root.Pump,
           Pump: Pump{Pressure=2.1, Capacity=??},      +Pump.Pressure
           Sensor: null }

T3  EXCEPTION on Sensor browse
    Catch in StartListeningAsync: CleanupSessionManagerAsync (session only).
    Re-throw. State at T2 frozen. Ownership retained.

T4  Base class retries StartListeningAsync after RetryTime
    Reset() clears NodeIdKey property data only. Ownership intact.
    LoadSubjectAsync re-runs:
      AddProperty(Motor) collides.
      ClaimSource(Motor.Speed) already owned, returns false.
    Retry hits the same partial state every cycle. Permanent failure.
```

### Option D (this design)

```
T0  Root { Motor: null, Pump: null, Sensor: null }    All per-load lists empty.

T1-T2  Discovery creates staged orphans, queues root ops + claims.
       Root state UNCHANGED.                          Orphans in heap
                                                       (unreachable from root):
                                                        stagedMotor, stagedPump
                                                       _pendingRootOps: queued
                                                       _pendingClaims: queued
                                                       _stagedSubjects: tracked

T3  EXCEPTION on Sensor browse (inside LoadSubjectAsync)
    Catch in loader: walk _stagedSubjects, RemoveFallbackContext on each.
    Clear lists. Re-throw.
    Outer catch in StartListeningAsync: dispose session manager. Re-throw.

T4  Base class retries StartListeningAsync after RetryTime.
    Root identical to T0. No ownership claims exist. No dynamic properties
    on root. Clean attempt. Succeeds.

Apply phase (synchronous tail of a successful LoadSubjectAsync):
    foreach claim in _pendingClaims: ClaimSource(claim)
    foreach op in _pendingRootOps: op()
    Single coherent burst from root observers' perspective.
```

## Why staged subjects need explicit detach

During discovery the loader constructs each staged subject without a constructor context and then explicitly calls `subject.Context.AddFallbackContext(rootContext)` so the subject can resolve services (interceptors, lifecycle, source ownership manager) through root.

`AddFallbackContext` is bidirectional (`InterceptorSubjectContext.cs:92`):

```csharp
if (_fallbackContexts.Add(contextImpl))
{
    lock (UsedByContextsLock) { contextImpl._usedByContexts.Add(this); }
```

The second line writes a hard reference into the target context's `_usedByContexts` set. Since the root context lives for the application lifetime, the staged subject's context (and the subject itself) is reachable from a long-lived root and cannot be garbage collected. Per-load failures would leak.

Symmetric `RemoveFallbackContext` (`InterceptorSubjectContext.cs:117-131`) clears both sides of the link. After cleanup, staged subjects have no incoming references from any long-lived structure and become GC-eligible. Inter-staged-subject fallback links form a closed reference cycle, which .NET's GC handles natively, so they need no explicit teardown.

Constructing without a constructor context (rather than relying on the source generator's auto-add of the constructor argument as a fallback at `SubjectCodeGenerator.cs:246`) keeps the add and the undo strictly symmetric: the loader owns the only fallback link to root and is responsible for removing it.

## Design

### Per-load tracking state

Three lists owned by the loader, scoped to one `LoadSubjectAsync` call:

| List | Purpose | Failure handling |
|---|---|---|
| `_pendingRootOps : List<Action>` | Root mutations to replay on apply | Discarded on failure |
| `_pendingClaims : List<PropertyReference>` | Source-ownership claims to make on apply | Never made on failure |
| `_stagedSubjects : List<IInterceptorSubject>` | Subjects created during discovery | Iterated on failure for `RemoveFallbackContext` |

### Mutation rules during discovery

| Mutation | Where applied |
|---|---|
| Create staged subject without constructor context, then explicit `subject.Context.AddFallbackContext(rootContext)` | Live. Register in `_stagedSubjects` immediately. Avoids relying on the source generator's auto-add behavior; the explicit call is what we will explicitly undo on failure. |
| `AddProperty` on a staged child | Live. Initializers need to fire so NodeMapper rounds see populated metadata. |
| `AddProperty` on root | Queued in `_pendingRootOps`. |
| `SetValueFromSource(child)` on a staged parent | Live. |
| `SetValueFromSource(stagedChild)` on root | Queued in `_pendingRootOps`. |
| `SetValueFromSource(primitive)` on a staged child | Live. |
| `SetPropertyData(NodeIdKey, nodeId)` on any subject | Live. Deferring breaks NodeMapper round dependencies. |
| `ClaimSource(property)` | Queued in `_pendingClaims`. Never claimed during discovery. |

### Apply phase

Synchronous tail of `LoadSubjectAsync`. No awaits between operations.

```csharp
foreach (var property in _pendingClaims)
{
    _ownership.ClaimSource(property);
}

foreach (var op in _pendingRootOps)
{
    op();
}
```

**Ordering rationale.** Claims are made before root attachments. At the instant a root observer sees a child appear, the child's leaf properties are already source-owned. An observer that writes to such a property immediately triggers the OPC UA outbound writer correctly. A reversed order would leave a microsecond gap where writes would be silently lost (no source claim equals no outbound dispatch).

### Failure path

Catch block at the outer scope of `LoadSubjectAsync`:

```csharp
catch
{
    foreach (var staged in _stagedSubjects)
    {
        staged.Context.RemoveFallbackContext(rootContext);
    }
    _stagedSubjects.Clear();
    _pendingClaims.Clear();
    _pendingRootOps.Clear();
    throw;
}
```

Inter-staged-subject fallback links added during nested loading (e.g., the `OpcUaSubjectLoader.cs:361` pattern that attaches child context to parent context) form closed cycles. They do not anchor to root and do not need explicit teardown.

## Code changes

### `OpcUaSubjectLoader.cs`

- Add the three tracking lists to `OpcUaLoadContext` (which is already per-load).
- Replace every direct root mutation with `_pendingRootOps.Add(() => ...)`.
- Replace every `_ownership.ClaimSource(p)` with `_pendingClaims.Add(p)`.
- Register every newly constructed subject in `_stagedSubjects` immediately after construction.
- Wrap the five-phase discovery pipeline in `try { discovery; apply(); } catch { cleanup(); throw; }`.

### `OpcUaSubjectClientSource.cs`

No behavioral changes. The catch block at line 169 still disposes the session manager and re-throws; loader-internal state cleanup is now the loader's responsibility.

### `OpcUaSessionExtensions.cs`

No changes. Continues to throw `OpcUaTransientServiceException` on per-NodeId transient bad status. This is now safe because the loader handles its partial state correctly.

## What this design does not do

- It does not suppress discovery-phase change events on staged subjects. They fire on the shared `PropertyChangeObservable` and are visible to context-level subscribers. This matches existing loader behavior (line 361 already creates child subjects with shared observable access via fallback). Filing as Issue #320 follow-up if a downstream consumer surfaces an actual problem.
- It does not address Issue #2 (root not in `SubjectsByNodeId`). Independent.
- It does not address Issue #4 (config validation gaps). Independent.
- It does not require Issue #3 (loader idempotency). Retries always start from a clean slate, so per-call idempotency is unnecessary.

## Test strategy

New tests in `Namotion.Interceptor.OpcUa.Tests`:

1. `WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState`. Inject a fault during browse round two. Assert root's properties match pre-load snapshot. Assert no ownership claims exist on root or any subject.

2. `WhenLoadFailsAndRetries_ThenSecondAttemptSucceedsCleanly`. Inject one-shot fault. Assert first call throws, second call succeeds. After second call, root has the full subject graph.

3. `WhenLoadFails_ThenStagedSubjectsAreGarbageCollectable`. Capture weak references to staged subjects collected before failure. Force GC after failure. Assert weak references are dead.

4. `WhenLoadSucceeds_ThenApplyPhaseExecutesAtomically`. From root observer's perspective, all root mutations occur in a single contiguous run with no awaitable interleaving.

5. `WhenApplyExecutes_ThenClaimsCompleteBeforeAttachments`. Subscribe to property changes on root. Inside the subscription, synchronously write to the newly attached child's property. Assert the write reaches the outbound writer (proves claim was in place at attach time).

Tests follow existing conventions. `When<Condition>_Then<Behavior>` naming. Arrange, Act, Assert comments. No `Task.Delay`. Use `AsyncTestHelpers.WaitUntilAsync` or `ManualResetEventSlim` for synchronization.

## Out of scope

- Atomic discovery-phase observability (full Issue #320 ambition). Would require buffering or suppressing change emissions during discovery. Deferred.
- Loader idempotency (Issue #3). Rendered unnecessary by clean-slate retries.

## Implementation notes (after merge)

### Partial root deferral instead of full deferral

The original design table queued `AddProperty` on root for the apply phase. The shipped implementation defers only `SetValueFromSource` calls on root (and all source-ownership claims). Dynamic property structure (the slot itself) is added live during discovery.

**What this means in practice**:

- On a successful load: identical observable behavior. All values appear in the apply burst.
- On a failed load: the dynamic property slot exists on root with a null value, no source claim, and no orphan subjects. The next retry finds the slot by name, treats it as an existing property, and fills it cleanly. No accumulating pollution across retries.

**Why partial instead of full**: full deferral would require either a new `RegisteredSubject.RemoveProperty` API (breaking the IInterceptorSubject contract) or a shadow root subject during discovery (with complications around static-property lookup via NodeMapper). The partial approach solves the customer-visible bugs (orphan subjects, claim leaks, half-loaded sub-graphs) without those changes.

**Trade-off documented for observers**: code that subscribes to root's `Properties` enumeration would see transient empty slots during a failed-then-retried load. Code that subscribes to value changes (the normal pattern) sees only successful loads.

### Parent context, not root context

`OpcUaLoadContext.RegisterStagedSubject` takes the immediate parent context as a parameter, not the root context. Each staged subject's fallback is added against its direct parent, mirroring exactly what `ContextInheritanceHandler` does when a subject is normally attached via property change. On rollback we remove the same parent context that was added.

This keeps the symmetry: when the staged subject is later attached via apply-phase `SetValueFromSource`, the inheritance handler tries to add the parent context as fallback (deduped no-op via the underlying HashSet). When the subject is eventually detached normally, the handler removes the parent context cleanly. Our manual add and the lifecycle handler are interchangeable.

### Registry cleanup via fallback lifecycle

`InterceptorExecutor.AddFallbackContext` queries the new fallback context for `ILifecycleInterceptor` services and calls `AttachSubjectToContext` on each. This automatically registers the staged subject in the registry during discovery. The symmetric `RemoveFallbackContext` calls `DetachSubjectFromContext`, which fires `IsContextDetach` and unregisters from the registry. No manual registry manipulation needed in the loader or the load context.

### GC test removed

The `WhenLoadFails_ThenStagedSubjectsAreGarbageCollectable` test was dropped in favor of the registry orphan check (`WhenLoadFails_ThenRegistryKnownSubjectsContainsNoOrphans`). Since the registry holds strong references to staged subjects via `KnownSubjects`, asserting that no orphans remain in the registry is a stronger and more practical guarantee than weak-reference-based GC eligibility (which depends on collection timing).
