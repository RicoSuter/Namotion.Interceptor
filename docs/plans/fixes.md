# WebSocket Connector Fixes

Fixes discovered and applied during chaos testing with the ConnectorTester (WebSocket mode).

---

## Fix 1: Welcome sequence race condition

**Files changed:** `WebSocketSubjectHandler.cs`

**Cause:** `CreatePartialUpdateFromChanges` ran outside `_applyUpdateLock`, while Welcome snapshot creation and sequence assignment were inside it. This meant a partial update could read property data from BEFORE a Welcome snapshot was built, but then be assigned a sequence number AFTER the Welcome's sequence. The client would accept this update (sequence > Welcome sequence) and apply stale data on top of its correct Welcome state.

**Symptom:** Client has stale values after reconnection. Doesn't converge because the stale update overwrites the correct Welcome state.

**Fix:** Move both `CreatePartialUpdateFromChanges` and `Interlocked.Increment(ref _sequence)` inside `_applyUpdateLock`. This guarantees that when a Welcome reads `_sequence`, all partial updates with earlier sequences have already been fully created from their change data. Split into sync `CreateUpdateWithSequence` (holds lock) and async `BroadcastUpdateAsync` (no lock) to avoid holding the lock during I/O.

**First observed:** Cycle 34 of initial test run.

---

## Fix 2: Subject not created when properties arrive in separate batch

**Files changed:** `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`

**Cause:** When a structural update (ObjectRef/Collection/Dictionary) referenced a new subject, `ApplyObjectUpdate` only created the subject if `itemProperties is not null` — i.e., if the subject's property data was in the same update batch. When the ChangeQueueProcessor flushed the structural change and value changes in separate batches (due to timing or dedup boundaries), the subject was never created on the client. All subsequent value-only updates for that subject ID were silently dropped because `TryGetSubjectById` couldn't find it.

**Symptom:** Client has a subject with all-default values (empty string, 0, 0) while the server has real values. Same subject ID on both sides, reachable from root on both. Does not converge — the value updates permanently never reach the client.

**Fix:** Remove the `itemProperties is not null` guard — always create the subject when a structural property references it, even if properties aren't in this batch. Properties will arrive in a later update and be applied via the `TryGetSubjectById` lookup in `ApplyUpdate`'s foreach loop.

**First observed:** Cycle 25 (previous code), confirmed via `[DIAG-SUA]` stderr output showing the diverged subject ID repeatedly "not found in ID registry and not processed by structural parent."

---

## Fix 3: Subject creation ordering in update applier

**Files changed:** `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`

**Cause (original):** The old code only created subjects when `itemProperties is not null` (Fix 2). It also called `SetSubjectId` before assigning to the graph.

**Cause (regression at high rates):** Moving `SetSubjectId` to after `SetValue` (three-phase: create → assign → set ID) introduced a race with the `ChangeQueueProcessor` flush timer. Between `SetValue` (which fires change notifications) and `SetSubjectId`, the CQP flush thread could call `GetOrAddSubjectId` on the new subject, generating a client-side ID that conflicts with the server-assigned ID. At 900 structural mutations/sec this causes `InvalidOperationException: Subject already has ID 'X'; cannot reassign to 'Y'`.

**Fix (final):** Set IDs immediately after creation, BEFORE assigning to the graph. Fix 4's guard in `SetSubjectId` ensures the reverse index is only populated when the lifecycle attach handler runs (after `SetValue`). The final pattern:
1. **Create** subject + add fallback context + **set ID** (stored in Data, not in reverse index)
2. **Assign to graph** (`property.SetValue`) — lifecycle attach populates reverse index from pre-assigned ID
3. **Apply properties**

For ObjectRef: `SetSubjectId` is called after `SetValue` (single item, no CQP race window). For Collections/Dictionaries: `SetSubjectId` is called before `SetValue` (batched items, CQP flush could race).

**Status:** Applied, needs confirmation at high structural mutation rates.

---

## Tester Fix: MutationEngine null guards

**Files changed:** `MutationEngine.cs` (ConnectorTester only)

