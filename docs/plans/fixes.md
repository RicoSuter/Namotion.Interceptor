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

**Status:** Applied. Confirmed at 900 structural mutations/sec (58+ cycles).

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

## Active Diagnostics

All `[DIAG]` stderr diagnostics have been removed. The following permanent logging remains:

| Log | File | Level | Purpose |
|-----|------|-------|---------|
| Retry queue optimistic re-apply summary | `SubjectSourceBackgroundService.cs` | Info/Warn | Logs count of re-applied and dropped changes on reconnection |
| Retry queue per-change drop | `SubjectSourceBackgroundService.cs` | Warning | Logs each dropped property name with subject type |
| Orphaned subject detection | `VerificationEngine.cs` (tester) | Warning | Enhanced orphaned subject detection with refCount and actual:FOUND/MISSING |
| Property diffs with timestamps | `VerificationEngine.cs` (tester) | Error | On failure: logs diverged properties with write timestamps from both participants |
| Re-sync check | `VerificationEngine.cs` (tester) | Warn/Error | On failure: applies server's complete update to diverged client, reports if transient or structural |

---

## ~~Stale value / structural mismatch on client~~ Resolved by Fix 9 + Fix 10

**Symptom:** Client has stale values or missing structural elements compared to the server. Does not converge within 60 seconds.

