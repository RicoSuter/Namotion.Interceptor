# Client-side structural sync improvements

Branch: `feature/opcua-bidirectional-structural-sync`
Base: `feature/opcua-dynamic-discovery-improvements`
PR: https://github.com/RicoSuter/Namotion.Interceptor/pull/294

## Context

Server-only structural sync nearly works (0 value diffs, 1-3 stale missing subjects at 20 struct/sec). Bidirectional is broken: client syncs ~39 out of ~500 subjects at 50 struct/sec each side. Root cause: no echo suppression on client. When the client sends `AddNodes` to the server, the server fires a `ModelChangeEvent` back. The client processes its own echo as an external event, creating duplicate subjects. At 50/sec this floods the processor.

## Design principles

1. **Outgoing = inline in CQP's `WriteChangesAsync`** (both server and client). Structural changes processed before values to guarantee ordering.
2. **Incoming = serialized through a queue** (client only; server handles incoming synchronously per OPC UA protocol). Values and structural events in the same queue for ordering.
3. **Echo suppression** via NodeId tracking on the client.

## Feature 1: Client outgoing inline + echo suppression

### Task 1.1: Move outgoing structural processing inline into WriteChangesAsync

**What**: In `OpcUaSubjectClientSource.WriteChangesAsync`, process structural changes inline before delegating value writes to `OutboundWriter`. This mirrors how the server does it in `OpcUaSubjectServer.ProcessStructuralChangesInline`.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
  - `WriteChangesAsync` (line 595): instead of `_structuralProcessor?.EnqueueStructuralChanges(changes.Span)`, iterate changes inline, extract subjects via `OpcUaStructuralChangeHelper`, compute diff, and for each added subject call `SendAddNodesToServerAsync`, for each removed call `SendDeleteNodesToServerAsync`. These methods move from the processor to here (or to a helper).
  - Store returned NodeIds from `AddNodes` responses in the echo set (Task 1.2).
  - Need `_session` reference (already available via `_sessionManager.CurrentSession`).

### Task 1.2: Add echo suppression to client structural processor

**What**: Add a `ConcurrentDictionary<NodeId, byte>` echo set. Populated after `AddNodes`/`DeleteNodes` in `WriteChangesAsync`. Checked in `ProcessExternalAddAsync`/`ProcessExternalRemove` before processing.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`
  - Add `_echoNodeIds` field (`ConcurrentDictionary<NodeId, byte>`)
  - Add `AddEcho(NodeId)`, `IsEcho(NodeId) -> bool` (TryRemove + return)
  - In `ProcessEventAsync` for external events: check `IsEcho(evt.AffectedNodeId)` before processing
  - Also check `_subjectMap.TryGetSubject(affectedNodeId, ...)` as idempotent fallback (already exists at line 203)
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
  - In inline outgoing (Task 1.1): after `AddNodes` response, call `_structuralProcessor.AddEcho(nodeId)`
  - After `DeleteNodes`, call `_structuralProcessor.AddEcho(nodeId)`

### Task 1.3: Clean up echo set on detach and reconnect

**What**: Prevent memory leaks in the echo set.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`
  - Add `ClearEchoes()` method
  - Add `RemoveEcho(NodeId)` method (for detach)
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
  - In `OnSubjectDetaching` (line 105): look up subject's NodeId from `subject.Data[SubjectNodeIdDataKey]`, call `RemoveEcho`
  - In `ReconnectSessionAsync` (line 452): call `_structuralProcessor.ClearEchoes()` before reconnection

### Task 1.4: Remove outgoing paths from structural processor and simplify base class