**Cause:** `VisitNode` iterated `node.Collection`, `node.Items`, and `node.ObjectRef` directly. Concurrent server updates (arriving via WebSocket) could set these properties to null between the null check and the iteration, causing `NullReferenceException`.

**Symptom:** Tester crashes with NullReferenceException in `VisitNode` during structural mutations.

**Fix:** Snapshot structural property references into local variables before null-checking and iterating.

---

## Tester Improvement: Pre-populated graph

**Files changed:** `TestNode.cs` (ConnectorTester only)

**Cause:** The initial graph had only 31 nodes (1 root + 20 collection + 10 dictionary). Structural mutations grew this to ~500 nodes over ~25 seconds. During the growth phase, every structural mutation was a net add (new node with default values), creating a unique stress pattern not representative of steady-state operation.

**Symptom:** Early cycles were disproportionately likely to fail due to the burst of structural adds, making it hard to distinguish startup-specific issues from genuine library bugs.

**Fix:** Pre-populate the graph to ~481 nodes (1 root + 30 depth-1 nodes, each with 15 leaf children). The test immediately operates at steady-state with balanced add/remove structural mutations from cycle 1.

---

## Tester Improvement: Batched mutation engine for high rates

**Files changed:** `MutationEngine.cs` (ConnectorTester only)

**Cause:** At high mutation rates (>1000/sec), `1000 / rate` gives 0ms delay, making the mutation loop a tight CPU spin. Also, `RebuildKnownNodes()` after every structural mutation became a bottleneck.

**Fix:** Batch mutations per 1ms tick (`batchSize = ceil(rate / 1000)`), always yield with `Task.Delay(1)`. Rebuild known nodes every ~10 structural mutations instead of after every one.

---

## Tester Change: Disabled console logger

**Files changed:** `Program.cs` (ConnectorTester only)

**Change:** Console logger commented out (`// b.AddConsole()`) to reduce terminal noise during long test runs. Cycle log files still capture all output.

---

## Tester Change: Increased WriteRetryQueueSize

**Files changed:** `Program.cs` (ConnectorTester only)

**Change:** Client WebSocket `WriteRetryQueueSize` increased from default 1000 to 10000 to handle 900 structural/sec without drops.

---

## Tester Change: Increased mutation rates for stress testing

**Files changed:** `appsettings.websocket.json` (ConnectorTester only)

**Current rates:** Server 5000 value + 500 structural/sec, clients 500 value + 200 structural/sec each. Total: 900 structural/sec (original was 40/sec).

---

## Active Diagnostics (to be removed before merge)

All diagnostics should be removed once investigation is complete.

| Tag | File | Output | Purpose |
|-----|------|--------|---------|
| `[DIAG] SUF drop` | `SubjectUpdateFactory.cs` | stderr | Logs when a change is dropped during update creation (unregistered subject) |
| `[DIAG] Broadcast: all changes dropped` | `WebSocketSubjectHandler.cs` | cycle log | Logs when entire flush batch produces empty update |
| `[DIAG-REG] ContextDetach` | `SubjectRegistry.cs` | stderr | Logs when `_knownSubjects.Remove` fails (subject already removed) |
| `[DIAG] Retry queue flush failed` | `SubjectSourceBackgroundService.cs` | cycle log | Logs when CQP writes go to retry queue during normal operation |
| `[DIAG] WriteChangesAsync: WebSocket not connected` | `WebSocketSubjectClientSource.cs` | cycle log | Logs when client writes fail due to closed socket |
| `PendingRetryWrites` property | `SubjectSourceBackgroundService.cs` | N/A | Exposed for future diagnostics |
| LEAK diagnostic | `VerificationEngine.cs` | cycle log | Enhanced orphaned subject detection with refCount and actual:FOUND/MISSING |

---

## Unit Test Changes