**Resolution:** Two separate root causes, fixed independently:
- **Value drops** (Pattern A): Fixed by Fix 9 (SUF fallback for momentarily-unregistered subjects)
- **Structural divergence** (Pattern B): Fixed by Fix 10 (optimistic retry queue re-apply)
- **Interceptor chain NRE**: Fixed by EnsureInitialized double-checked locking (#227, merged to master)

**Status:** Resolved. 48+ cycles at 900/sec full-chaos with zero convergence failures.

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

**Status:** Applied. Addresses the `refCount>0` leak case. The dominant `refCount=0` leak case was resolved by Fix 8.

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

## ~~Under Investigation:~~ Resolved: HeapMB growth (~5 MB/cycle at 900/sec)

**Symptom:** HeapMB grows linearly at ~5 MB/cycle despite full GC (forced, blocking, compacting) between cycles. Registry counts match perfectly (no subject leak).

**Root cause:** `_lastProcessedValues` in `LifecycleInterceptor.WriteProperty` was updated unconditionally (line 391), even when `parentStillAttached = false`. When a concurrent detach removed the parent from `_attachedSubjects`, the `_lastProcessedValues` entries for that parent were already cleaned up. Writing a new entry created a dangling reference keyed by a `PropertyReference` to the dead parent. This entry was never removed — no future detach would clean it up. The value (collection/dictionary) held references to child subjects, preventing GC.

**Fix:** Guard `_lastProcessedValues` update with the same `parentStillAttached` check used for the attach guard. When the parent is dead, skip the update — the entry would be dangling. On re-attach, `WriteProperty` falls back to `context.CurrentValue` (the normal initial-write path).

**Confirmed:** 42 cycles at 900/sec with stable HeapMB (23-31 MB range, no growth trend). Previously grew ~5 MB/cycle linearly.

---

## Fix 9: Value updates dropped for momentarily-unregistered subjects

**Files changed:** `SubjectUpdateFactory.cs`, `ChangeQueueProcessor.cs`

**Cause:** In `SubjectUpdateFactory.ProcessPropertyChange`, when `TryGetRegisteredProperty()` returns null (subject momentarily unregistered due to concurrent structural mutation), the value change was silently dropped. The subject's structural update goes through (it's on the parent's property, parent is registered), so the client creates the subject with defaults. But the value updates are lost and no new change notifications are generated.

**Fix (two parts):**
1. `SubjectUpdateFactory`: When `registeredProperty` is null but the subject has a valid ID and the property is a value type (not structural), include the change as a simple value update using the subject's own property metadata. Structural properties still require the full registry metadata and are skipped.
2. `ChangeQueueProcessor`: Refactored `propertyFilter` from `Func<RegisteredSubjectProperty, bool>` to `Func<PropertyReference, bool>`, pushing the null-property decision to each caller. Each consumer now explicitly decides policy for unregistered properties:
   - `SubjectSourceBackgroundService` (client sources): lets unregistered properties through → SUF fallback handles them
   - WebSocket/MQTT/OPC UA servers: returns false → drops them (server doesn't need SUF fallback; clients get correct state on next Welcome)

**Reproducing tests:** `DetachedSubjectUpdateDropTests` — 2 deterministic tests.

**Status:** Applied. Validated at 900/sec with chaos testing.

---

## Fix 10: Structural divergence from stale retry queue writes

**Files changed:** `WriteRetryQueue.cs`, `SubjectPropertyWriter.cs`, `SubjectSourceBackgroundService.cs`

**Cause:** On reconnection, `SubjectPropertyWriter.LoadInitialStateAndResumeAsync` flushed the retry queue directly to the server BEFORE applying the initial state (Welcome). These stale changes (made against the client's pre-disconnection state) could corrupt the server's graph when the server had newer state.

**Fix:** Optimistic concurrency with local re-apply. Instead of flushing stale changes to the server, the retry queue is drained after initial state loads and CQP is created. Each queued change is compared locally: if `currentValue == oldValue` (source hasn't changed the property), the change is re-applied locally and flows through CQP to the server as a fresh write. If the values differ, the change is dropped (source wins).

**Design:** See [Retry Queue Optimistic Concurrency Design](2026-03-21-retry-queue-optimistic-concurrency-design.md).

**Key changes:**
- `WriteRetryQueue`: Added `DrainForLocalReapply()` method
- `SubjectPropertyWriter`: Removed `_flushRetryQueueAsync` callback, simplified to 2-arg constructor
- `SubjectSourceBackgroundService`: Added `ReapplyRetryQueue()` between CQP creation and `ProcessAsync`, with per-change Warning logging for dropped changes

**Status:** Applied. Validated 48+ cycles at 900/sec full-chaos with zero convergence failures (previously failed at cycle 5-50).

---

## Fix 11: Unregistered subject in graph — parentStillAttached guard too aggressive

**Files changed:** `LifecycleInterceptor.cs`

**Symptom:** A subject exists in the graph (reachable via dictionary) with a valid subject ID, but has **zero registered properties**. All property values are null/default. Does not converge — even applying a complete update from the server (re-sync check) cannot fix it. ~1 per 66 cycles at 900/sec full-chaos.

**Observed:** Cycle 66. Subject `7aS7PvciN889iOLGCG4fld` in a Dictionary on parent `06qVolpnidSH7AhiK9liJA.Items`. Server has all 6 properties populated. Client-b has the subject with empty properties `{}`. Re-sync check: "still diverged after applying server's complete update → structural/applier bug."

**Root cause:** The `parentStillAttached` guard in `LifecycleInterceptor.WriteProperty` (added for the HeapMB fix) prevented both child attachment AND `_lastProcessedValues` update when the parent was concurrently being detached. This broke the invariant **reachable → registered**: the child CLR object was in the dictionary (reachable) but never registered in the SubjectRegistry. The `ApplyPropertyUpdate` method silently skips unregistered subjects, making them permanently broken — no future update (partial or complete) could fix them.

**Discarded approach — `parentStillAttached` guard:**

The previous fix (HeapMB growth) added a `parentStillAttached` guard that skipped both `AttachToProperty` and `_lastProcessedValues` update when the parent was concurrently detached. This prevented the HeapMB leak but broke the reachable → registered invariant. **Do not re-introduce this guard** — it trades a memory leak for permanently broken subjects, which is worse (broken subjects can never self-heal; leaks are bounded by graph size).

**Fix (three parts):**

1. **Remove the `parentStillAttached` guard entirely.** Children are always attached and `_lastProcessedValues` is always updated. This restores the invariant: if a subject is reachable from the graph, it is registered.

2. **Add parent-dead check after attachment.** If the parent was concurrently detached (removed from `_attachedSubjects` between `next()` and lock acquisition), immediately detach the just-attached children and remove the `_lastProcessedValues` entry. This prevents the HeapMB leak that the original guard was fixing.

3. **Defense-in-depth: late-entry cleanup in DetachFromProperty.** After recursive child detach, re-check `_lastProcessedValues` for entries added by concurrent writes during the detach. Clean up any found. This catches edge cases where the parent-dead check in WriteProperty couldn't run (e.g., the detach completed before the write's lock acquisition).

**Performance note:** This approach does more work than the `parentStillAttached` guard in the concurrent-detach case: it attaches children, updates `_lastProcessedValues`, then immediately detaches and cleans up. The guard would have skipped all of this. However, the concurrent-detach case is rare (only happens when a structural write races with a parent detach). In the normal case (no concurrent detach), the parent-dead check is a single `_attachedSubjects.ContainsKey` lookup — negligible. The correctness guarantee (reachable == registered) is worth the extra work in the rare concurrent case.

**Concurrent path analysis:**
- Thread A attaches, then Thread B detaches: B's recursive cleanup finds children via `_lastProcessedValues` and detaches them. ✓
- Thread B detaches, then Thread A attaches: A's parent-dead check detects dead parent, immediately detaches children and cleans `_lastProcessedValues`. ✓
- Normal case (no concurrent detach): Children attached, parent alive, no cleanup needed. ✓
- All operations are under `lock (_attachedSubjects)` — no interleaving within the locked section. ✓

**Reproducing test:** `ConcurrentDictWriteDuringParentDetach_AllReachableSubjectsAreRegistered` — verifies both directions: reachable → registered AND not reachable → not registered.

**Status:** Applied. All 10 concurrency tests pass. Awaiting chaos tester validation.
