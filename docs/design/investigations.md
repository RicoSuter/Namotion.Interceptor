# Investigations

Current state and open issues for OPC UA structural synchronization.

This branch (`feature/opcua-bidirectional-structural-sync`) is a rewrite of the original structural mutations PR (#121). That PR used a more complex architecture with `GraphChangeSender`/`GraphChangeReceiver`/`GraphChangeDispatcher`/`GraphChangeApplier` base classes and ~3,200 lines of production code. This rewrite is simpler at ~2,100 lines: the server processes structural changes inline in `WriteChangesAsync`, and the client uses a Channel queue for incoming `ModelChangeEvent`s.

## Current State (2026-05-10)

### ConnectorTester Profile Results

| Profile | Struct Rate | Description | Status |
|---------|------------|-------------|--------|
| `opcua-structural-serveronly` | Server: 20/s, Client: 0 | Server mutates structure, client observes | **Nearly works**: 0 value diffs, 1-3 missing subjects (stale events) |
| `opcua-structural-simple` | Server: 50/s, Client: 50/s | Both sides mutate structure | **Broken**: client syncs ~39 out of ~500 subjects |
| `opcua-structural` | Server: 200/s, Client: 200/s + chaos | Full stress with disconnects | **Not tested** |
| `opcua-structural-nosync` | No structural sync | Baseline value sync | **Not tested** |

### Integration Tests

55 pass, 0 skipped, 0 failures (1 pre-existing flaky test: `WhenServerClearsReference_ThenClientSubjectDetaches`, port conflict in full suite).

## Fixes Applied (this branch)

1. **Unique NodeIds** (`CustomNodeManager._dynamicNodeCounter`): Collections, dictionaries, and references all use a monotonic counter for dynamically created NodeIds. Prevents NodeId reuse when subjects are replaced at the same position.
2. **Inline server structural processing**: Structural changes processed synchronously in `WriteChangesAsync` before the value loop. Removed `OpcUaServerStructuralChangeProcessor` class. Ensures OPC UA nodes exist before value writes.
3. **Filter failed monitored items**: `AddMonitoredItemsAsync` now calls `FilterOutFailedMonitoredItemsAsync` after `ApplyChangesAsync` for dynamically added items.
4. **Path-based snapshot comparison**: `VerificationEngine.CreateSnapshot` walks the graph by position (`ROOT/Collection[2]/Items[key]`) instead of sequential IDs. The old approach produced false diffs when subjects were missing.
5. **Warning logs for stale events**: `ProcessExternalAddAsync` logs when browse fails (node removed between ModelChangeEvent and browse).
6. **8 new integration tests**: Value sync after structural adds, rapid adds, add/remove convergence, concurrent mutations, NodeId collision, bidirectional values, and stress test.

## Ruled Out

- **Throughput**: Structural events process in 1ms median, not a bottleneck at 20/sec.
- **SemaphoreSlim for incoming serialization**: Blocking the SDK's publish callback thread causes more diffs (365 vs 12), not fewer.
- **Removing ReadInitialValuesAsync**: 1385 diffs without it. Initial notifications from `MonitoringMode.Reporting` are unreliable for dynamically added items.
- **Dropped notifications**: Only 2 unmatched `ClientHandle` warnings in a full run. Subscriptions deliver correctly.

## Open Issues

### 1. Server-only: stale events (1-3 missing subjects)

At 20 struct/sec, 1-3 deeply nested subjects are missing from the client after 2-minute convergence. Diagnostic logs show the server added a node, fired `NodeAdded`, then removed it before the client could browse. The client's `ProcessExternalAddAsync` silently fails (`browse parent null` or `not found in browse`).

**Potential fix**: Periodic reconciliation pass after the structural event queue drains. The `OpcUaPeriodicResyncHandler` from an earlier design could serve as a safety net.

### 2. Bidirectional: client only syncs ~39 out of ~500 subjects

At 50 struct/sec on both sides, the client barely discovers any dynamically added subjects. The root cause has not been fully investigated. Known contributing factors:
- The server's `CreateDynamicSubjectNodes` returns null for subjects already created by the `AddNodes` handler (expected, not a bug, downgraded to debug log).
- The client sends `AddNodes` for local structural changes. The server creates the subject and fires `NodeAdded`. The client receives its own `NodeAdded` event and tries to create the subject again.
- The current design lacks coordination between outgoing `AddNodes` requests and incoming `ModelChangeEvent` processing.

PR #121 had dedicated `GraphChangeSender`/`GraphChangeReceiver`/`GraphChangeDispatcher` classes with more sophisticated coordination. The current simple Channel-based approach works for server-only mutations but may need rethinking for bidirectional.

### 3. Chaos profiles not tested

The full stress profile (`opcua-structural`) with disconnects and reconnects has not been tested. This requires the structural sync to handle session reconnection, subscription transfer, and state reconciliation after gaps.