| File | Change |
|------|--------|
| `SubjectUpdateExtensionsTests.cs` | Updated `WhenApplyingUpdateWithMissingSubjectId` to expect subject creation (was: expect null) |
| `SubjectUpdateExtensionsTests.cs` | Added `WhenObjectRefPropertiesArriveInLaterBatch` test |
| `SubjectUpdateExtensionsTests.cs` | Added `WhenCollectionItemPropertiesArriveInLaterBatch` test |
| `SubjectUpdateExtensionsTests.cs` | Added `WhenObjectRefAndPropertiesInSameBatch` test |
| `ConcurrentStructuralWriteLeakTests.cs` | New file — 9 concurrency tests for lifecycle registry leak (2000 iterations each) |
| `SubjectUpdateExtensionsTests.cs` | Added `ConcurrentApplySubjectUpdateAndPropertyWrite` — concurrent apply+write (passes) |
| `SubjectUpdateExtensionsTests.cs` | Added `ConcurrentCompleteUpdateAndStructuralWrites` — reproduces no-parents leak (now passes with Fix 8) |

---

## ~~Under Investigation:~~ Likely resolved by Fix 9: Stale value / structural mismatch on client

**Symptom:** Client has stale values or missing structural elements compared to the server. Does not converge within 60 seconds.

**Previous observations:** ~1 per 35 cycles at 900/sec. Client-a-only, all-clients, and full-chaos profiles.

**Analysis of cycle 3 failure snapshot:** Server and client-b had identical subject count (474) and identical structure. Only 3 value properties on a single subject diverged — server had real values (IntValue=1057511, DecimalValue=10540.76, StringValue="001011be"), client-b had defaults (0, 0, ""). The subject was created structurally (correct ID, correct structure) but value updates never arrived.

**Status:** Likely resolved by Fix 9 (EnsureInitialized double-checked locking). The broken interceptor chain read path could return wrong values in addition to causing NREs. After Fix 9: 74+ cycles at 900/sec with zero convergence failures (previously failed ~1 per 35 cycles).

---

## Fix 4: Registry reverse ID index guard

**Files changed:** `SubjectRegistry.cs`

**Cause:** `SetSubjectId` and `GetOrAddSubjectId` unconditionally added entries to `_subjectIdToSubject`, even for subjects that were never attached via lifecycle (e.g., written to the backing store then immediately overwritten by another thread). These entries persisted forever since no detach event would fire for an unattached subject.

**Symptom:** Orphaned entries in `_subjectIdToSubject` for subjects not in `_knownSubjects`. Memory-only leak, no incorrect behavior.

**Fix:** Only populate `_subjectIdToSubject` if the subject is already in `_knownSubjects`. The lifecycle attach handler picks up pre-assigned IDs from `subject.Data` when the subject properly enters the graph.

**Status:** Applied. Belt-and-suspenders alongside Fix 5.

---

## Fix 5: Lifecycle attaches children to dead parents during concurrent detach

**Files changed:** `LifecycleInterceptor.cs`

**Cause:** When `DetachFromProperty` recursively detaches a subject's children, it reads the subject's **current backing store values** (line 224) to find children. But between `_attachedSubjects.Remove(subject)` and the backing store read, a concurrent thread can write a new child to one of the subject's structural properties. The recursive detach reads the NEW child (not the OLD one) and fails to detach it (it was never attached). Meanwhile, the concurrent thread's `WriteProperty` lifecycle processing correctly detaches the OLD child but then **attaches the NEW child to the now-dead parent** — creating an unreachable subject in `_knownSubjects`.

Detailed race:
1. Subject S has `ObjectRef = Z`. Z is attached via `(S, "ObjectRef")`.
2. Thread A (lifecycle): detaching S → `_attachedSubjects.Remove(S)`
3. Thread B (MutationEngine): `S.ObjectRef = W` → `next()` writes W to backing store
4. Thread A: reads `S.ObjectRef` → gets W (not Z!) → tries `DetachFromProperty(W)` → W not attached → no-op. **Z is never recursively detached.**
5. Thread A: removes `_lastProcessedValues[(S, "ObjectRef")]`, releases lock
6. Thread B acquires lock: `lastProcessed = Z`, backing store = W → detach Z ✓, **attach W** to `(S, "ObjectRef")` ✗
7. W is in `_knownSubjects` with parent reference to detached S. Unreachable from root. **Leak.**

**Symptom:** Subjects accumulate in `_knownSubjects` but are not reachable from root. Rate-dependent: ~1/cycle at 200 structural mutations/sec, ~10/cycle at 900/sec. Affects all structural property types (Collection, Dictionary, ObjectRef) on all participants.

