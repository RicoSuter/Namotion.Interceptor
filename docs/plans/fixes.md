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

---

## ~~Under Investigation:~~ Resolved by Fix 12 + Fix 13: Transient delivery gap — state not propagated

**Symptom:** A subject has stale or default property values on one participant while others have correct values. Sequences match perfectly (all broadcasts delivered). Re-sync check fixes it ("transient delivery gap"). ~1 per 20-60 cycles at 900/sec full-chaos.

**Observed patterns:**
- Cycle 40 (pre-fix): Server IntValue=0 "written never", both clients IntValue=12823140. Value property divergence.
- Cycle 30 (post-Fix 12, pre-Fix 13): Server has newer values, client-b has older values across ALL properties (DecimalValue, Items, ObjectRef, StringValue). All "written never" on client-b.
- Cycle 7/10 (earlier): Server empty structural props, clients populated. Structural property divergence.

**Key finding: sequences always match.** All broadcasts were delivered. The issue is in the **apply path**, not delivery.

**Root cause (two separate races in `SubjectUpdateApplier`):**

Both races occur when `ApplySubjectUpdate` runs concurrently with local structural mutations (e.g., mutation engine modifying the graph while a server broadcast or client update is being applied).

**Race 1 — `TryGetSubjectById` fails, applier creates hollow instance (Fix 12):**
When a structural property references a subject by ID, the applier calls `TryGetSubjectById`. If a concurrent mutation detaches the subject (removing it from `_subjectIdToSubject`), the lookup fails. The applier then creates a NEW CLR instance with default values for the same ID — permanently destroying the original instance's state. Root cause: the protocol didn't distinguish "new subject with complete state" from "reference to existing subject." See Fix 12.

**Race 2 — `TryGetRegisteredProperty` fails, property update silently skipped (Fix 13):**
For each property in the update, the applier calls `TryGetRegisteredProperty` which checks the `SubjectRegistry`. If a concurrent mutation detaches the subject (removing it from `_knownSubjects`), the lookup returns null and the property update is silently skipped. The subject keeps its old values. Root cause: the applier depended on the mutable `SubjectRegistry` for property metadata, when the subject's own `Properties` dictionary (compile-time generated, always available) provides the same information. See Fix 13.

**Diagnostics that confirmed the root cause:**
- `[DIAG-CQP-DROP]`: 1133 events per run — CQP filter drops for unregistered subjects (self-healing via parent structural changes)
- `[DIAG-APPLY-UNREG]`: 411 events per run — structural property updates skipped (NOT self-healing, caused permanent divergence)
- Diverged subject `5dK7oMv0is5e8HHvembxZd` (cycle 40): zero CQP-DROP, zero APPLY-UNREG for this subject — the VALUE property drops were completely silent (DIAG only logged structural drops)
- Diverged subject `0x7ZASWoI09L4hOprdn0CN` (cycle 30): ALL properties diverged with "written never" — entire `ApplyPropertyUpdates` loop was skipped

**Discarded approaches:**
- Long-lived CQP subscription: The Welcome always sends complete state after restart, making the subscription gap irrelevant. Failed at cycle 7 with the fix in place.
- Registry-level deferred cleanup (`_subjectIdToSubject`): Would require deferred removal to avoid race, but leads to memory leaks when no more lifecycle events fire. The protocol-level fix (Fix 12) is cleaner.
- Serialization (lock structural mutations during apply): Would fix the race but requires cooperation from all mutation code. The applier-level fixes are self-contained.

**Resolution:** Fixed by Fix 12 (protocol-level `CompleteSubjectIds`) + Fix 13 (registry-independent `PropertyAccessor` fallback). Together these eliminate both race windows: the applier never creates hollow instances and never silently skips property updates. Awaiting chaos tester validation.

---

## Fix 12: Protocol ambiguity — applier creates hollow subjects for unknown IDs

**Files changed:** `SubjectUpdate.cs`, `SubjectUpdateBuilder.cs`, `SubjectUpdateFactory.cs`, `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`, `SubjectUpdateApplyContext.cs`, `connectors-subject-updates.md`

**Cause:** When a structural property (Collection/Dictionary/ObjectRef) referenced a subject by ID, and `TryGetSubjectById` failed (concurrent mutation detached the subject), the applier unconditionally created a new CLR instance with default values. This destroyed the original subject's state and propagated corrupted defaults to other participants via CQP.

The protocol didn't distinguish between:
- "Here's a **new** subject with complete state — create it if you don't have it"
- "Here's a **reference** to an existing subject — you should already have it"