**What**: Now that outgoing is inline in `WriteChangesAsync`, remove the local (outgoing) paths from the processor and simplify the base class.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`
  - Remove `SendAddNodesToServerAsync` (lines 75-161) (logic moved to `OpcUaSubjectClientSource`)
  - Remove `SendDeleteNodesToServerAsync` (lines 163-193) (logic moved)
  - Simplify `ProcessEventAsync`: remove `IsLocal` branch, only handle external events
  - Remove `TryGetNodeIdForSubject` override (only needed by `EnqueueStructuralChanges` for outgoing)
- `src/Namotion.Interceptor.OpcUa/OpcUaStructuralChangeProcessor.cs`
  - Remove `EnqueueStructuralChanges` method (lines 34-59, moved to client source inline)
  - Remove `IsLocal` from `StructuralChangeEvent` record
  - Remove `TryGetNodeIdForSubject` abstract method
  - The base class becomes: Channel + ProcessLoopAsync + Enqueue + ProcessEventAsync abstract

### Task 1.5: Integration tests for echo suppression

**What**: Add tests that verify bidirectional structural changes work without duplicate subjects.

**File**: `src/Namotion.Interceptor.OpcUa.Tests/Integration/StructuralSyncTests.cs`

Tests to add:
- `WhenClientAddsCollectionItem_ThenServerDoesNotEcho`: Client adds item, verify client has exactly 1 new item (not duplicated by echo)
- `WhenBothSidesAddCollectionItemsSimultaneously_ThenBothConverge`: Server and client each add items, wait, verify both sides have all items
- `WhenClientAddsAndServerAddsRapidly_ThenClientConverges`: 10 items from each side at ~100ms intervals, verify convergence

### Task 1.6: Verify and update

- Run all integration tests (target: 55+ pass, 0 fail)
- Run ConnectorTester `opcua-structural-serveronly` profile
- Run ConnectorTester `opcua-structural-simple` profile
- Update `docs/design/investigations.md` with results

---

## Feature 2: Client incoming unified queue

### Task 2.1: Rename event type and add value variant

**What**: Replace `StructuralChangeEvent` with `IncomingEvent` that can represent both values and structural changes.

**Files**:
- `src/Namotion.Interceptor.OpcUa/OpcUaStructuralChangeProcessor.cs`
  - Rename `StructuralChangeEvent` to `IncomingEvent`
  - Add value fields: `RegisteredSubjectProperty? ValueProperty`, `object? Value`, `DateTimeOffset ValueTimestamp`, `DateTimeOffset? ValueReceivedTimestamp`
  - Add `IncomingEventType` enum: `StructuralAdd`, `StructuralRemove`, `Value`
  - Replace `StructuralChangeVerb` with `IncomingEventType`
  - Rename base class from `OpcUaStructuralChangeProcessor` to `OpcUaIncomingEventProcessor` (or keep if too much churn)

### Task 2.2: Route value notifications through the incoming queue

**What**: Instead of `OnFastDataChange` calling `_propertyWriter.Write` directly, enqueue value events to the structural processor's Channel.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/Connection/SubscriptionManager.cs`
  - `OnFastDataChange` (line 152): instead of calling `_propertyWriter.Write(...)`, call a callback/delegate that enqueues `IncomingEvent` with type `Value` for each monitored item notification
  - The callback is set by `OpcUaSubjectClientSource` when creating the `SubscriptionManager` (or the processor is passed directly)
  - Keep throughput counting (`_source.IncomingThroughput.Add`)
  - Keep the pooled changes pattern for efficiency, but route through the queue
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`
  - `ProcessEventAsync`: add handling for `IncomingEventType.Value` case: call `SetValueFromSource` on the property

### Task 2.3: Handle value events in the processor

**What**: The processor's `ProcessEventAsync` handles value events by applying them to properties via `SetValueFromSource`.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs`
  - In `ProcessEventAsync`, add a branch for `IncomingEventType.Value`:
    ```
    var value = _configuration.ValueConverter.ConvertToPropertyValue(evt.Value, evt.ValueProperty);
    evt.ValueProperty.SetValueFromSource(_clientSource, evt.ValueTimestamp, evt.ValueReceivedTimestamp, value);
    ```
  - Error handling: catch and log, don't stop the loop (already handled by base class)

### Task 2.4: Update ModelChangeEvent handler to use new event type

**What**: Update the `OpcUaModelChangeEventHandler` callback in `OpcUaSubjectClientSource` to create `IncomingEvent` with the new type.

**Files**:
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
  - In `StartListeningAsync` (line 154) and `ReconnectSessionAsync` (line 529): update the callback lambda to create `IncomingEvent` instead of `StructuralChangeEvent`

### Task 2.5: Integration tests for unified queue ordering

**What**: Test that values for newly added subjects are not lost.

**File**: `src/Namotion.Interceptor.OpcUa.Tests/Integration/StructuralSyncTests.cs`

Tests to add:
- `WhenServerAddsSubjectAndImmediatelyWritesValue_ThenClientSeesValueViaUnifiedQueue`: Server adds subject, immediately sets a value, client should see both subject and value (this may already exist as `WhenServerAddsSubjectAndImmediatelyMutatesValue_ThenClientSeesValue` but verify it passes reliably)

### Task 2.6: Verify and update

- Run all integration tests (target: all pass)
- Run ConnectorTester `opcua-structural-serveronly` profile
- Run ConnectorTester `opcua-structural-simple` profile
- Update `docs/design/investigations.md` with results

---

## Task 3: Documentation and PR update

### Task 3.1: Update OPC UA docs

**Files**:
- `docs/connectors-opcua-client.md`: Update Internal Design section to document unified incoming queue, inline outgoing, echo suppression
- `docs/connectors-opcua-server.md`: Minor updates if needed (verify accuracy of existing content)
- `docs/design/investigations.md`: Final state with all ConnectorTester results

### Task 3.2: Update PR #294

- Update title and description to reflect the full scope of changes
- PR: https://github.com/RicoSuter/Namotion.Interceptor/pull/294

---

## Execution order

1. Tasks 1.1-1.4 (inline outgoing + echo suppression + cleanup)
2. Task 1.5 (integration tests for Feature 1)
3. Task 1.6 (verify Feature 1: tests + ConnectorTester + update investigations.md)
4. Tasks 2.1-2.4 (unified incoming queue)
5. Task 2.5 (integration tests for Feature 2)
6. Task 2.6 (verify Feature 2: tests + ConnectorTester + update investigations.md)
7. Tasks 3.1-3.2 (docs + PR update)

## What stays unchanged

- Server-side code: `OpcUaSubjectServer`, `CustomNodeManager`, `OpcUaStandardServer`
- `ConnectorSubjectMap`, `OpcUaStructuralChangeHelper`
- `OpcUaModelChangeEventHandler` (only callback wiring changes, not handler logic)
- `OutboundWriter` (value write path unchanged)
- All existing integration tests (must continue passing)