**Fix:** In `WriteProperty`, before attaching new children, check if the parent subject is still in `_attachedSubjects`. If the parent was concurrently detached, skip attaching children — they would be unreachable.

**Status:** Applied. Addresses the `refCount>0` leak case (~2% of leaks at 900/sec). Does NOT address the dominant `refCount=0` leak case (~98%). See "Under Investigation" below.

---

## Fix 6: SetSubjectId race with ChangeQueueProcessor flush

**Files changed:** `SubjectItemsUpdateApplier.cs`

**Cause:** Fix 3 moved `SetSubjectId` to after `SetValue` for the three-phase pattern. But for collections/dictionaries, `SetValue` triggers change notifications. The CQP flush timer (running on a background thread) can call `GetOrAddSubjectId` on the new subject between `SetValue` and `SetSubjectId`, generating a client-side ID that conflicts with the server-assigned ID. At 900/sec this causes `InvalidOperationException: Subject already has ID 'X'; cannot reassign to 'Y'`.

**Fix:** Set IDs on new subjects immediately after creation (before `SetValue`), not after. Fix 4's guard in `SetSubjectId` ensures the reverse index is only populated by the lifecycle attach handler (after `SetValue`). The final pattern for collections/dictionaries:
1. **Create** + add fallback context + **set ID** (stored in Data only, not reverse index)
2. **Assign to graph** (`SetValue`) — lifecycle attach populates reverse index from pre-assigned ID
3. **Apply properties**

**Status:** Applied, confirmed (no more SetSubjectId crashes at 900/sec).

---

## Fix 7: Lifecycle recursive detach race — two-part fix

**Files changed:** `LifecycleInterceptor.cs`

**Cause:** Two concurrent race conditions in `DetachFromProperty` when `isLastDetach`:

**(a) Backing store read finds wrong children:** The recursive child detach read the current backing store (`metadata.GetValue?.Invoke(subject)`) to find children. A concurrent `next()` could have written a new (unattached) child to the backing store. The detach would try to detach this unattached child (no-op) while missing the actually-attached child.

**(b) Late attachments to dead parent:** Between `_attachedSubjects.Remove(subject)` and the end of recursive detach, a concurrent `WriteProperty` could attach new children to the just-detached subject's properties. These children would be in `_attachedSubjects` referencing the dead parent, but the recursive detach already finished — they'd never be cleaned up.

**Fix (two parts):**
1. Read `_lastProcessedValues` instead of the backing store for recursive child discovery. This tracks what the lifecycle has actually attached — no new allocations, faster than property getter calls.
2. After recursive detach, scan `_attachedSubjects` for subjects with a PropertyReference pointing to the just-detached subject. Detach any found. The scan runs only during `isLastDetach` and the allocation for the list only occurs when late attachments are found (rare).

**Reproducing test:** `ConcurrentStructuralWriteLeakTests.ParentDetachDuringChildPropertyWrite_OrphanedGrandchildrenLeakInRegistry` (2000 iterations × 10 rounds, previously produced ~547 orphans, now 0).

**Status:** Applied, confirmed by unit tests (5/5 concurrency tests pass at 2000 iterations).

**Status:** Applied, confirmed by unit tests (8/8 concurrency tests pass at 2000 iterations).

---

## Fix 8: Remove redundant AddFallbackContext in update applier

**Files changed:** `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`

**Cause:** `ApplyObjectUpdate` and `CreateSubjectItem` called `AddFallbackContext(parent.Context)` on newly created subjects BEFORE assigning them to the graph via `SetValue`. `AddFallbackContext` triggers `AttachSubjectToContext` (via `InterceptorExecutor`), which registers the subject in both `_knownSubjects` and `_attachedSubjects`. If the parent is concurrently detached before `SetValue`, the subject is orphaned — registered but never properly attached via a property reference.

**Fix:** Remove the explicit `AddFallbackContext` calls. `ContextInheritanceHandler` already adds fallback context automatically when a subject enters the graph via `SetValue` → lifecycle `AttachToProperty` → `HandleLifecycleChange(IsContextAttach)`. The explicit calls were redundant and created an orphan window.