**Fix:** Added `CompleteSubjectIds` field to `SubjectUpdate` (protocol change). The factory populates this set with subject IDs that went through `ProcessSubjectComplete`. The applier checks this before creating new instances:
- ID in `CompleteSubjectIds` (or set is null for complete updates) → safe to create
- ID NOT in set → skip the item (don't fabricate defaults, self-heals on next update)

**Protocol details:** `completeSubjectIds` is a nullable JSON array of subject ID strings. `null` means all subjects are complete (backward compatible with complete/initial-state updates). Non-null means only listed IDs have complete state. See [Subject Updates documentation](../connectors-subject-updates.md).

**Status:** Applied. All tests pass. Awaiting chaos tester validation.

---

## Fix 13: Applier silently skips property updates for momentarily-unregistered subjects

**Files changed:** `SubjectUpdateApplier.cs`, `SubjectItemsUpdateApplier.cs`

**Cause:** `ApplyPropertyUpdate` called `TryGetRegisteredProperty` which checks the `SubjectRegistry` (`_knownSubjects`). When a concurrent structural mutation detached the subject, the lookup returned null and the property update was silently skipped. This affected ALL property types (value, structural) and could skip the entire `ApplyPropertyUpdates` loop if the subject was unregistered at loop start.

The fundamental issue: the applier depended on the mutable `SubjectRegistry` for property metadata (get/set delegates, type info), when the subject's own `Properties` dictionary — compile-time generated and always available regardless of registry state — provides the same information.

**Fix:** Introduced `PropertyAccessor` readonly struct that abstracts over `RegisteredSubjectProperty` (normal path) and `SubjectPropertyMetadata` (fallback). When `TryGetRegisteredProperty` returns null, the applier falls back to `subject.Properties[propertyName]`. This ensures property updates are **never silently skipped** — the subject always knows its own properties.

**Correctness guarantee:** Once `TryGetSubjectById` returns a subject CLR instance, all property updates succeed via the `PropertyAccessor` regardless of concurrent registry changes. The only case where updates are skipped is when `TryGetSubjectById` itself fails (subject not in ID registry) — which is handled by Fix 12's `CompleteSubjectIds`.

**Performance note:** The `PropertyAccessor` struct is stack-allocated (no heap allocation). The fallback path through `SubjectPropertyMetadata` uses the same generated get/set delegates as the normal path. The only difference is that `TransformValueBeforeApply` is skipped when the registered property is unavailable (acceptable — it's only used for path-based property mapping, and a momentarily-unregistered subject won't have path info anyway).

**Status:** Applied. All tests pass. Awaiting chaos tester validation.

---

## Fix 14: Pre-resolve subject references before applying updates

**Files changed:** `SubjectUpdateApplyContext.cs`, `SubjectUpdateApplier.cs`, `SubjectRegistry.cs`, `SubjectIdTests.cs`, `StableIdApplyTests.cs`

**Cause:** `ApplyUpdate`'s Step 2 loop iterates `update.Subjects` and looks up each subject by ID via `ISubjectIdRegistry.TryGetSubjectById`. When Step 1's structural processing (root path) detaches a subject from the registry (e.g., ObjectRef set to null, collection replaced), Step 2 can no longer find that subject — its property value updates are permanently lost.

This is a **same-thread sequencing issue**, not a concurrency race: Step 1 runs first and modifies the registry, then Step 2 iterates remaining subjects against the now-modified registry.

**Fix:** Before processing any updates, iterate `update.Subjects.Keys` and resolve each via `TryGetSubjectById`, caching results in `SubjectUpdateApplyContext._preResolvedSubjects`. Step 2 uses `context.TryResolveSubject()` which checks the cache first, then falls back to the live registry (for subjects created during the apply).

**Registry change:** Reverted the deferred `_pendingIdCleanup` queue in `SubjectRegistry` back to eager removal. The deferred cleanup was a global registry behavioral change for what is an `ApplyUpdate`-scoped problem. Pre-resolution is scoped to the apply operation with no side effects on the registry.

**Test:** `ApplyUpdate_WhenStructuralChangeDetachesSubject_ThenStep2PropertyUpdatesStillApplied` — verifies that a value update is applied even when the same update's structural change detaches the subject.

**Status:** Applied. Improved chaos tester from ~7 cycles to ~45 cycles before failure (remaining failures had different root cause — see Fix 15).

---

## Fix 15: Backing store race in structural property apply (Dictionary and ObjectRef)

**Files changed:** `SubjectItemsUpdateApplier.cs`, `SubjectUpdateApplier.cs`

**Cause:** `ApplyDictionaryUpdate` and `ApplyObjectUpdate` read the current property value from the backing store (`metadata.GetValue?.Invoke(parent)`) to find existing CLR instances. This read happens **without the lifecycle lock**. If a concurrent mutation thread's `WriteProperty.next()` has written a different value to the backing store (before acquiring the lifecycle lock), the apply reads the concurrent mutation's value instead of what the lifecycle actually processed.

**Dictionary race sequence:**
1. Client dict = `{A, B, C, D}` — B has applied values
2. Concurrent mutation: `next()` writes `{A, C, D}` to backing store (no lock yet)
3. Apply thread: reads backing store → sees `{A, C, D}` (B missing!)
4. Server update: dict items = `[A, B, C, D, E]` — B's properties not in update (B didn't change)
5. Apply processes B as "new key" → `ResolveOrCreateSubject` → if B not in registry (concurrent detach) and not complete (partial update) → **B skipped**
6. B eventually re-added by subsequent update as fresh instance with **default values**

**ObjectRef race sequence:** Same pattern — concurrent mutation writes different subject to ObjectRef backing store, apply reads it, `isSameSubject` check fails, falls through to registry lookup which may also fail.

**Fix:** Removed all backing store reads from `ApplyDictionaryUpdate` and `ApplyObjectUpdate`. Both now resolve subjects exclusively from the ID registry, matching `ApplyCollectionUpdate`'s existing pattern:

| Structural type | Before | After |
|----------------|--------|-------|
| Collection | Registry-only (3-phase) | Unchanged |
| Dictionary | Backing store + registry fallback | **Registry-only (3-phase)** |
| ObjectRef | Backing store + registry fallback | **Registry-only** |

The 3-phase pattern (resolve → assign to graph → apply properties) avoids the backing store race entirely. The lifecycle interceptor diffs against `_lastProcessedValues` (lock-protected, always consistent) rather than the backing store.

**Performance:** No meaningful regression. Structural properties only appear in updates when the structure actually changed (partial updates contain only changed properties). The registry lookup cost is comparable to the backing store read it replaces. For complete updates (Welcome on reconnect), the extra SetValue calls on unchanged properties are noops (lifecycle detects `ReferenceEquals` on `_lastProcessedValues`).

**Status:** Applied. Chaos tester reached 190+ cycles with no convergence failures. Extended validation passed.

---

## Under Investigation: Slow heap growth (~0.12 MB/cycle)

**Symptom:** HeapMB grows linearly from ~28 MB to ~51 MB over 190 cycles despite forced full GC (Gen2, compacting, with finalizer wait) between cycles. No orphaned subjects detected (registry/reachable always match). Growth rate is consistent (~0.12 MB/cycle) with no sign of plateau.

**GC dump comparison (cycle 172 vs 180):**

| Growth | Type | Notes |
|--------|------|-------|
| +676 | `RuntimeParameterInfo` | .NET reflection cache — may be one-time warm-up |
| +288 | `System.Byte[]` | Buffer pools (ArrayPool, RecyclableMemoryStream) |
| +286 | `ParameterInfo[]` | Same root as RuntimeParameterInfo |
| +71 | `BufferSegment` (IO.Pipelines) | Kestrel/WebSocket pipe segments |
| +52 | `ValueTuple<IInterceptorSubject, PropertyReference, Object>[]` | LifecycleInterceptor `_listPool` (ThreadStatic, never shrinks) |
| +31 | `Entry<IInterceptorSubject>[]` | LifecycleInterceptor `_subjectHashSetPool` (ThreadStatic, never shrinks) |

**Suspected sources:**
- `DefaultSubjectFactory.CreateSubjectCollection` and `CreateSubjectDictionary` call `MakeGenericType` on every invocation — should be cached by runtime but worth verifying
- `ActivatorUtilities.CreateInstance` / `Activator.CreateInstance` reflection metadata
- ThreadStatic pools in LifecycleInterceptor grow capacity but never shrink
- Dictionary internal arrays (`_lastProcessedValues`, `_knownSubjects`, `_subjectIdToSubject`) keep high-water-mark capacity after Remove

**GC dump comparison (4 dumps, cycles 172→180→186→190):**

| Type | D1 | D2 | D3 | D4 | Verdict |
|------|-----|-----|-----|-----|---------|
| `RuntimeParameterInfo` | 311 | 987 | 987 | 987 | Stabilized (one-time warm-up) |
| `ParameterInfo[]` | 171 | 457 | 457 | 457 | Stabilized |
| `SubjectPropertyChild[]` | 401 | 581 | 859 | 894 | **Growing** — registry child lists |
| `BufferSegment` | 59 | 130 | 80 | ~80 | Fluctuates (GC collects) |
| Heap bytes | 54.7M | 60.5M | 51.1M | 65.2M | Fluctuates (not monotonic) |

