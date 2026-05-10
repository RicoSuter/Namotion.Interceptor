# Investigations

Current state and open issues for OPC UA structural synchronization.

This branch (`feature/opcua-bidirectional-structural-sync`) is a rewrite of the original structural mutations PR (#121). That PR used a more complex architecture with `GraphChangeSender`/`GraphChangeReceiver`/`GraphChangeDispatcher`/`GraphChangeApplier` base classes and ~3,200 lines of production code. This rewrite is simpler: the server processes structural changes inline in `WriteChangesAsync`, and the client uses a unified incoming event queue for both value notifications and structural `ModelChangeEvent`s. Outgoing structural changes (AddNodes/DeleteNodes) are processed inline in the client's `WriteChangesAsync` before value writes.

## Current State (2026-05-10, post Feature 1+2)

### ConnectorTester Profile Results

| Profile | Struct Rate | Description | Status |
|---------|------------|-------------|--------|
| `opcua-structural-serveronly` | Server: 20/s, Client: 0 | Server mutates structure, client observes | **Nearly works**: 431/428 nodes, 5 missing, 3 value diffs |
| `opcua-structural-simple` | Server: 50/s, Client: 50/s | Both sides mutate structure | **Broken**: 501 server nodes, 42 client nodes. Client incoming nearly dead (8 received changes total). |
| `opcua-structural` | Server: 200/s, Client: 200/s + chaos | Full stress with disconnects | **Not tested** |
| `opcua-structural-nosync` | No structural sync | Baseline value sync | **Not tested** |

### Integration Tests

58 pass, 0 skipped, 0 failures.

## Fixes Applied (this branch)

1. **Unique NodeIds** (`CustomNodeManager._dynamicNodeCounter`): Collections, dictionaries, and references all use a monotonic counter for dynamically created NodeIds. Prevents NodeId reuse when subjects are replaced at the same position.
2. **Inline server structural processing**: Structural changes processed synchronously in `WriteChangesAsync` before the value loop. Removed `OpcUaServerStructuralChangeProcessor` class. Ensures OPC UA nodes exist before value writes.
3. **Inline client structural processing**: Client `WriteChangesAsync` sends `AddNodes`/`DeleteNodes` inline before value writes, mirroring the server design. Symmetric outgoing path.
4. **Echo suppression**: `ConcurrentDictionary<NodeId, byte>` on the client's incoming processor. Populated after AddNodes/DeleteNodes responses. Checked before processing incoming ModelChangeEvents. Cleaned on detach and reconnect.
5. **Unified incoming queue**: Client routes both value notifications (`OnFastDataChange`) and structural events (`ModelChangeEvent`) through a single `Channel<IncomingEvent>`, processed in arrival order. Guarantees values for new subjects aren't processed before the subject exists.
6. **SemaphoreSlim deadlock fix**: `HandleRemoteAddNode` and `HandleRemoteDeleteNode` held `_structureLock` while calling `CreateDynamicSubjectNodes`/`RemoveSubjectNodes`, which also acquire `_structureLock`. SemaphoreSlim doesn't support reentrancy, causing a deadlock on any client AddNodes/DeleteNodes request. Fixed by releasing the lock before node creation/removal.
7. **ExpandedNodeId storage fix**: `AddNodes` response returns `ExpandedNodeId`, but `SubjectNodeIdDataKey` was storing it as-is. Other code checks `obj is NodeId`, which fails for `ExpandedNodeId`. Now converts to `NodeId` before storing.
8. **Filter failed monitored items**: `AddMonitoredItemsAsync` now calls `FilterOutFailedMonitoredItemsAsync` after `ApplyChangesAsync` for dynamically added items.
9. **Path-based snapshot comparison**: `VerificationEngine.CreateSnapshot` walks the graph by position (`ROOT/Collection[2]/Items[key]`) instead of sequential IDs. The old approach produced false diffs when subjects were missing.
10. **11 integration tests**: Echo suppression, bidirectional convergence, rapid adds, value sync after structural adds, and stress test.

## Ruled Out

- **Throughput**: Structural events process in 1ms median, not a bottleneck at 20/sec.
- **SemaphoreSlim for incoming serialization**: Blocking the SDK's publish callback thread causes more diffs (365 vs 12), not fewer.
- **Removing ReadInitialValuesAsync**: 1385 diffs without it. Initial notifications from `MonitoringMode.Reporting` are unreliable for dynamically added items.
- **Dropped notifications**: Only 2 unmatched `ClientHandle` warnings in a full run. Subscriptions deliver correctly.

## Open Issues

### 1. Server-only: 5 missing subjects, 3 value diffs

At 20 struct/sec, 5 subjects are missing from the client and 3 value diffs remain after 2-minute convergence. The missing subjects are likely stale events (server adds then removes quickly). The value diffs are timing-related.

Previous result (before unified queue): 0 missing, 1 value diff. The unified queue may add latency during structural event processing that delays value convergence.

**Potential fix**: Periodic reconciliation pass. Or optimize structural event processing to reduce latency.

### 2. Bidirectional: client receives almost no incoming events

At 50 struct/sec on both sides, the client only receives 8 incoming changes during the entire 30-second mutation phase. The server has 501 nodes, the client has 42. The client's outgoing works (published 3,443 changes) but incoming is nearly dead.

Root cause not fully identified. The deadlock is fixed (HandleRemoteAddNode no longer deadlocks on _structureLock). Echo suppression works (integration tests pass). But under high bidirectional load, the client's incoming event processing is starved.

Possible causes:
- The ModelChangeEvent subscription (`PublishingInterval = 1000ms`) may be too slow to keep up with 50 struct/sec
- The inline outgoing AddNodes calls in `WriteChangesAsync` may block the CQP, preventing subscription publish responses from being processed (the SDK publish thread may depend on the same session I/O thread)
- The unified incoming queue serializes value and structural processing. If structural processing is slow (browse + load + subscribe per event), values pile up

### 3. Chaos profiles not tested

The full stress profile (`opcua-structural`) with disconnects and reconnects has not been tested.