**Reproducing test:** `ConcurrentCompleteUpdateAndStructuralWrites_NoOrphanedSubjectsInRegistry` — was FAILING with dozens of `refCount=0, no-parents` orphans, now passes with zero.

**Status:** Applied, confirmed by unit tests AND ConnectorTester (34 cycles at 900/sec, zero registry leaks on all participants). Also fixed NRE crash in `FindSubjectsInProperties`.

---

## ~~Under Investigation:~~ Resolved by Fix 8: Residual +1 registry leak (refCount=0, no-parents)

**Symptom:** After Fix 7, most leaks are eliminated. A residual +1 leak persists on server and client-a (not growing — stays at exactly +1 per participant). The leaked subject has `refCount=0` and `no-parents`, meaning:
- The lifecycle fully detached it (refCount decremented to 0)
- All parent references were cleaned up (Parents array empty)
- But `_knownSubjects.Remove` was never called (`HandleLifecycleChange` with `IsContextDetach` was never fired)

**Diagnostics confirmed:**
- `[DIAG-REG] ContextDetach: NOT in _knownSubjects` — never fires (Remove is never called for these subjects)
- `[DIAG-REG] RE-REGISTERING` — never fires (subjects are not re-added after removal)
- `[DIAG-LIFE] REFCOUNT MISMATCH` — never fires (refCount and set.Count are in sync)

**Observed at:** 900/sec structural mutation rate. All chaos profiles. At 900/sec, leak grows slowly (+1→+2→+4 over 10 cycles). At production rates (40/sec) it was near-zero.

**Key constraint:** `refCount=0` and `no-parents` yet still in `_knownSubjects`. The subject was registered (via `RegisterSubject`) but `HandleLifecycleChange` with `IsContextDetach=true` was never called. Likely cause: `RegisterSubject(property.Subject)` at line 127 re-registers a parent that was concurrently detached. A targeted unit test (`ParentReRegisteredAfterDetach`) could NOT reproduce the `no-parents` case — Fix 7's late-attachment cleanup catches most scenarios. The remaining leak may be specific to the WebSocket connector flow (Welcome apply, reconnection).

**Confirmed with latest code:** Leak persists at 900/sec even with all fixes applied (+1/+2 per chaos cycle, same subjects persist). Reduced from +15-20 per cycle to +1-2.

**Reproducing unit test:** `ConcurrentCompleteUpdateAndStructuralWrites_NoOrphanedSubjectsInRegistry` — exercises concurrent `ApplySubjectUpdate` (complete graph replacement, 2-level deep) with structural property writes. Reliably produces `refCount=0, no-parents` orphans.

**Root cause:** When `ApplySubjectUpdate` replaces a child (via ObjectRef update), the old child is detached. But a concurrent `WriteProperty` on the old child's structural property triggers `HandleLifecycleChange(IsPropertyReferenceAdded)` for a grandchild, which calls `RegisterSubject(child)` as a parent side-effect — re-adding the child to `_knownSubjects` after it was removed. Fix 7's late-attachment cleanup then detaches the grandchild, but the re-registered child remains orphaned.

**Resolution:** Fixed by Fix 8 (removing redundant `AddFallbackContext`). The explicit call registered subjects in `_knownSubjects` before they entered the graph, creating the orphan window.

**Unit tests:**
- `ParentReRegisteredAfterDetach_NoParentsLeakInRegistry` — validates Fix 7 prevents `has-parents` variant
- `ConcurrentApplySubjectUpdateAndPropertyWrite_NoOrphanedSubjectsInRegistry` — single-level concurrent apply+write
- `ConcurrentCompleteUpdateAndStructuralWrites_NoOrphanedSubjectsInRegistry` — reproducing test for `no-parents` leak, now passes

---

## Fix 9: Broken double-checked locking in EnsureInitialized (NRE in FindSubjectsInProperties)

**Files changed:** `InterceptorSubjectContext.cs`

**Symptom:** `NullReferenceException` in `FindSubjectsInProperties` during `AttachSubjectToContext`, called from `AddFallbackContext` during lifecycle processing. Crashes the MutationEngine BackgroundService. ~1 per 30 cycles at 900 structural mutations/sec.

