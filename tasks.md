# WebSocket Structural Mutations - Convergence Investigation

## Goal

Make the ConnectorTester pass with structural mutations enabled for WebSocket sync.
Run command: `dotnet run --project src/Namotion.Interceptor.ConnectorTester --launch-profile websocket -c Release`
60s mutation phase + 60s convergence timeout.

## Test Setup

- **Server**: 1000 value mutations/sec, 2 structural mutations/sec
- **Client-A**: 100 value mutations/sec, 1 structural mutation/sec
- **Client-B**: 100 value mutations/sec, 1 structural mutation/sec
- **Chaos profiles**: no-chaos (current), server-only, client-a-only, all-clients, full-chaos
- **Structural mutations**: ObjectRef replacements, Collection adds/removes, Dictionary adds/removes
- **Total per run**: ~240 structural mutations, ~63,000 value mutations

## Architecture Overview

```
MutationEngine (per participant)
    ↓ property writes
ChangeTracker (IWriteInterceptor)
    ↓ SubjectPropertyChange events
ChangeQueueProcessor (8ms buffer, dedup by PropertyReference)
    ↓ flush batch
SubjectUpdateFactory.CreatePartialUpdateFromChanges()
    ↓ SubjectUpdate (flat dict of subjectId → properties)
WebSocketSubjectHandler.BroadcastChangesAsync()
    ↓ serialize + send to all clients
WebSocketSubjectClientSource.ReceiveLoopAsync()
    ↓ deserialize
SubjectUpdateApplier.ApplyUpdate()
    ↓ set property values via RegisteredSubjectProperty.SetValue()
ChangeTracker fires again → echoed back (filtered by source)
```

### Key Mechanisms

- **useCompleteStructuralState=true**: Server broadcasts read current live property values instead of change snapshots (avoids stale diffs with multiple concurrent writers)
- **Echo prevention**: ChangeQueueProcessor has source filter. Server source=handler, Client source=clientSource. MutationEngine source=null (passes both filters).
- **Dedup**: ChangeQueueProcessor keeps first old value + last new value per PropertyReference via `WithOldValueFrom`
- **SubjectUpdateApplyContext.TryMarkAsProcessed**: Prevents double-processing of subjects during apply

## Fixes Applied (This Branch)

### Prior Sessions (Fixes 1-7)

1. **Idempotent collection insert** - Guard against duplicate Insert operations
2. **Count trim guards** - Prevent out-of-range Remove operations
3. **Dedup old-value merging** - ChangeQueueProcessor keeps first old + last new
4. **Partial update path ordering** - Process structural changes before value changes
5. **ApplyObjectUpdate registry lookup** - Reuse existing subjects by stable ID
6. **Complete structural state** - Server broadcasts use `useCompleteStructuralState: true`
7. **Stable base62 subject IDs** - 22-char base62-encoded GUIDs for cross-participant identity

### This Session (Fixes 8-9)

8. **Race condition mitigation for detached subjects** (SubjectUpdateFactory.cs)
   - **8a**: When `useCompleteStructuralState=true`, read `property.GetValue()` (current live) instead of `change.GetNewValue()` (snapshot) for Collection, Dictionary, AND ObjectRef
   - **8b**: In `ProcessSubjectComplete`, create empty Subjects entry when subject is detached (`TryGetRegisteredSubject()` returns null)

9. **CRITICAL: SubjectUpdateApplier if/else bug** (SubjectUpdateApplier.cs)
   - **Problem**: `ApplyUpdate()` had an if/else structure. When root subject had entries in `Subjects` dict (because root properties changed in the same batch), the `else` branch (which iterates ALL subjects by stable ID) was SKIPPED. Non-root subject changes were **silently dropped**.
   - **Fix**: Changed if/else to always run the stable-ID-lookup loop after the root path. `TryMarkAsProcessed` prevents double-processing.
   - **Impact**: 97% reduction in failures (from ~30 structural failures to exactly 1)

