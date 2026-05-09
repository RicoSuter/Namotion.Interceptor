# Investigations

Open questions and issues to look into.

## OPC UA: ConnectorTester convergence investigation (2026-05-09)

### Progress

Fixed the snapshot comparison (was using sequential IDs that shifted when subjects were missing, now uses path-based matching). With correct comparison:

- **Structure converges perfectly**: 148 paths on both sides, 0 missing, 0 extra
- **Values nearly converge**: only 3-6 diffs per run, all on `ObjectRef` paths

### Fixes applied (committed)

1. **Unique NodeIds** (`CustomNodeManager._dynamicNodeCounter`): positional `People[2]` replaced with counter-based `People_42`. Fixes NodeId reuse when subjects shift positions.
2. **Inline server structural processing**: structural changes processed synchronously in `WriteChangesAsync` before value loop. Removed `OpcUaServerStructuralChangeProcessor` class.
3. **Filter failed monitored items**: `AddMonitoredItemsAsync` now calls `FilterOutFailedMonitoredItemsAsync` after `ApplyChangesAsync`.
4. **Path-based snapshot comparison**: `VerificationEngine.CreateSnapshot` now walks graph by position (`ROOT/Collection[2]/Items[key]`) instead of sequential IDs.

### Ruled out

- **Throughput**: structural events process in 1ms median, not a bottleneck
- **SemaphoreSlim for incoming serialization**: blocking the SDK callback thread makes things worse (365 diffs vs 12)
- **Removing ReadInitialValuesAsync**: 1385 diffs without it (initial notifications unreliable)
- **Dropped notifications**: only 2 unmatched ClientHandle warnings in a full run

### Remaining issue: ObjectRef value diffs (3-6 per run)

All remaining diffs are on `ObjectRef` paths. Pattern:
- `ROOT/Collection[N]/ObjectRef`: both sides have values but they disagree (stale values on one side)
- `ROOT/Items[key]/ObjectRef`: server has defaults, client has values (or vice versa)

ObjectRef replacement during `MutateObjectRef`:
```csharp
target.ObjectRef = null;       // remove
target.ObjectRef = CreateNewNode();  // add new with defaults
```

This creates two structural events (Remove + Add). The new ObjectRef starts with defaults. The MutationEngine may or may not mutate its values before mutations stop. If one side processes the Remove+Add at a different point in time than the other, values diverge.

### Next steps

- Investigate why ObjectRef replacement causes stale values
- Check if ObjectRef Remove event properly cleans up subscriptions on client
- Check if ObjectRef Add event properly sets up subscriptions for the new subject

## OPC UA: Duplicate Add events for same NodeId without intervening Remove

**Observed in:** Stress test `WhenServerMutatesStructureAndValuesRapidly_ThenClientConvergesToFinalState`

**Symptom:** The client receives two `ModelChangeEvent(NodeAdded, Root.People[2])` without a `ModelChangeEvent(NodeDeleted, Root.People[2])` in between. The client's idempotent check drops the second Add because the SubjectMap already has an entry for that NodeId.

**Root cause hypothesis:** The server's `EnqueueStructuralChanges` computes the subject diff (old vs new collection). When two rapid mutations both add a subject at the same index (e.g., index 2), the server fires two Add events for `People[2]` without a Remove. This suggests the index assignment in `CreateDynamicSubjectNodes` uses the subject's position in the new array, which can collide across mutations.

**Root cause confirmed:** `ExtractSubjects` records each subject's current array index. `ComputeSubjectDiff` uses reference equality for add/remove detection but carries the array index. When subject C moves from index 2 to index 1 (due to another subject being removed), its OPC UA node keeps NodeId `People[2]`. A new subject D added at array index 2 gets the same path `People[2]`, creating a duplicate NodeId. The diff is correct (different subjects by reference) but the index-to-NodeId mapping is wrong because C# array indices shift on removal while OPC UA NodeIds are fixed.

**Fix:** Use a monotonic counter per collection property instead of array index for NodeId generation. See `docs/design/2026-05-09-structural-sync-fixes.md` for the design.

**Related:** Two integration tests are skipped with the same root cause:
- `WhenServerReplacesCollectionEntirely_ThenClientSeesNewItems` (Skip: "Requires unique NodeIds: path-based NodeIds are reused when items are replaced at the same index")
- `WhenServerReplacesValueAtExistingDictionaryKey_ThenClientSeesNewSubject` (Skip: "Requires unique NodeIds: path-based NodeIds are reused when values are replaced at the same key")