**Stack trace:**
```
LifecycleInterceptor.FindSubjectsInProperties (line 426)
← LifecycleInterceptor.AttachSubjectToContext (line 39)
← InterceptorExecutor.AddFallbackContext (line 42)
← LifecycleInterceptor.InvokeAddedLifecycleHandlers (line 151)
← LifecycleInterceptor.WriteProperty (line 332)
← MutationEngine.PerformStructuralMutation
```

**Root cause:** `EnsureInitialized()` in `InterceptorSubjectContext` had a broken double-checked locking pattern:
```csharp
// BEFORE (broken):
if (_serviceCache is null)
{
    lock (_lock)
    {
        // Missing inner null check — two threads could both enter and overwrite
        _serviceCache = new ConcurrentDictionary<Type, object>();
        _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
        _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
    }
}
```
Two issues:
1. **Missing inner null check** — two threads passing the outer check simultaneously would both enter the lock and overwrite each other's dictionaries.
2. **Store ordering** — `_serviceCache` (the guard field read outside the lock) was assigned FIRST, before `_readInterceptorFunction`. The JIT/CPU could make `_serviceCache` visible to other threads while `_readInterceptorFunction` is still null. A thread checking `_serviceCache is null` → false would skip the lock and call `_readInterceptorFunction!.TryGetValue(...)` → **NRE**.

**Fix:**
```csharp
// AFTER (correct):
if (_serviceCache is null)
{
    lock (_lock)
    {
        if (_serviceCache is null)  // Inner null check
        {
            _readInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
            _writeInterceptorFunction = new ConcurrentDictionary<Type, Delegate>();
            // Guard field LAST with volatile write to ensure ordering
            Volatile.Write(ref _serviceCache, new ConcurrentDictionary<Type, object>());
        }
    }
}
```

**Diagnostic trail:**
- First diagnostic: NRE on `property=ObjectRef, subjectType=TestNode, isAttached=True` — subject is valid, not null
- Refined diagnostic: NRE confirmed in `GetValue?.Invoke(subject)` (not in `FindSubjectsInProperty`)
- `GetValue` calls the intercepted property getter → interceptor chain → `GetReadInterceptorFunction()` → `_readInterceptorFunction!.TryGetValue(...)` → NRE when field is null

**Status:** Applied. Confirmed: 74+ cycles at 900/sec with zero NRE crashes AND zero convergence failures (previously ~1 NRE per 30 cycles, ~1 convergence failure per 35 cycles).

---

## Under Investigation: HeapMB growth (~5 MB/cycle at 900/sec)

**Symptom:** HeapMB grows linearly at ~5 MB/cycle despite full GC (forced, blocking, compacting) between cycles. Registry counts match perfectly (no subject leak). Process reaches ~900 MB after ~70 cycles.

**GC dump analysis (5-minute diff):**
- +3,081 `TestNode` instances (62,977 → 66,058) — 126x more than the ~500 in the registry
- +3,015 `InterceptorExecutor` instances — one per leaked TestNode
- +6,029 `HashSet<InterceptorSubjectContext>` — `_usedByContexts` + `_fallbackContexts` per executor
- +4,110 `ConcurrentDictionary<Type,Delegate>` — `_readInterceptorFunction`/`_writeInterceptorFunction` per executor
- +175,650 `System.Object` — boxed values, lock objects

**Root cause hypothesis:** Child InterceptorExecutors are added to parent context's `_usedByContexts` via `AddFallbackContext`, but `RemoveFallbackContext` is not called for all detached subjects. The parent context (root) holds references to leaked child contexts, preventing GC.

**Investigation approach:** Added `AddFallbackContext`/`RemoveFallbackContext` counters to `InterceptorSubjectContext.GetFallbackStats()`. Need to confirm delta grows over time to verify the add/remove imbalance.

**Status:** Open. Registry-level leak is fixed (Fixes 4-8), but `_usedByContexts` leak persists. Needs heap dump comparison with `dotnet-gcdump` after fresh start to identify exact retention path.