10. **CRITICAL: ApplyObjectUpdate replacement bug** (SubjectUpdateApplier.cs)
    - **Problem**: When `existingItem is not null`, the code assumed it was the SAME logical subject as the update target. It blindly set `existingItem.SetSubjectId(propertyUpdate.Id)` and kept `targetItem = existingItem`. Since `existingItem == targetItem`, `property.SetValue()` was NEVER called. For ObjectRef REPLACEMENTS (server swapped subject A → B), this meant the client kept pointing to A (with corrupted stable ID set to B's ID), instead of replacing it with B.
    - **Fix**: Check if existing item's stable ID matches `propertyUpdate.Id`. If they match → same subject, keep it. If they differ → replacement, look up or create the correct target subject by stable ID.
    - **Why it only affected ~0.4%**: In most cases, the properties were fully overwritten in-place (making OLD functionally equivalent to NEW). Failure occurred when `TryMarkAsProcessed` returned false or concurrent modifications prevented full property overwrite.

## Current State

### Test Results After All Fixes (Including Fix 10)

**All cycles PASS with all chaos profiles:**

| Cycle | Profile | Result | Convergence |
|-------|---------|--------|-------------|
| 1     | no-chaos | PASS | 0.0s |
| 2     | server-only | PASS | 0.0s |
| 3     | client-a-only | PASS | 0.0s |
| 4     | all-clients | PASS | 0.0s |
| 5     | full-chaos | PASS | 0.0s |
| 6-11  | all profiles again | PASS | 0.0s |

- **0** failures across 11+ cycles
- ~63,000 value mutations + ~240 structural mutations per cycle
- All chaos events (Kill, Disconnect) recovered correctly
- Convergence is immediate (0.0s) after mutation phase ends

## Open Investigation

### Hypotheses for Remaining 1 ObjectRef Failure

1. **Timing race in flush**: The ObjectRef replacement change is captured but the flush batch builds the update at a moment when a concurrent MutationEngine write on the SAME property overwrites it before `property.GetValue()` reads it (since `useCompleteStructuralState=true` reads current live value, a concurrent write could replace the value AGAIN, and the intermediate value is lost)

2. **Client-side apply ordering**: When a client receives an update that replaces an ObjectRef, the new target subject might not yet be registered in the client's SubjectRegistry. `ApplyObjectUpdate` tries registry lookup by stable ID, fails, then tries to create from factory — but if the subject data is in the same update, it might not be processed yet.

3. **ChangeQueueProcessor dedup**: If an ObjectRef property is written twice in the same buffer window (e.g., MutationEngine replaces it, then a WebSocket update from the other client also touches it), the dedup keeps first-old + last-new. The intermediate value (which was the correct server state) is lost.

4. **Broadcast excluded by source filter**: A client sends an ObjectRef update to server. Server applies it (source=connection). ChangeQueueProcessor captures the change but source filter might exclude it from broadcast back to OTHER clients.

### Next Steps

- [ ] Analyze latest failure snapshots in detail (exact subject IDs, parent properties)
- [ ] Add targeted diagnostic logging to ObjectRef broadcast/apply path
- [ ] Determine: is the update never sent, sent but not received, or received but not applied?
- [ ] Check if the failing ObjectRef is always a server-originated or client-originated mutation
- [ ] Consider whether hypothesis 1 or 4 explains the "random which client fails" pattern

## Future Consideration: Eliminate `useCompleteStructuralState`

### Background

`useCompleteStructuralState=true` was introduced as Fix 6 to work around unreliable diff-based updates when multiple concurrent writers (server MutationEngine + incoming client updates) modify the same structural properties. When enabled, the server reads the **current live property value** at flush time instead of computing a diff from old→new change snapshots.

### Problems with `useCompleteStructuralState`

- **Bandwidth**: Sends the full collection/dictionary/object state on every change instead of a minimal diff (Insert/Remove operations). For large collections this is wasteful.
- **Race window**: Reading live state at flush time introduces its own race — a concurrent write between change capture and `property.GetValue()` can cause the intermediate state to be lost (hypothesis 1 for the remaining ObjectRef bug).
- **Conceptual mismatch**: The diff-based protocol (Insert/Remove/Move operations) was designed for efficiency. Bypassing it with complete state is a workaround, not a fix.

### Can We Fix the Protocol to Not Need It?

The root cause that motivated `useCompleteStructuralState` is: **the change snapshot (old/new values) can become stale between capture and flush when concurrent writers modify the same property**.

Potential alternatives:
1. **Lock structural writes during flush**: Ensure MutationEngine and incoming client updates cannot write structural properties while ChangeQueueProcessor is building the SubjectUpdate. Downside: increases contention, may hurt throughput.
2. **Sequence-aware dedup**: Instead of keeping first-old + last-new, keep ALL structural changes in order and replay them as a sequence of operations. This preserves intermediate states.
3. **Snapshot-on-capture for structural properties**: Take a deep snapshot of collection/dictionary state at change-capture time (not just the reference). This makes the change self-contained. Downside: allocation cost per structural change.
4. **Server-side version vectors**: Track per-property version numbers. If a flush detects the property was mutated again since the captured change, re-read and send complete state only for that property (adaptive approach).

### Verdict

Investigate whether fixing the protocol properly (option 2 or 3) can eliminate `useCompleteStructuralState` entirely. This would fix the remaining ObjectRef race AND reduce bandwidth. However, this is a larger refactor — the current workaround is acceptable for getting convergence working first.

## Code Review of All Changes

### All changes are necessary — no speculative/hope-based code found

Every change traces back to a specific fix or the core stable-ID feature:

| Change | Why | Necessary? |
|--------|-----|-----------|
| `SubjectRegistryExtensions` (+57 lines) | Core: `GenerateSubjectId`, `GetOrAddSubjectId`, `SetSubjectId` | Yes - core feature |
| `ISubjectRegistry` (+21 lines) | Core: `RegisterStableId`, `UnregisterStableId`, `TryGetSubjectByStableId` | Yes - core feature |
| `SubjectRegistry` (+42 lines) | Core: `_stableIdToSubject` reverse index + cleanup on detach | Yes - core feature |
| `SubjectUpdateBuilder` (+37 lines) | Core: `GetOrCreateIdWithStatus`, `SubjectHasUpdates`, `ProcessedSubjects` | Yes - core feature |
| `SubjectUpdate` (+13 lines) | `useCompleteStructuralState` param | Yes - Fix 6 |
| `SubjectUpdateFactory` (193→ lines, net reduction) | Core: stable-ID-based update building, `useCompleteStructuralState` branches, detached subject defense | Yes - core + Fix 8a/8b |
| `SubjectUpdateApplier` (+112 lines) | Core: stable-ID-based apply, always-run-both paths, ObjectRef replacement detection | Yes - core + Fix 9 + Fix 10 |
| `SubjectItemsUpdateApplier` (+259 lines) | Core: ID-based Insert/Remove/Move, idempotent guards, registry lookup | Yes - core + Fix 1/2/5 |
| `SubjectItemsUpdateFactory` (+102 lines) | Core: ID-based collection/dictionary diff and complete state | Yes - core |
| `CollectionDiffBuilder` (98→ lines, net reduction) | Core: subject-reference-based diff (replaced index-based) | Yes - core |
| `SubjectCollectionOperation` (+25 lines) | Core: `Id`, `AfterId`, `Key` (replaced `Index`) | Yes - core |
| `SubjectPropertyItemUpdate` (+20 lines) | Core: `Id`, `Key` (replaced index-based) | Yes - core |
| `ChangeQueueProcessor` (+20 lines) | Fix 3: `WithOldValueFrom` dedup merge | Yes - Fix 3 |
| `SubjectPropertyChange` (+20 lines) | Fix 3: `WithOldValueFrom` method | Yes - Fix 3 |
| `WebSocketSubjectHandler` (+7 lines) | Fix 6: `useCompleteStructuralState: true` | Yes - Fix 6 |
| `WebSocketSubjectClientSource` (+35 lines) | Structural ownership: claim structural props + auto-claim on attach | Yes - needed for client→server structural forwarding |

### Lower-level library changes review (Registry, Tracking)

All changes are **additive** — no existing behavior modified. However:

**`ISubjectRegistry` interface (BREAKING for external implementors)**
- Added 3 new methods: `RegisterStableId`, `UnregisterStableId`, `TryGetSubjectByStableId`
- This is a breaking change for anyone who implements `ISubjectRegistry` outside the repo
- Mitigation: library is version 0.0.2 (pre-release), so breaking changes are expected
- Consider: default interface methods to make it non-breaking?

**`SubjectRegistry` detach cleanup — O(n) linear scan**
- Line 116-129: On every subject detach, iterates ALL entries in `_stableIdToSubject` to find the matching subject by reference equality. There's a TODO noting this.
- For large graphs (1000+ subjects), this linear scan on every detach could be noticeable.
- Fix: use `subject.Data.TryGetValue(SubjectIdKey)` to get the stable ID directly, then `_stableIdToSubject.Remove(id)` — O(1) instead of O(n).

**`SubjectRegistry` lock contention**
- All stable ID operations (`Register`, `Unregister`, `TryGetByStableId`) share the same `_knownSubjects` lock.
- Acceptable for now but could be a bottleneck under high concurrency.
- Already existing pattern (all SubjectRegistry methods use this lock).

**`SubjectPropertyChange.WithOldValueFrom` (Tracking)**
- `internal` method, zero-allocation for inline values.
- Does not affect existing public API or behavior.
- Clean implementation.

**`SubjectRegistryExtensions.GenerateSubjectId`**
- Uses `BigInteger` division loop — allocates ~3 objects per call.
- Called once per subject creation, so not in any hot path.
- Acceptable.

### Potential simplifications (not blocking, for later)

- [ ] `SubjectItemsUpdateApplier.ApplyDictionaryUpdate` lines 281-309: The "trim excess" block has an unreachable inner `if (propertyUpdate.Operations is { Count: > 0 })` check — we already checked `Operations is null` on line 281. The `updatedKeys` from Operations will always be empty. Simplify to only collect keys from `Items`.
- [ ] `SubjectItemsUpdateApplier` resolves `parent.Context.TryGetService<ISubjectRegistry>()` multiple times per call (once per Insert operation). Could resolve once and pass through.

## Cleanup Tasks

- [ ] **Add `TryGetSubjectId` extension method**: `SubjectUpdateApplier` currently accesses `subject.Data.TryGetValue((null, "Namotion.Interceptor.SubjectId"), ...)` directly, leaking internal knowledge of how stable IDs are stored. Add a `TryGetSubjectId(this IInterceptorSubject subject, out string? id)` extension method to `SubjectRegistryExtensions` alongside the existing `GetOrAddSubjectId` and `SetSubjectId` methods. Then update the applier to use it.
- [ ] **Eliminate `useCompleteStructuralState`** (see section above)
- [ ] **Remove magic string duplication**: The `SubjectIdKey` constant is `internal` in `Namotion.Interceptor.Registry`. Either make it `public` or provide proper public API surface.
- [ ] **Update docs with updated protocol**: Update documentation in `docs/` to reflect the new stable-ID-based protocol (base62 subject IDs, ID-based collection/dictionary operations, ObjectRef replacement semantics, `useCompleteStructuralState` behavior).

## Regression Test Coverage Needed

The following bugs were found and fixed but lack dedicated unit tests to prevent regressions:

- [ ] **Fix 3 — ChangeQueueProcessor dedup old-value merging**: Test that when the same property changes A→B then B→C in the same buffer window, the deduped change preserves old=A and new=C (not old=B, new=C). File: `ChangeQueueProcessor` + `SubjectPropertyChange.WithOldValueFrom`.
- [ ] **Fix 6 — `useCompleteStructuralState` reads live property**: Test that when `useCompleteStructuralState=true`, `CreatePartialUpdateFromChanges` reads the current property value for structural types (Collection, Dictionary, ObjectRef) instead of the change snapshot. Verify this for all 3 structural kinds.
- [ ] **Fix 8b — Detached subject creates empty entry**: Test that `ProcessSubjectComplete` creates an empty Subjects entry when `TryGetRegisteredSubject()` returns null (subject detached).
- [ ] **Fix 9 — Non-root subjects always processed**: Test that when a partial update contains changes to both root and non-root subjects, ALL subjects are processed (not just the root). The if/else regression: ensure the stable-ID-lookup loop runs even when root has entries.
- [ ] **Fix 10 — ObjectRef replacement detection**: Test that when an ObjectRef property already has subject A (with stable ID "x") and the update says the property should point to subject B (with stable ID "y"), the applier creates/finds B and calls `SetValue(B)` — NOT keeping A with B's ID. Test both: same-ID (keep existing) and different-ID (replace).
- [ ] **Fix 1 — Idempotent collection Insert**: Test that applying the same Insert operation twice (same subject ID) doesn't create a duplicate in the collection.
- [ ] **Fix 2 — Collection count trim guards**: Test that a Remove operation for a non-existent ID is safely ignored (no exception, no side effects).
- [ ] **Stress test with higher structural mutation rates**: Run ConnectorTester with 10x structural mutation rates (20 structural/sec server, 10/sec clients) to increase the likelihood of concurrent structural operations overlapping. Value mutation rates are already high enough. Also try shorter buffer times (1-2ms) to stress the dedup logic with more frequent flushes during structural changes.

## Key Source Files

| File | Role |
|------|------|
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateApplier.cs` | Applies SubjectUpdate to subject graph |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectUpdateFactory.cs` | Creates SubjectUpdate from changes |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateApplier.cs` | Applies Collection/Dictionary operations |
| `src/Namotion.Interceptor.Connectors/Updates/Internal/SubjectItemsUpdateFactory.cs` | Creates Collection/Dictionary updates |
| `src/Namotion.Interceptor.Connectors/ChangeQueueProcessor.cs` | Buffers, deduplicates, flushes changes |
| `src/Namotion.Interceptor.WebSocket/Server/WebSocketSubjectHandler.cs` | Server-side WebSocket handler + broadcast |
| `src/Namotion.Interceptor.WebSocket/Client/WebSocketSubjectClientSource.cs` | Client-side WebSocket receive + apply |
| `src/Namotion.Interceptor.ConnectorTester/Engine/MutationEngine.cs` | Generates random mutations |
| `src/Namotion.Interceptor.ConnectorTester/Engine/VerificationEngine.cs` | Compares snapshots |
| `src/Namotion.Interceptor.Registry/SubjectRegistry.cs` | Subject registration + stable ID lookup |