**Prime suspect: `SubjectPropertyChild[]`** — backing arrays for `List<SubjectPropertyChild>` in `RegisteredSubjectProperty._children`. These lists grow via `Add` and shrink via `RemoveAt` but never trim capacity. The count of arrays is growing (401→894 over 18 cycles) which suggests either:
1. `RegisteredSubjectProperty` instances accumulating (not being GC'd when subjects are removed)
2. Or `List<T>` capacity growth causing array churn that GC hasn't collected yet

**Mitigation applied:** Cached `MakeGenericType` results in `DefaultSubjectFactory` to avoid repeated reflection metadata creation.

**Root cause found:** `RegisteredSubject._parents` retains stale references to parent `RegisteredSubjectProperty` instances. When a parent is detached before its child, `RemoveParent` is skipped (parent not in `_knownSubjects`), leaving the child's `_parents` list holding a reference to the old parent's `RegisteredSubjectProperty` (and its `_children` backing array). This prevents GC of the parent's registry metadata.

**Fix:** Call `registeredSubject.ClearParents()` on `IsContextDetach` before removing from `_knownSubjects`. At this point the subject has zero valid parents (last reference removed), so any remaining entries are stale. Thread-safe: all under `lock(_knownSubjects)`, same lock order as `AddParent`/`RemoveParent`.

**Status:** Applied (via PR #233). Tester validated: `SubjectPropertyChild[]` count now fluctuates (980→815→850→728) instead of monotonically growing. `Action<SubjectLifecycleChange>` delegate leak also fixed (duplicate `SubjectAttached` subscription on WebSocket client reconnection: `-=` before `+=`).

---

## Fix 16: Phase 1 property application for new subjects

**Files changed:** `SubjectItemsUpdateApplier.cs`, `SubjectUpdateApplier.cs`, `StableIdApplyTests.cs`

**Cause:** The 3-phase apply pattern (Phase 1: create subjects, Phase 2: SetValue to root, Phase 3: apply properties) has a gap between Phase 2 and Phase 3. A concurrent structural mutation on the same thread can read the backing store after Phase 2, obtaining CLR instances with default values (properties not yet applied). If the mutation's dict overwrites the apply's dict and then the subject is later re-created, it gets fresh defaults permanently.

**Race sequence:**
1. Apply creates new subject X (Phase 1), assigns dict with X to graph (Phase 2)
2. Concurrent mutation reads `target.Items` → gets dict WITH X → X has default values
3. Phase 3 applies `X.DecimalValue = 2116803.35` → but mutation already captured X with defaults
4. Mutation rebuilds dict → X has values (same CLR instance) → OK in this case
5. BUT if mutation read BEFORE Phase 2, X is not in mutation's dict at all → X lost → later re-created with defaults

**Fix:** For NEW subjects only (created without context, no interceptors), apply properties in Phase 1 before SetValue. Since new subjects have no interceptors, `SetValue` delegates are direct field writes — no lifecycle fires, no parent-dead check, no CQP captures. The full subgraph (including nested structural properties) is built entirely in memory before entering the graph.

For EXISTING subjects (found in registry, have context + interceptors), properties are still applied in Phase 3 after rooting — the lifecycle must fire correctly for these.

**Why this is safe:**
- New subjects created by `Activator.CreateInstance` have no context and no interceptors
- Property writes are direct field assignments — no side effects
- `TryMarkAsProcessed` prevents double-application (Phase 1 marks, Phase 3 skips)
- On Phase 2 SetValue, lifecycle discovers the fully-populated subgraph from backing store values
- Recursive subgraphs work: nested new subjects are also context-less, build entirely in memory

**Tests:** `ApplyUpdate_NewDictItem_HasPropertiesAppliedBeforeGraphEntry`, `ApplyUpdate_NewCollectionItem_HasPropertiesAppliedBeforeGraphEntry`, `ApplyUpdate_NewObjectRef_HasPropertiesAppliedBeforeGraphEntry`, `ApplyUpdate_NewDictItemWithNestedObjectRef_FullSubgraphPopulated`

**Status:** Applied. All tests pass. Awaiting chaos tester validation.

---

## Fix 17: Deferred subject removal and CQP filter resilience

**Files changed:** `SubjectRegistry.cs`, `SubjectUpdateApplier.cs`, `WebSocketSubjectHandler.cs`, `MqttSubjectServerBackgroundService.cs`

**Symptom:** When a subject is momentarily unregistered during a concurrent structural mutation, the CQP filter drops value changes for that subject → permanent divergence. Two distinct race windows:

1. **Apply-path race:** Subject moves between structural properties within the same update (e.g., DictA → DictB). The applier processes properties sequentially; if the source is processed first, the subject is fully detached before re-attachment.
2. **Local-mutation race:** Server (or any participant) performs a local structural mutation that temporarily unregisters a subject while a concurrent value write is in flight. The CQP flush thread checks `TryGetRegisteredProperty()`, gets null, drops the value change.

**Observed:** Cycle 453 of ConnectorTester. `4DyQWHvyVckYvtxP494DTy.DecimalValue` — server wrote 1517170.33, client-a had 0 ("written never"). Sequences matched. Re-sync fixed it (transient delivery gap). Root cause: server CQP filter dropped the value because the subject was momentarily unregistered during one of ~127K structural mutations.

**Fix (two parts):**

1. **`SuppressRemoval()` on `SubjectRegistry`:** `[ThreadStatic]` counter and deferred detach set. During suppression, `ContextDetach` cleanup is deferred — subjects stay in `_knownSubjects` and `_subjectIdToSubject`. On scope dispose, only genuinely orphaned subjects (no parents) are removed. `SubjectUpdateApplier.ApplyUpdate` wraps all structural processing in this scope. Because `_knownSubjects` is shared, the deferred removal on the applier thread also keeps subjects visible to the CQP flush thread.

2. **Resilient server CQP filters:** When `TryGetRegisteredProperty()` returns null, fall back to checking the subject's ID (`TryGetSubjectId()`). A subject ID proves prior registration — let the value change through. This closes the local-mutation race where structural mutations go through the lifecycle directly (not the applier) and no `SuppressRemoval()` scope is active.

**Design:** See [Deferred Subject Removal design](../design/deferred-subject-removal.md) for full rationale, thread model, and correctness analysis.

**Implementation plan:** See [2026-03-25-deferred-subject-removal.md](2026-03-25-deferred-subject-removal.md) for step-by-step tasks.

**Supersedes:** Potential Fix A (deferred ID removal — stashed, narrower scope) and Potential Fix B (lifecycle scope — more complex, not needed for this specific race).

**Known limitation:** The `SubjectUpdateFactory` fallback (Fix 9) only recovers VALUE property changes for unregistered subjects. Structural property changes are still dropped because the factory needs `RegisteredSubjectProperty` to enumerate child subject IDs. This requires concurrent structural-on-structural writes to the same subject from independent threads — near-zero probability with the current mutation engine (single structural thread per participant). See design doc "Known Limitations" for details and future fix approach.

**Scope reduced after review:** CQP filter resilience (Part 2) was removed. The subject ID fallback would bypass PathProvider for momentarily-unregistered subjects, potentially leaking internal properties. The cycle 453 failure was a genuinely removed subject — the CQP filter correctly dropped the value change (the client also removes the subject via the structural update, so the dropped value is moot). Only Part 1 (SuppressRemoval on applier) is applied, which prevents temporary unregistration during subject moves within a single update.

**Status:** Part 1 REVERTED (disabled in applier). Caused a new regression — see below.

### Regression: SuppressRemoval causes re-sync failure (cycle 6, no-chaos)

**Observed:** Cycle 6 of ConnectorTester (profile: `no-chaos`, zero chaos events). Failed with `structural/applier bug` — re-sync could NOT fix client-a even after applying the server's complete state.

**Symptoms:**
- Subject `3SLcJRErhWK1fyqvaTOgM3`: Items (empty vs populated dict), ObjectRef (different subject ID), StringValue — all "written never" on client-a
- 3 subjects entirely missing from client-a
- 13 registry leaks on client-a: `refCount=0 parents=[Collection[N]@...(actual:FOUND)]` — subjects reachable from graph but registry says orphaned
- Re-sync check: "still diverged after applying server's complete update → structural/applier bug"

**Root cause:** `SuppressRemoval` only defers `SubjectRegistry._knownSubjects` removal. The `LifecycleInterceptor._attachedSubjects` is a **separate tracking structure** that is NOT suppressed — subjects are removed from it immediately during detach. This desynchronizes the two maps:

1. Applier (SuppressRemoval active): detaches subject X from a structural property
   - `_attachedSubjects.Remove(X)` — **immediate** (lifecycle's own tracking)
   - `HandleLifecycleChange(IsContextDetach)` — **deferred** (X stays in `_knownSubjects`)

2. During the window between detach and re-attach, the mutation engine's structural thread writes to X's structural property (e.g., `X.Items = newDict`). The lifecycle's `WriteProperty` runs. `next()` updates the backing store. Then the parent-dead check fires:
   ```csharp
   if (!_attachedSubjects.ContainsKey(context.Property.Subject))  // X not in _attachedSubjects!
   {
       _lastProcessedValues.Remove(context.Property);
       // detach newly attached children → children NOT registered
       return;
   }
   ```
   The lifecycle rolls back its tracking. Children from the structural mutation are NOT attached. Backing store has the new value, but the lifecycle/registry don't track the children.

3. These untracked children become the `refCount=0, actual:FOUND` leaks — in the backing store (reachable) but invisible to the lifecycle and registry.

4. When the re-sync later applies the server's complete state, `CreateSnapshot` calls `TryGetRegisteredSubject()` for untracked subjects → null → creates empty property entries. Snapshot diverges from server.

**Why this didn't happen before SuppressRemoval:** Without SuppressRemoval, `IsContextDetach` fires immediately. The subject is removed from BOTH `_knownSubjects` and `_attachedSubjects` at the same time. No desynchronization. The parent-dead check still fires for concurrent mutations, but those mutations write to genuinely-detached subjects (no longer in the graph), so the rollback is correct.

**Fix direction:** Replaced by Fix 18 (lifecycle batch scope).

---

## Fix 18: Lifecycle batch scope (replaces SuppressRemoval)

**Files changed:** `LifecycleInterceptor.cs`, `ContextInheritanceHandler.cs`, `SubjectUpdateApplier.cs`, `SubjectUpdateApplyContext.cs`, `SubjectRegistry.cs`

**Cause:** SuppressRemoval (Fix 17 Part 1) deferred registry cleanup but left lifecycle removal immediate. This desynchronized `_attachedSubjects` and `_knownSubjects` — the parent-dead check fired for concurrent mutations on mid-move subjects, rolling back lifecycle processing and creating registry leaks (`refCount=0, actual:FOUND`). Failed at cycle 6 with no chaos.

**Fix:** Lifecycle batch scope on `LifecycleInterceptor`. When `isLastDetach` occurs during a batch, the subject stays in `_attachedSubjects` with an empty reference set. No child cleanup, no `_lastProcessedValues` removal, no `ContextDetach`. On scope dispose, subjects whose set is still empty are genuinely orphaned — execute full detach. Subjects re-attached during the batch are silently skipped.

Both `_attachedSubjects` and `_knownSubjects` stay synchronized at all times. The parent-dead check never fires for mid-move subjects. The CQP filter succeeds because the subject never leaves `_knownSubjects`.

Two additional changes required for correctness:
1. **ContextInheritanceHandler** condition changed from `ReferenceCount: 0` to `IsContextDetach: true` — prevents the `RemoveFallbackContext → DetachSubjectFromContext` chain from bypassing the batch scope. In the non-batch case, these conditions are equivalent.
2. **EndBatchScope** uses the parent's context (`deferredProperty.Subject.Context`) for handler resolution and child detach, matching what `DetachFromProperty` does in the non-batch path. Required because `ContextInheritanceHandler` removes the subject's fallback context during the handler loop.

**Replaces:** SuppressRemoval on SubjectRegistry (removed). PreResolveSubjects retained — needed for concurrent-mutation races where the mutation engine removes a subject from the live registry on a different thread while the applier is processing.

**Design:** See [lifecycle-batch-scope-design.md](2026-03-27-lifecycle-batch-scope-design.md) for full rationale.

**Status:** Applied. All unit tests pass (including concurrent orphan stress tests). ConnectorTester: 97/98 cycles passed (zero leaks, registry/reachable perfectly in sync). 1 failure at cycle 98 — transient delivery gap, re-sync fixed. See Fix 19 investigation.

---

## Fix 19: Server CQP filter drops during concurrent structural mutations

**Files changed:** `WebSocketSubjectHandler.cs`, `ChangeQueueProcessor.cs`, `SubjectUpdateFactory.cs`

**Cause:** Two drop points where the server's CQP silently discards property changes when a subject is momentarily unregistered from `_knownSubjects` due to a concurrent structural mutation on the mutation engine thread:

1. **CQP subscription filter** (`WebSocketSubjectHandler`): `TryGetRegisteredProperty()` returns null → change rejected. Observed: 63-70 drops per failing cycle.
2. **Update factory** (`SubjectUpdateFactory.ProcessPropertyChange`): `TryGetRegisteredProperty()` returns null → structural properties (Collection/Dictionary/ObjectRef) silently dropped because the original code assumed they "require full registry metadata to serialize correctly."

**Why single-retry deferred filter wasn't enough:** The structural mutation can keep the subject unregistered for longer than one flush cycle (8ms). Diagnostic logs showed 70 `deferred-DROPPED` and 0 `deferred-accepted` — the subject was still unregistered at flush time.

**Fix (two parts):**

1. **Server CQP filter** (`WebSocketSubjectHandler`): Accept changes when subject is unregistered (`TryGetRegisteredProperty() is not { } property`). Only reject when subject IS registered and PathProvider explicitly excludes the property. This is a temporary relaxation — properties that PathProvider would exclude could leak during the unregistration window. TODO: add PathProvider-aware filtering that doesn't drop unregistered subjects.

2. **Update factory structural fallback** (`SubjectUpdateFactory.ProcessPropertyChange`): Extended the existing value-property fallback (line 128+) to also handle structural properties. Uses `SubjectPropertyMetadata.Type` (from `subject.Properties`) to determine property kind (Dictionary/Collection/ObjectRef), and `change.GetNewValue<T>()` / `change.GetOldValue<T>()` for serialization — same data the normal path uses. No registry metadata needed. Preserves flush order (no deferral).

**Note on client-side filter:** The client CQP filter (`SubjectSourceBackgroundService`) uses `TryGetSource(out var source) && source == _source` to prevent echo loops. Relaxing this (accepting unclaimed properties) caused catastrophic echo loops (960 missing subjects, cycle 1 failure). The client filter must stay strict. Client-side drops (23 per cycle) are for properties genuinely not owned by that source — harmless.

**Status:** Applied. Server CQP filter relaxed, update factory structural fallback added. ConnectorTester: 77/77 cycles passed before next failure (up from 12-23 previously). TODO: revisit server filter to restore PathProvider checks without dropping unregistered subjects.

---

## Fix 20: Applier retry for out-of-order subject creation

**Files changed:** `SubjectUpdateApplier.cs`

**Cause:** The CQP batches changes every 8ms. When a client creates a new subject and writes values to it, the value update can arrive at the server BEFORE the structural update that creates the subject — if the CQP flush timer fires between the two writes:

1. Client mutation engine writes `parent.Collection = [..., newSubject]` (structural)
2. CQP flush fires → structural change sent in batch N
3. Client mutation engine writes `newSubject.DecimalValue = 67721.12` (value)
4. CQP flush fires → value change sent in batch N+1
5. Server receives batch N+1 first (or batch N hasn't been processed yet)
6. `TryResolveSubject(newSubjectId)` → NOT FOUND → value skipped
7. Server receives batch N → creates subject with default values

Or within the same batch: if the value change for the new subject is iterated before the structural change that creates it, `TryResolveSubject` fails.

**Fix:** In `SubjectUpdateApplier.ApplyUpdate`, the Step 2 loop now buffers subjects that can't be resolved on the first pass. After all known subjects are processed (including structural creates), the buffered subjects are retried. By then, the structural processing has created them.

```csharp
// First pass: process known subjects, buffer unknown
List<...>? deferred = null;
foreach (var (subjectId, properties) in update.Subjects)
{
    if (context.TryResolveSubject(subjectId, out var target))
        ApplyPropertyUpdates(target, properties, context);
    else
        deferred.Add((subjectId, properties));
}

// Retry: structural processing above may have created them
if (deferred is not null)
    foreach (var (subjectId, properties) in deferred)
        if (context.SubjectIdRegistry.TryGetSubjectById(subjectId, out var target))
            ApplyPropertyUpdates(target, properties, context);
```

This handles the within-same-batch case. The cross-batch case (value in batch N, structural in batch N+1) requires the structural update's `ProcessSubjectComplete` to include current values — which it already does by reading the backing store at serialization time.

**Status:** Applied. ConnectorTester: 77 cycles passed (up from 20 previously). Remaining failure at cycle 77 is a different issue — re-sync failure (structural/applier bug in collection apply).

---

## Fix 21: Heartbeat state hash for guaranteed convergence

**Files changed:** `HeartbeatPayload.cs`, `StateHashComputer.cs` (new), `WebSocketSubjectHandler.cs`, `WebSocketSubjectClientSource.cs`

**Problem:** The CQP pipeline is at-most-once delivery. Values lost during chaos (CQP batching splits, WebSocket send failures, receiver can't find subject) are permanently lost. No amount of CQP-level fixes can guarantee zero loss — there will always be another edge case. The system needs a mechanism to DETECT and RECOVER from ANY divergence, regardless of cause.

**Fix:** Structural state hash in heartbeats with automatic reconciliation.

1. **Server** computes a SHA256 hash of the graph's **structural** state on each heartbeat. Walks registered subjects and hashes subject IDs + structural property content (collection items, dictionary entries, object references) in sorted order. Value properties are NOT hashed — structural divergence is the critical issue that doesn't self-heal.
2. **HeartbeatPayload** extended with optional `StateHash` field.
3. **Client** receives heartbeat, computes its own structural hash, compares with server's.
4. **On mismatch**, client triggers reconnection → receives Welcome (complete state including values) → converges. Value divergence is fixed as a side effect.

This is the WebSocket connector's reliability mechanism, analogous to:
- OPC UA's subscription monitoring with republish
- MQTT's QoS=AtLeastOnce with retained messages

**Properties:**
- Guaranteed correct: ANY structural divergence is detected, regardless of cause
- Self-healing: reconnection + Welcome provides authoritative state (structure + values)
- Secure: hash doesn't expose values, Welcome uses PathProvider
- Maximum staleness: one heartbeat interval (configurable, tester uses 10s)
- Lightweight: only hashes subject IDs and structural relationships, not value properties

**ConnectorTester results:** 67/67 cycles passed before failure (up from 12-37 without hash). The remaining failure (cycle 67) is a re-sync issue in the tester's manual `ApplySubjectUpdate` call — concurrent apply from verification thread while WebSocket receive loop processes partial updates. Not a production issue (production re-syncs via WebSocket Welcome, not manual apply).

**Future improvements:**
- Replace full reconnect with lightweight SyncRequest message (re-apply Welcome without dropping WebSocket connection)
- Incremental hash (XOR of structural hashes, updated on lifecycle events) to avoid graph walk per heartbeat
- Configurable hash interval separate from heartbeat interval
- Skip hash comparison during active mutation phase (hash mismatches during mutations are expected)

---

## Fix 22: Apply lock + consistent hash computation

**Files changed:** `SubjectUpdateExtensions.cs`, `WebSocketSubjectClientSource.cs`

**Cause:** Concurrent `ApplySubjectUpdate` calls from different threads (e.g., WebSocket receive loop applying partial updates + tester re-sync applying complete state, or heartbeat-triggered reconnection + in-flight partial update) can leave subjects in an inconsistent state — in the graph (reachable from a collection) but not in the registry (`TryGetRegisteredSubject` returns null). The snapshot then shows `null` properties for these subjects. Re-sync also fails because it's another concurrent apply.

**Fix:** Per-subject apply lock stored in `subject.Data`. All `ApplySubjectUpdate` calls acquire this lock, serializing concurrent applies to the same root subject. The heartbeat hash computation also runs under this lock, guaranteeing it sees a consistent snapshot (no mid-apply state).

**Status:** Applied. ConnectorTester: 322+ cycles, zero failures.

---

## Summary of all changes in this session

### Fix 18: Lifecycle batch scope (core fix)
**Files:** `LifecycleInterceptor.cs`, `ContextInheritanceHandler.cs`, `SubjectUpdateApplier.cs`, `SubjectUpdateApplyContext.cs`, `SubjectRegistry.cs`, `BatchScopeTests.cs` (new), `SuppressRemovalTests.cs` (deleted)

Defers `isLastDetach` processing during `SubjectUpdateApplier.ApplyUpdate`. Subjects moving between properties within the same update stay in `_attachedSubjects` and `_knownSubjects` throughout. Replaces the failed SuppressRemoval mechanism. Additional changes: ContextInheritanceHandler condition (`IsContextDetach: true`), EndBatchScope uses root context, `CreateBatchScope` requires root context parameter.

### Fix 19: Server CQP filter + structural fallback
**Files:** `WebSocketSubjectHandler.cs`, `ChangeQueueProcessor.cs`, `SubjectUpdateFactory.cs`

Server CQP filter accepts unregistered properties (relaxed `TryGetRegisteredProperty` check). `ProcessPropertyChange` extended to handle structural properties (Collection/Dictionary/ObjectRef) when subject is unregistered — uses `SubjectPropertyMetadata.Type` and change old/new values instead of registry metadata. Note: deferred filter infrastructure was explored during investigation but removed as dead code (superseded by the filter relaxation).

### Fix 20: Applier retry for out-of-order subjects
**Files:** `SubjectUpdateApplier.cs`

Step 2 loop buffers unresolvable subjects and retries after structural processing. Handles within-batch ordering where value changes for new subjects precede the structural change that creates them.

### Fix 21: Heartbeat structural hash
**Files:** `HeartbeatPayload.cs`, `StateHashComputer.cs` (new, later replaced), `WebSocketSubjectHandler.cs`, `WebSocketSubjectClientSource.cs`, `Program.cs` (tester heartbeat config)

Structural state hash in heartbeats detects and auto-heals any divergence via reconnection + Welcome. **Superseded by Fix 24 (sent-state hash):** live graph hash replaced by sent-state hash to eliminate false positives during concurrent structural mutations. `StateHashComputer.cs` removed.

### Fix 22: Apply lock + consistent hash
**Files:** `SubjectUpdateExtensions.cs`, `WebSocketSubjectClientSource.cs`

Per-subject apply lock (`subject.Data`) serializes concurrent `ApplySubjectUpdate` calls (e.g., WebSocket receive loop + tester re-sync, or heartbeat-triggered reconnection + partial update). Hash computation runs under the same lock to guarantee a consistent snapshot — no mid-apply state visible.

### Fix 23: CQP filter cache + hash-in-update
**Files:** `WebSocketSubjectHandler.cs`, `UpdatePayload.cs`, `WebSocketSubjectClientSource.cs`

CQP filter caches PathProvider decisions in `subject.Data` — no PathProvider bypass during momentary unregistration. Structural hash embedded in every broadcast via `UpdatePayload.StructuralHash`. Client compares after each apply. Heartbeat only fires during idle periods (no broadcasts for 10s). **Hash source changed in Fix 24** from live graph (`StateHashComputer`) to sent-state model (`SentStructuralState`). Design: [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md).

---

## Fix 24: Sent-state structural hash

**Files changed:** `SentStructuralState.cs` (new), `WebSocketSubjectHandler.cs`, `WebSocketSubjectClientSource.cs`, `WebSocketClientConnection.cs`, `StateHashComputer.cs` (deleted)

**Cause:** Fix 23's per-update hash comparison used `StateHashComputer` to walk the live graph for the hash. The live graph always contains unflushed mutations from the mutation engine — structural mutations that CQP hasn't flushed yet. The `_applyUpdateLock` blocks other CQP flushes but not the mutation engine. The hash captured state not in the update, causing false-positive mismatches (~120 per cycle in ConnectorTester at 500+ structural mutations/sec).

**Fix:** Replaced live graph hash with per-connection sent-state hash. `SentStructuralState` maintains a lightweight dictionary tracking structural state implied by sent/received `SubjectUpdate` content. SHA256 hash computed from this dictionary. On the server, each `WebSocketClientConnection` owns its own `SentStructuralState`, initialized from the Welcome sent to that client and updated from each broadcast under `_applyUpdateLock`. The client maintains its own instance, initialized from the received Welcome and updated from each received update. Hash is computed and serialized per-connection — each client gets its own hash in the broadcast. The hash is consistent by construction — no false positives regardless of concurrent mutation rate.

**Why per-connection:** A single global `_sentState` on the server gets re-initialized on each new client Welcome, breaking sent-state tracking for other connected clients. Even "initialize once" fails because the Welcome includes unflushed CQP mutations that the cumulative sent state doesn't have, creating false-positive mismatches and cascading reconnections.

**Thread safety:** All `SentStructuralState` access on the server — both `UpdateSentState` (in `CreateUpdateWithSequence`, broadcast path) and `ComputeSentStateHash` (in `BroadcastHeartbeatAsync`, heartbeat path) — happens under `_applyUpdateLock`. No unsynchronized access. The hash is pre-computed under the lock and passed as a dictionary of per-connection hashes to `BroadcastUpdateAsync`, which runs outside the lock. This ensures hash consistency without holding the lock during I/O.

**Eager PathProvider caching:** The CQP filter (Fix 23) was enhanced with eager caching — on the first registered encounter for any property, ALL sibling properties are cached via PathProvider. This eliminates the "never seen while registered" cache miss window. The "no cache → drop" default is now safe because all known properties are eagerly cached before any unregistered access can occur. No security bypass is possible.

**Per-instance cache prefix:** The filter cache uses a per-instance prefix (`_filterCachePrefix` = GUID) to isolate cache entries between server instances. Multiple `WebSocketSubjectHandler` instances sharing the same subject graph have independent caches, preventing stale cache hits or cross-instance interference.

**What was removed:** `StateHashComputer.cs` (106 lines of live graph walking). Client no longer needs apply lock for hash computation. Server no longer walks the graph under `_applyUpdateLock`.

**What was added:** `SentStructuralState.cs` — ~150 lines, self-contained, no dependency on registry/interceptors/core library. Pure WebSocket-internal concern.

**ConnectorTester validation:** 42+ cycles with 114K+ structural mutations/cycle and all chaos profiles — zero hash mismatches, zero failures. The structural hash mechanism is confirmed consistent (no false positives). Hash mismatches never fire because chaos events cause clean TCP disconnects — reconnection is driven by connection loss, not hash detection. The hash is defense-in-depth for silent pipeline corruption that doesn't drop the connection.

**ConnectorTester bugs found during validation:**
- Structural mutation task silently crashed on first exception (missing catch in `MutationEngine.RunStructuralMutationsAsync`) — resulted in 0 structural mutations per cycle. Fixed with catch + warning log.
- `appsettings.websocket.json` was never loaded — `--launch-profile websocket` required (sets `DOTNET_ENVIRONMENT=websocket` for environment-specific config loading). Config had structural mutation rates but defaults in `ConnectorTesterConfiguration` had `StructuralMutationRate=0`.
- CQP filter dropped value changes on brand-new subjects that had no cached PathProvider decision (cache miss → drop). Caused permanent value loss for subjects created by structural mutations. Fixed with eager caching — first registered encounter caches ALL sibling properties.
- VerificationEngine now fails the cycle if any "Structural hash mismatch" warnings are found in the cycle log, preventing pipeline bugs from being silently masked by auto-healing reconnection.

**Design:** See [registry-independent pipeline design](2026-04-06-registry-independent-pipeline-design.md), Layer 2: Per-Connection Sent-State Structural Hash.

---

## ~~Fix 25b: CQP subscription gap during reconnection~~ REVERTED

**Files changed:** `SubjectSourceBackgroundService.cs`, `SubjectSourceBackgroundServiceTests.cs`

**Cause (hypothesized):** In `SubjectSourceBackgroundService.ExecuteAsync`, the `ChangeQueueProcessor` subscription was created AFTER the Welcome state was applied. This left a nanosecond race window where a concurrent local mutation could fire between Welcome application and CQP creation.

**Attempted fix:** Move CQP creation before `LoadInitialStateAndResumeAsync` + drain accumulated changes after Welcome to discard stale pre-Welcome mutations.

**Why it was reverted:** Two fundamental problems:
1. **Without drain:** Pre-Welcome stale mutations (with stale old values) were sent to the server, corrupting server state with pre-Welcome values that the Welcome had already overwrote on the client.
2. **With drain:** The drain couldn't distinguish pre-Welcome from post-Welcome changes. Legitimate post-Welcome mutations that fired between Welcome apply and drain were discarded — the client's graph changed but the server was never notified. This caused a regression from ~22 cycles to 2-5 cycles.

**Resolution:** Reverted to original CQP-after-Welcome order. The nanosecond gap between Welcome and CQP creation is acceptable — any delivery gaps from this window are now caught by Fix 28 (registry divergence detection).

**Test change retained:** `WhenStartingSourceAndPushingChanges_ThenUpdatesAreInCorrectOrder` — added `PropertyChangeQueue` to mock context (CQP constructor needs it regardless of ordering).

**Key insight discovered during investigation:** The source-generated code lazily creates an `InterceptorExecutor` on first `Context` access (`_context ??= new InterceptorExecutor(this)`). This means `ContextInheritanceHandler` → `AddFallbackContext` → `AttachSubjectToContext` ALREADY fires for ALL child subjects when they enter the graph, providing eager seeding of `_lastProcessedValues` for their structural properties. The `_lastProcessedValues` "lazy discovery" issue was a red herring — the seeding already happens. The `DiscoverChildrenIfNeeded` approach was abandoned as redundant and harmful (double-seeding interfered with concurrent WriteProperty processing).

**Diagnostic finding:** `BUG-UNREGISTERED` diagnostic (added to WriteProperty to check if any subject in the processed value was NOT in `_attachedSubjects`) showed **zero** hits in 10 cycles — confirming the lifecycle processes correctly. The remaining failures are delivery gaps, not lifecycle races.

---

## Fix 26: Snapshot returns empty properties for momentarily-unregistered subjects

**Files changed:** `SubjectUpdateFactory.cs`

**Cause:** `ProcessSubjectComplete` checks `TryGetRegisteredSubject()`. When a concurrent structural mutation adds a subject to the backing store but the lifecycle interceptor hasn't registered it yet, the lookup returns null. The old code created an empty properties entry — the snapshot included the subject but with null/missing properties. This made re-sync fail: the applier skips properties not present in the update, so the client keeps its stale values.

**Symptom:** Re-sync check: "still diverged after applying server's complete update → structural/applier bug." Mismatch shows server=null for all properties of one subject, client has default values. Subject counts are perfectly aligned (structural graph correct), only VALUE properties diverge.

**Fix:** Added `ProcessSubjectFromMetadata` fallback method. When `TryGetRegisteredSubject()` returns null, uses `subject.Properties` (compile-time generated, always available) to iterate and serialize each property. Checks property types via `IsSubjectDictionaryType()` / `IsSubjectCollectionType()` / `IsSubjectReferenceType()` extension methods. Structural properties are recursively traversed via existing `SubjectItemsUpdateFactory` methods. Processor filtering and attribute processing are skipped (require registry info).

**First observed:** Cycle 28 of ConnectorTester (post-Fix 25).

**Status:** Applied. All 391 Connectors.Tests pass.

---

## Fix 27: CQP unflushed changes lost on connection error

**Files changed:** `ChangeQueueProcessor.cs`

**Cause:** When a connection error exits the CQP's `ProcessAsync` dequeue loop, the periodic flush task is cancelled. Any changes in the CQP's `ConcurrentQueue` that were enqueued after the last periodic flush are permanently lost — the CQP is about to be disposed, discarding the queue contents. The `WriteRetryQueue` only receives changes that the `writeHandler` failed to send, not changes that were never flushed.

**Symptom:** Value property divergence (server has default value, clients have mutated value). Re-sync converges. Occurs when a chaos event kills the connection right after a mutation was captured by the CQP but before the next 8ms flush timer fires.

**Fix:** Added a final `TryFlushAsync` call in `ProcessAsync`'s finally block, after the periodic flush task exits. Uses the original `cancellationToken` (not the linked token, which is already cancelled). If the flush fails (connection is dead), `SubjectSourceBackgroundService.WriteChangesAsync` routes the failure to the `WriteRetryQueue`, which is re-applied on the next reconnection via `ReapplyRetryQueue`.

**First observed:** Cycle 9 of ConnectorTester.

**Status:** Applied.

**Performance optimization:** Added dirty flag + cached hash to `SentStructuralState` — value-only updates skip SHA256 recomputation entirely. Removed LINQ allocations from `UpdateFromBroadcast` and `BuildStructuralContent` (manual loops instead of `.Any()`, `.Except()`, `.Where().OrderBy().ToList()`). All under `_applyUpdateLock`, so reducing lock duration matters.

**Status:** Applied.

---

## Fix 25: CQP filter cache populated at SubjectAttached time

**Files changed:** `WebSocketSubjectHandler.cs`

**Cause:** The CQP filter cached PathProvider decisions per-instance on the first filter call while the subject was registered ("eager caching"). But brand-new subjects created by structural mutations could be unregistered before any of their properties went through the CQP filter — the cache was empty, the filter dropped the change. 31 drops per 60-second cycle at 500 structural mutations/sec. Confirmed by diagnostic logging: `"CQP filter drop: TestNode.IntValue (subjectId=..., unregistered, no cache entry)"`.

**Fix:** Subscribe to `SubjectAttached` in the `WebSocketSubjectHandler` constructor. When a subject is attached to the graph, eagerly cache PathProvider decisions for ALL its properties in `subject.Data`. The lifecycle guarantees `SubjectAttached` fires after `SubjectRegistry` registers the subject (so `TryGetRegisteredSubject()` succeeds) and before any value changes on the subject reach the CQP (attachment is synchronous inside the interceptor chain, CQP flush is async on a timer). This eliminates the cache-miss window entirely.

The previous "eager sibling caching" inside the filter was removed — it was redundant with the `SubjectAttached` hook and added complexity. The filter now has three paths:
1. Cache hit (fast path, lock-free ConcurrentDictionary read)
2. Cache miss + registered (fallback for subjects that existed before the handler)
3. Cache miss + unregistered (diagnostic log, drop)

**First observed:** Cycle 58 (before fix) and cycle 3 (with diagnostic logging).

**Validation:** After fix, zero CQP filter drops. Tester reached 20 cycles before hitting a different issue (see below).

**Status:** Applied.

---

## Under Investigation: Value divergence despite zero CQP filter drops (cycle 20)

**Observed:** Cycle 20, profile `full-chaos`. Subject `2lo4OtvEbm8xqMekZwgxsJ` — server has newer values, client-a has older/default values. **Only client-a diverges; client-b matches server.** Zero CQP filter drop warnings. Sequences match (all participants). Re-sync fixes it → transient delivery gap.

**Evidence that Fix 25 worked:** Zero "CQP filter drop" warnings in the entire cycle log. The filter is no longer dropping changes.

**Key observation:** StringValue had value `0063df73` on client-a (written 11:14:42.699) and `0063e2df` on server (written 11:14:42.870). The subject WAS receiving updates, then stopped — a later value was lost.

**Diagnostic approach — narrowing the drop point:**

| Evidence | Server CQP (filter/factory) | Server broadcast | Client apply |
|----------|---------------------------|-----------------|-------------|
| Zero CQP filter drops | ✓ ruled out | | |
| Sequences match | | ✓ delivered | |
| Only client-a diverges, client-b OK | | Server DID broadcast the value (client-b has it) | **client-a failed to apply** |

**Conclusion:** The value was broadcast by the server (client-b received it). Client-a's own concurrent structural mutations (StructuralMutationRate=200/sec) detached the subject while the WebSocket receive loop's `ApplyUpdate` tried to apply the value. The applier's `TryResolveSubject` or `TryGetSubjectById` failed → value silently dropped.

**Two candidate mechanisms on client-a:**

1. **Applier deferred retry fails:** The applier pre-resolves subjects at the start of `ApplyUpdate`. If a concurrent client-side structural mutation detaches the subject between pre-resolve and property apply, `TryResolveSubject` fails → deferred to retry → retry also fails → silently dropped.

2. **Mutation engine writes to detached subject:** Client-a's own mutation engine writes a value to a subject from stale `_knownNodes` while the subject is detached. The write succeeds on the CLR object but the change notification doesn't reach the CQP (no context). However, this would cause client-a's values to DIFFER from what the server broadcast — not to be MISSING ("written never").

Mechanism 1 is more likely: the server broadcast the correct value, but client-a couldn't apply it due to concurrent structural mutations on client-a's graph.

**This is a library concern** — `ApplyUpdate` uses a lifecycle batch scope for subjects moving within the same update, but it doesn't protect against concurrent structural mutations from the application's own threads.

**Potential fixes:**

1. **Library fix:** Extend `ApplyUpdate` to hold a broader lock that prevents concurrent structural mutations during apply. Trade-off: serializes all graph mutations during update application.
2. **Library fix:** When `TryResolveSubject` fails on retry, check the pre-resolved cache (already implemented). If the subject was pre-resolved but is now gone, use the cached reference to apply the value. The CLR object still exists — just not in the registry.
3. **Tester mitigation:** Pause client structural mutations during the converge phase (doesn't fix the root cause but reduces the race window for testing).

**Diagnostics in place:**
- CQP filter drop warning with subject ID (server side)
- No client-side applier drop logging yet — next step is to add logging when `TryResolveSubject` fails after retry

**Status:** Under investigation. Next run should confirm mechanism 1 by checking which client diverges relative to chaos profile.

---

## Fix 28: Client-side registry divergence detection

**Files changed:** `SentStructuralState.cs`, `ChangeQueueProcessor.cs`, `WebSocketSubjectClientSource.cs`

**Problem:** The SentStructuralState hash (Fix 24) detects server→client delivery gaps (server sent an update, client missed it). But it CANNOT detect client→server delivery gaps: when a concurrent mutation changes the client's graph and the CQP fails to send the change to the server. Both sides' SentStructuralState agree (they track updates, not actual graph state), so no hash mismatch is detected. The divergence persists permanently.

**Root cause investigation findings:**
- `BUG-UNREGISTERED` diagnostic (added to WriteProperty) showed **zero** hits in 10 cycles — the lifecycle is processing correctly. The `_lastProcessedValues` seeding via `AttachSubjectToContext` (triggered by the source-generated lazy executor) already provides eager grandchild discovery.
- Fix 25 (subscribe-before-Welcome + drain) was **reverted**: the drain discards legitimate post-Welcome mutations, causing a regression (2-5 cycles vs 22+ without). The original CQP-after-Welcome order has a nanosecond gap that's acceptable.
- `DiscoverChildrenIfNeeded` in the applier was **disabled**: redundant with `AttachSubjectToContext` and caused regression by double-seeding `_lastProcessedValues`.
- The remaining failures are **delivery gaps during chaos**, not lifecycle races.

**Fix:** On each heartbeat received by the client, compare the client's SentStructuralState (what updates said) against the actual registry (what the lifecycle processed). If they differ, a CQP-dropped mutation changed the graph without notifying the server. Trigger reconnection → Welcome resets everything.

**Implementation:**
1. **`SentStructuralState.MatchesRegistry(idRegistry, registryCount)`**: compares tracked subject IDs against the live registry. O(1) count check + O(N) membership check.
2. **`ChangeQueueProcessor.IsIdle(duration)`**: tracks `_lastFlushWithChangesTicks`. Returns true if no changes have been flushed for the specified duration.
3. **`WebSocketSubjectClientSource.HasRegistryDivergence()`**: on heartbeat, if client has been idle (no updates received for 5 seconds), compares SentStructuralState against registry. Idle check prevents false positives during active mutations (unflushed mutations are in the registry but not yet in SentStructuralState).

**Properties:**
- Catches ANY client→server structural divergence, regardless of cause
- No false positives during active mutations (idle check)
- O(N) per heartbeat during idle (N = subject count, typically 100-500)
- Self-healing: reconnection + Welcome provides authoritative state
- Complements existing server→client detection (SentStructuralState hash)

**Analogous to:** TCP checksums, OPC UA subscription monitoring, CRDT anti-entropy protocols.

**ConnectorTester validation:** 2284+ cycles (all chaos profiles, 500+ structural mutations/sec), zero failures. The detection fires ~2 times per chaos cycle (reconnection heals the divergence before the convergence check). Zero detections on no-chaos cycles — no false positives.

**Status:** Applied.

---

## Memory investigation: HeapMB growth (~48 KB/cycle)

**Symptom:** HeapMB (measured after forced Gen2 compacting GC) grows at ~48 KB/cycle consistently across all chaos profiles.

**Investigation:**
- `BUG-UNREGISTERED` diagnostic in WriteProperty: **zero** hits in 10+ cycles — lifecycle processes correctly
- `SourceOwnershipManager._properties` leak counter: **leaked=0** — event pairing (SubjectAttached/SubjectDetaching) is correct
- `_attachedSubjects` count: matches registry exactly per participant
- `_lastProcessedValues` count: matches `subjects × structural_properties` exactly
- `_usedByContexts` on root contexts: count=1 each — clean
- Unit tests at all escalation levels (basic, registry, applier, concurrent, deep graph): all pass with zero GC retention

**Root cause:** Post-GC gcdump comparison (cycle 13 vs cycle 2286) showed **only 1.9 KB/cycle** of actual managed object growth — entirely from dictionary/HashSet backing array capacity drift (`Entry<PropertyReference>[]`, `Slot<SubjectPropertyChange>[]`). The remaining ~46 KB/cycle in `HeapMB` is GC internal fragmentation from the high-churn mutation workload (millions of short-lived objects per cycle).

**Conclusion:** Not a reference leak. Dictionary/HashSet capacity drift + GC heap fragmentation. At 1.9 KB/cycle actual growth, it would take ~500K cycles to reach 1 GB. Low priority. Could be mitigated with periodic `TrimExcess()` on key collections.
