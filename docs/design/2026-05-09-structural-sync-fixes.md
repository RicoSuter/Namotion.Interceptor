# Structural Sync Fixes

Three fixes for OPC UA structural synchronization correctness, ordered by dependency.

## Problem Summary

The stress test (`WhenServerMutatesStructureAndValuesRapidly`) shows the client stuck at 1 subject while the server has 13+. Three issues contribute:

1. **NodeId collisions**: Collection items get positional NodeIds (`People[2]`). When subjects shift positions in the C# array, new subjects can get the same NodeId as existing ones. The client's idempotent check drops the duplicate Add.

2. **Server outgoing race**: The CQP processes value changes synchronously in `WriteChangesAsync`, but structural changes are enqueued to a background Channel. Value changes for newly added subjects are silently dropped because OPC UA nodes don't exist yet.

3. **Client incoming race**: Value notifications (`OnFastDataChange`) and structural processing (`ProcessExternalAddAsync`) run on independent threads. The `ReadInitialValuesAsync` call can overwrite a fresher notification value.

## Fix 1: Unique NodeIds for Dynamic Subjects

**Change:** Use a monotonic counter per `CustomNodeManager` instance instead of the array index for collection/dictionary item NodeIds.

**Current:** `People[2]` (positional, reused when items shift)
**New:** `People_42` (counter-based, unique per subject instance)

The counter increments each time a dynamic child node is created. The BrowseName stays positional for OPC UA browsing (`People[2]`). Only the NodeId changes.

**Files:**
- `CustomNodeManager.cs`: Add `_dynamicNodeCounter` field. Change `CreateDynamicSubjectNodes` to use counter instead of `change.Index` in the NodeId path.

**Does NOT affect:**
- Static nodes created at startup (they keep path-based NodeIds)
- Variable nodes (only object nodes for collection/dictionary items)
- BrowseNames (stay positional/key-based for OPC UA browsing)

## Fix 2: Inline Server Structural Processing

**Change:** Process structural changes synchronously in `WriteChangesAsync`, before the value loop. Remove the server-side Channel queue.

**Current flow:**
```
WriteChangesAsync:
  1. EnqueueStructuralChanges → background Channel (slow)
  2. Value loop → update OPC UA nodes (fast, nodes may not exist!)
```

**New flow:**
```
WriteChangesAsync:
  1. Process structural changes inline (create/remove nodes)
  2. Value loop → update OPC UA nodes (nodes guaranteed to exist)
```

**Files:**
- `OpcUaSubjectServer.cs`: Inline structural processing in `WriteChangesAsync`. Remove `_structuralProcessor` field and background task.
- `OpcUaServerStructuralChangeProcessor.cs`: Remove or reduce to helper methods called from `WriteChangesAsync`.

**Client outgoing:** Same pattern. In client's `WriteChangesAsync`, send `AddNodes`/`DeleteNodes` before value writes. This is already async (`WriteChangesAsync` returns `ValueTask`).

## Fix 3: Client Incoming Race Protection

**Change:** If testing after fixes 1+2 still shows value diffs, add a `SemaphoreSlim` shared between `OnFastDataChange` and the structural processor. The structural processor holds the semaphore during subscribe+read, blocking value notifications temporarily (~100-400ms). This eliminates the read/subscribe race.

**Decision:** Implement only if fixes 1+2 don't achieve convergence. The OPC UA spec guarantees notification ordering within a subscription, and subscription-based initial values should be reliable once the server correctly publishes all values (fix 2).

**If needed, files:**
- `OpcUaSubjectClientSource.cs` or `SubscriptionManager.cs`: Add `SemaphoreSlim`, acquire in `OnFastDataChange`, acquire async in structural processor.

## Simplification Opportunities

While implementing, look for code to remove:
- Server Channel queue (`OpcUaServerStructuralChangeProcessor` as a separate class)
- `ReadInitialValuesAsync` in client processor (may be unnecessary after fix 2)
- Any dead code paths exposed by inlining
