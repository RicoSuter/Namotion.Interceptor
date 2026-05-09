# Investigations

Open questions and issues to look into.

## OPC UA: Duplicate Add events for same NodeId without intervening Remove

**Observed in:** Stress test `WhenServerMutatesStructureAndValuesRapidly_ThenClientConvergesToFinalState`

**Symptom:** The client receives two `ModelChangeEvent(NodeAdded, Root.People[2])` without a `ModelChangeEvent(NodeDeleted, Root.People[2])` in between. The client's idempotent check drops the second Add because the SubjectMap already has an entry for that NodeId.

**Root cause hypothesis:** The server's `EnqueueStructuralChanges` computes the subject diff (old vs new collection). When two rapid mutations both add a subject at the same index (e.g., index 2), the server fires two Add events for `People[2]` without a Remove. This suggests the index assignment in `CreateDynamicSubjectNodes` uses the subject's position in the new array, which can collide across mutations.

**Root cause confirmed:** `ExtractSubjects` records each subject's current array index. `ComputeSubjectDiff` uses reference equality for add/remove detection but carries the array index. When subject C moves from index 2 to index 1 (due to another subject being removed), its OPC UA node keeps NodeId `People[2]`. A new subject D added at array index 2 gets the same path `People[2]`, creating a duplicate NodeId. The diff is correct (different subjects by reference) but the index-to-NodeId mapping is wrong because C# array indices shift on removal while OPC UA NodeIds are fixed.

**Fix:** Use a monotonic counter per collection property instead of array index for NodeId generation. See `docs/design/2026-05-09-structural-sync-fixes.md` for the design.

**Related:** Two integration tests are skipped with the same root cause:
- `WhenServerReplacesCollectionEntirely_ThenClientSeesNewItems` (Skip: "Requires unique NodeIds: path-based NodeIds are reused when items are replaced at the same index")
- `WhenServerReplacesValueAtExistingDictionaryKey_ThenClientSeesNewSubject` (Skip: "Requires unique NodeIds: path-based NodeIds are reused when values are replaced at the same key")
