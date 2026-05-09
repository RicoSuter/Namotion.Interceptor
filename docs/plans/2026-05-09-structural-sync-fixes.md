# Plan: Structural Sync Fixes

Design: `docs/design/2026-05-09-structural-sync-fixes.md`
Branch: `feature/opcua-dynamic-discovery-improvements`

## Approach

TDD for each fix: write a failing integration test, implement the fix, verify all tests pass. Simplify code as we go.

---

## Fix 1: Unique NodeIds for Dynamic Subjects

### Task 1.1: Write failing integration test

Add test `WhenServerReplacesCollectionItemAtSameIndex_ThenClientSeesNewSubject` to `StructuralSyncTests.cs`. The test should:
- Start server+client with structural sync
- Add 3 subjects to People
- Wait for client sync
- Replace subject at index 1 with a new subject (same array position, different instance)
- Assert client sees the new subject's values

This test should fail with the current positional NodeId scheme.

### Task 1.2: Implement unique NodeIds

In `CustomNodeManager.cs`:
- Add `private long _dynamicNodeCounter` field
- In `CreateDynamicSubjectNodes`, for collection items: use `$"{parentPath}{propertyName}_{Interlocked.Increment(ref _dynamicNodeCounter)}"` instead of `$"{parentPath}{propertyName}[{change.Index}]"`
- For dictionary items: keep the key-based path (keys are unique by definition)
- BrowseName stays unchanged (positional/key-based)

### Task 1.3: Verify

- Run the new test (should pass)
- Run all integration tests
- Run unit tests
- Un-skip the two previously skipped tests if they now pass
- Simplify: remove any workarounds for NodeId reuse

---

## Fix 2: Inline Server Structural Processing

### Task 2.1: Write failing integration test

The stress test `WhenServerMutatesStructureAndValuesRapidly` should now pass structurally (fix 1) but may still fail on value convergence. If it already passes, write a more targeted test:

Add test `WhenServerAddsSubjectAndImmediatelyMutatesValue_ThenClientSeesValue` to `StructuralSyncTests.cs`:
- Add a subject to People
- In the same synchronous block (no await between), mutate its FirstName
- Assert client sees both the subject AND the mutated FirstName

### Task 2.2: Implement inline server structural processing

In `OpcUaSubjectServer.WriteChangesAsync`:
- Before the value loop, iterate changes and process structural changes inline (create/remove nodes, fire ModelChangeEvents)
- Remove `_structuralProcessor?.EnqueueStructuralChanges(span)` call
- Remove `_structuralProcessor` field, `_structuralProcessorTask`, and background task creation

Move the node creation/removal logic from `OpcUaServerStructuralChangeProcessor.ProcessEventAsync` into helper methods callable from `WriteChangesAsync`. If `OpcUaServerStructuralChangeProcessor` becomes empty, delete it.

In `OpcUaSubjectClientSource.WriteChangesAsync`:
- Before passing changes to the outbound writer, process structural changes: send `AddNodes`/`DeleteNodes` for added/removed subjects
- Remove client-side `EnqueueStructuralChanges` for outgoing changes (keep the Channel queue for incoming ModelChangeEvents)

### Task 2.3: Verify

- Run the new test (should pass)
- Run stress test
- Run all integration tests
- Run unit tests
- Simplify: remove `OpcUaServerStructuralChangeProcessor` if fully inlined

---

## Fix 3: Client Incoming Race Protection (conditional)

### Task 3.1: Evaluate

After fixes 1+2, run the stress test and ConnectorTester `opcua-structural-serveronly` profile. Check:
- Do all subjects converge? (fix 1 should resolve missing subjects)
- Do all values converge? (fix 2 should resolve server-side value drops)
- If yes: skip fix 3, document why it's not needed
- If no: proceed with fix 3

### Task 3.2: Write failing test (if needed)

Add test that demonstrates a value stuck at stale read value after structural add.

### Task 3.3: Implement semaphore (if needed)

Add `SemaphoreSlim` shared between `OnFastDataChange` and the structural processor's `ProcessExternalAddAsync`. The structural processor holds the semaphore during subscribe+read. Value notifications wait.

### Task 3.4: Verify (if needed)

- Run stress test
- Run ConnectorTester
- Run all tests

---

## Verification Checklist

After all fixes:
- [ ] All unit tests pass
- [ ] All integration tests pass (including previously skipped ones)
- [ ] Stress test `WhenServerMutatesStructureAndValuesRapidly` passes
- [ ] ConnectorTester `opcua-structural-serveronly` converges
- [ ] Code simplified (removed classes/methods/dead code)
