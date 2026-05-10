# Investigations

Current state and open issues for OPC UA structural synchronization.

This branch (`feature/opcua-bidirectional-structural-sync`) is a rewrite of the original structural mutations PR (#121). That PR used `GraphChangeSender`/`GraphChangeReceiver`/`GraphChangeDispatcher`/`GraphChangeApplier` base classes with ~3,200 lines. This rewrite uses a symmetric design: both server and client process outgoing structural changes inline in `WriteChangesAsync` before values, and the client routes all incoming events (values + structural) through a unified `Channel<IncomingEvent>` queue.

## Current State (2026-05-11)

### ConnectorTester Profile Results

| Profile | Struct Rate | Description | Status |
|---------|------------|-------------|--------|
| `opcua-structural-nosync` | 0 / 0 | Value-only baseline | **Converges perfectly** (runs indefinitely) |
| `opcua-structural-serveronly` | Server: 20/s, Client: 0 | Server mutates structure | **Nearly works**: 470/469 nodes, 1 value diff |
| `opcua-structural-clientonly` | Server: 0, Client: 20/s | Client mutates structure | **30 value diffs** on 31 common nodes (structural correct, values stale) |
| `opcua-structural-simple` | Server: 50/s, Client: 50/s | Both sides mutate | **Not retested** (blocked by client-only value issue) |
| `opcua-structural` | + chaos | Full stress with disconnects | **Not tested** |

### Integration Tests

62 pass, 0 skipped, 0 failures. Includes bidirectional structural mutations, echo suppression, client-originated value sync in both directions, rapid adds, and source ownership verification.

## Architecture

### Outgoing (local changes to protocol)

Both server and client process outgoing structural changes inline in `WriteChangesAsync`, before value writes. The CQP change is used as a trigger; the actual subjects are diffed via `OpcUaStructuralChangeHelper`.

- **Server**: `ProcessStructuralChangesInline` creates/removes OPC UA nodes and fires `ModelChangeEvent`s. Local, no network calls.
- **Client**: `ProcessOutgoingStructuralChangesAsync` sends `AddNodes`/`DeleteNodes` to the server. Network calls block the CQP flush (this causes the stale value issue, see Open Issues).

After AddNodes returns, the client immediately sets up subscriptions for the new subject inline (LoadSubjectAsync + AddMonitoredItemsAsync). The echo is stored in a `ConcurrentDictionary<NodeId, byte>`.

### Incoming (protocol to local)

- **Server**: Synchronous. Client writes arrive via OPC UA SDK (`StateChanged` callback). `AddNodes`/`DeleteNodes` handled in `HandleRemoteAddNode`/`HandleRemoteDeleteNode`. Required by OPC UA protocol (must return NodeId in response).
- **Client**: Unified `Channel<IncomingEvent>` queue. Both value notifications (`OnFastDataChange`) and structural events (`ModelChangeEvent`) are enqueued and processed in arrival order by `OpcUaClientIncomingEventProcessor`. When an echo is detected (NodeId in echo set), structural adds still trigger subscription setup as a safety net.

### CQP Lifecycle

`SubjectSourceBase.ExecuteAsync` runs:
1. `StartListeningAsync` (connect only, fast)
2. CQP created (subscription starts capturing changes)
3. `ProcessAsync` started concurrently (dequeue + flush loop)
4. `LoadInitialStateAndResumeAsync` (browse, claim, subscribe, read values) runs concurrently with step 3
5. `OnChangeProcessorStartedAsync` (post-load reconciliation)

Steps 3 and 4 run concurrently: as the loader progressively claims properties during browse, the CQP starts capturing changes to those properties. Mutations from other hosted services (e.g., ConnectorTester's MutationEngine) are captured as soon as the target property is claimed.

### Post-load Reconciliation

`OnChangeProcessorStartedAsync` walks the subject graph after loading. For each structural property that has been claimed (has source or OpcUaNodeIdKey), it checks children for `SubjectNodeIdDataKey`. Subjects without a NodeId were created locally during loading and never sent to the server. AddNodes + subscription setup is triggered for them.

## Fixes Applied (this branch)

1. **Unique NodeIds** (`CustomNodeManager._dynamicNodeCounter`): Monotonic counter for dynamically created NodeIds. Prevents reuse when subjects are replaced.
2. **Inline server structural processing**: `ProcessStructuralChangesInline` in `WriteChangesAsync` before values. Removed `OpcUaServerStructuralChangeProcessor`.
3. **Inline client structural processing**: `ProcessOutgoingStructuralChangesAsync` sends AddNodes/DeleteNodes before value writes. Symmetric with server.
4. **Echo suppression**: `ConcurrentDictionary<NodeId, byte>` populated after AddNodes/DeleteNodes. Checked on incoming `ModelChangeEvent`. Cleaned on detach and reconnect.
5. **Unified incoming queue**: `Channel<IncomingEvent>` with `IncomingEventType.Value`, `StructuralAdd`, `StructuralRemove`. Replaces separate `OnFastDataChange` direct apply + structural Channel.
6. **SemaphoreSlim deadlock fix**: `HandleRemoteAddNode`/`HandleRemoteDeleteNode` held `_structureLock` while calling `CreateDynamicSubjectNodes`/`RemoveSubjectNodes` (also acquire `_structureLock`). SemaphoreSlim is not reentrant. Fixed by releasing lock before node creation/removal.
7. **ExpandedNodeId storage fix**: `AddNodes` response returns `ExpandedNodeId`. Code elsewhere checks `obj is NodeId`. Now converts to `NodeId` before storing in `SubjectNodeIdDataKey`.
8. **Inline subscription setup after AddNodes**: After successful AddNodes, immediately browse server node, set OpcUaNodeIdKey on properties, create MonitoredItems, and subscribe. No need to wait for ModelChangeEvent echo.
9. **SubjectMap population after AddNodes**: Subject added to `ConnectorSubjectMap` after AddNodes so the echo handler can find it.
10. **Post-load structural reconciliation**: `OnChangeProcessorStartedAsync` in `SubjectSourceBase`. OPC UA client walks subject graph and sends AddNodes for locally-created subjects without NodeIds.
11. **CQP concurrent with loading**: `SubjectSourceBase` creates CQP before `LoadInitialStateAndResumeAsync` and starts `ProcessAsync` concurrently. Properties claimed during loading are immediately captured by the CQP.
12. **Browse moved to LoadInitialStateAsync**: `StartListeningAsync` only establishes the OPC UA session. The heavy recursive browse, property claiming, subscription creation, and structural processor setup moved to `LoadInitialStateAsync`.
13. **Filter failed monitored items**: `AddMonitoredItemsAsync` calls `FilterOutFailedMonitoredItemsAsync` for dynamically added items.
14. **Path-based snapshot comparison**: `VerificationEngine.CreateSnapshot` uses position paths (`ROOT/Collection[2]/Items[key]`) instead of sequential IDs.
15. **12 integration tests**: Echo suppression, bidirectional convergence, rapid adds, client-originated value sync (both directions), source ownership verification, client rapid structural adds.

## Ruled Out

- **Throughput**: Structural events process in 1ms median, not a bottleneck.
- **SemaphoreSlim for incoming serialization**: Blocking the SDK callback thread worsens diffs (365 vs 12).
- **Removing ReadInitialValuesAsync**: 1385 diffs without it. Initial notifications unreliable for dynamic items.
- **Dropped notifications**: Only 2 unmatched `ClientHandle` warnings. Subscriptions deliver correctly.
- **Lifecycle ownership leak**: Collection rebuilds do NOT detach surviving subjects. The lifecycle uses reference-counted property sets. Only removed subjects are detached. Verified by tracing `LifecycleInterceptor.WriteProperty` diff logic.
- **CQP-before-load without concurrent ProcessAsync**: CQP subscription queues changes but `ProcessAsync` (dequeue + flush loop) must run concurrently for changes to actually be processed during loading. Without concurrent ProcessAsync, changes accumulate in the subscription queue and are never dequeued.
- **Reading current property value at flush time** (instead of buffered `change.GetNewValue()`): Tested. Improves ConnectorTester convergence (30 diffs down from 31) but breaks 4 integration tests that expect specific value ordering. The approach is architecturally sound for eventual consistency (a stale write is followed by the correct value) but needs test adjustments.

## Open Issues

### 1. Client-only: 30 value diffs (stale buffered values)

**Root cause identified.** When the client sends AddNodes (inline in `WriteChangesAsync`), the network call blocks the CQP flush. During this delay, incoming server notifications update local properties via `SetValueFromSource(clientSource)`. The CQP skips these (source matches). When the CQP finally flushes, it writes the buffered (now stale) value to the server, overwriting the fresher server value.

After mutations stop, no follow-up changes arrive to correct the stale write. Both sides freeze at different values.

**Evidence:**
- Value-only (nosync) converges perfectly: proves value sync works without structural mutations
- Server-only (1 value diff): server's structural processing is local (no network calls), so CQP flush is not delayed
- Client-only (30 value diffs): inline AddNodes blocks CQP flush, values close but not equal (e.g., 50.04 vs 49.13)
- Values differ on ALL 31 common nodes, suggesting a systematic timing issue, not random

**Possible fixes:**
1. Read current property value at flush time instead of buffered value. CQP change becomes a trigger, actual value read at write time. Tested: fixes convergence but breaks integration tests that assert specific values. Needs test updates.
2. Move AddNodes back to background Channel (loses ordering guarantee but unblocks CQP flush).
3. Add a periodic "push all owned values" operation that runs during convergence.
4. Use timestamps to detect stale writes: if the property's last-modified timestamp is newer than the change's timestamp, skip the write.

### 2. Server-only: 1 value diff, 1 missing subject

Nearly converged. The 1 missing subject is a stale event (server adds then removes before client can browse). The 1 value diff is timing-related.

**Potential fix**: Periodic reconciliation pass as safety net.

### 3. Chaos profiles not tested

The full stress profile with disconnects and reconnects has not been tested.

### 4. ConnectorTester structural extras (4 extra nodes on client)

In client-only mode, the client has 4 subjects that don't exist on the server. These are subjects whose AddNodes failed (parent was simultaneously deleted, or AddNodes returned an error). The client has them locally but the server doesn't. Without periodic reconciliation, these stay permanently out of sync.
