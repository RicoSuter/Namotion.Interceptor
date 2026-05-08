# Bidirectional OPC UA Structural Sync

## Overview

Enable runtime structural synchronization between C# model and OPC UA address space. When subjects are added/removed from collections, dictionaries, or references at runtime, OPC UA nodes are created/removed and connected clients are notified.

## Branch

New branch off `feature/opcua-dynamic-discovery-improvements` (which already improved dynamic property discovery and NodeId-based subject dedup in the loader).

## Design Principles

- Extend existing classes, no coordinator/strategy/actor patterns
- Two small trigger classes (push + poll), independent of each other
- ConnectorSubjectMap for reverse lookup + ref counting with auto-cleanup
- Subject.Data for NodeId storage (easy access from user code)
- Reuse existing loader browse logic for reconciliation
- Loop prevention via change.Source

---

## Task 1: ConnectorSubjectMap<TExternalId>

**Goal:** Reusable reverse-index map with ref counting and lifecycle-based auto-cleanup.

**File:** `src/Namotion.Interceptor.Connectors/ConnectorSubjectMap.cs` (new)

**Design:**
- Single-direction map: externalId -> subject
- Ref counting per subject (for shared references, e.g. nameplate spec)
- Auto-cleanup: hooks LifecycleInterceptor.SubjectDetached, decrements ref count, removes entry at zero
- Thread-safe via ConcurrentDictionary

**API:**
```csharp
public class ConnectorSubjectMap<TExternalId> : IDisposable
    where TExternalId : notnull
{
    public ConnectorSubjectMap(IInterceptorSubjectContext context);

    // Register a mapping (increments ref count if subject already tracked)
    public void Add(TExternalId externalId, IInterceptorSubject subject);

    // Lookup by external ID
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject);

    // Manual removal (returns true when ref count hits zero and entry is removed)
    public bool Remove(TExternalId externalId);

    // All tracked external IDs
    public IEnumerable<TExternalId> ExternalIds { get; }

    public void Dispose(); // Unhook lifecycle events, clear maps
}
```

**Auto-cleanup logic in SubjectDetached handler:**
- Scan entries where value.Subject matches detached subject
- Decrement ref count; if zero, remove entry
- Use `ConcurrentDictionary<TExternalId, (IInterceptorSubject Subject, int RefCount)>`

**Test file:** `src/Namotion.Interceptor.Connectors.Tests/ConnectorSubjectMapTests.cs` (new)

**Tests:**
- WhenAddingMapping_ThenCanLookupByExternalId
- WhenAddingSameSubjectTwice_ThenRefCountIncrements
- WhenRemovingWithRefCountAboveOne_ThenEntryRemains
- WhenRemovingWithRefCountOne_ThenEntryIsRemoved
- WhenSubjectDetaches_ThenEntryIsAutoCleaned
- WhenSubjectDetachesWithMultipleRefs_ThenRefCountDecrements
- WhenDisposed_ThenAllEntriesCleared

**Verification:** `dotnet test src/Namotion.Interceptor.Connectors.Tests --filter "ConnectorSubjectMap"`

---

## Task 2: Refactor MQTT to use ConnectorSubjectMap

**Goal:** Validate the abstraction by replacing MQTT's O(n) topic-to-property cleanup.

**File:** `src/Namotion.Interceptor.Mqtt/Client/MqttSubjectClientSource.cs`

**Changes:**
- Replace `_topicToProperty` ConcurrentDictionary with `ConnectorSubjectMap<string>`
- Remove the manual O(n) `OnSubjectDetached` cleanup for topic mappings (map handles it)
- Keep `_propertyToTopic` as-is (that direction is property-keyed, not subject-keyed)

**Verification:** `dotnet test src/Namotion.Interceptor.Mqtt.Tests` (existing tests pass)

---

## Task 3: Server-side dynamic node creation on SubjectAttached

**Goal:** When a subject is attached to the model at runtime, create OPC UA nodes and fire ModelChangeEvent.

**Files to modify:**
- `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServerBackgroundService.cs`
- `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

**Changes to OpcUaSubjectServerBackgroundService:**
- Add `_lifecycleInterceptor.SubjectAttached += OnSubjectAttached;` alongside existing SubjectDetached hook (line 84)
- Implement `OnSubjectAttached`:
  ```csharp
  private void OnSubjectAttached(SubjectLifecycleChange change)
  {
      var nodeManager = _server?.GetNodeManager();
      if (nodeManager is null) return;

      var createdNode = nodeManager.CreateDynamicSubjectNodes(change.Subject);
      if (createdNode is not null && _configuration.EnableStructureSynchronization)
      {
          nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeAdded, createdNode.NodeId);
      }
  }
  ```
- Update `OnSubjectDetached` to also fire ModelChangeEvent:
  ```csharp
  private void OnSubjectDetached(SubjectLifecycleChange change)
  {
      var nodeManager = _server?.GetNodeManager();
      if (nodeManager is null) return;

      // Get NodeId before removal
      var registeredSubject = change.Subject.TryGetRegisteredSubject();
      NodeId? nodeId = null;
      if (registeredSubject is not null)
      {
          nodeManager.TryGetNodeId(registeredSubject, out nodeId);
      }

      _server?.RemoveSubjectNodes(change.Subject);

      if (nodeId is not null && _configuration.EnableStructureSynchronization)
      {
          nodeManager.FireModelChangeEvent(ModelChangeStructureVerbMask.NodeDeleted, nodeId);
      }
  }
  ```
- Unsubscribe from SubjectAttached in finally block (line 95)

**Changes to CustomNodeManager:**
- Add `FireModelChangeEvent` method:
  ```csharp
  public void FireModelChangeEvent(ModelChangeStructureVerbMask verb, NodeId affectedNodeId)
  {
      var context = SystemContext;
      var eventState = new GeneralModelChangeEventState(null);
      eventState.Initialize(context, null, EventSeverity.Low,
          new LocalizedText("Address space structure changed"));
      eventState.SetChildValue(context, BrowseNames.SourceNode, ObjectIds.Server, false);
      eventState.SetChildValue(context, BrowseNames.SourceName, "Server", false);
      eventState.SetChildValue(context, BrowseNames.Changes,
          new[] { new ModelChangeStructureDataType
              { Verb = (byte)verb, Affected = affectedNodeId,
                AffectedType = ObjectTypeIds.BaseObjectType } }, false);
      eventState.SetChildValue(context, BrowseNames.Time, DateTime.UtcNow, false);
      eventState.SetChildValue(context, BrowseNames.ReceiveTime, DateTime.UtcNow, false);
      Server.ReportEvent(eventState);
  }
  ```
- Add `TryGetNodeId` method to look up a subject's NodeId from `_subjects`:
  ```csharp
  public bool TryGetNodeId(RegisteredSubject subject, out NodeId? nodeId)
  {
      if (_subjects.TryGetValue(subject, out var node))
      {
          nodeId = node.NodeId;
          return true;
      }
      nodeId = null;
      return false;
  }
  ```

**Test file:** `src/Namotion.Interceptor.OpcUa.Tests/Server/DynamicNodeCreationTests.cs` (new, unit tests)

**Tests:**
- WhenSubjectAttached_ThenOpcUaNodesCreated
- WhenSubjectDetached_ThenOpcUaNodesRemoved
- WhenSubjectAttached_ThenModelChangeEventFired
- WhenSubjectDetached_ThenModelChangeEventFired
- WhenStructureSyncDisabled_ThenNoModelChangeEventFired

**Verification:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "DynamicNodeCreation"`

---

## Task 4: Client-side ModelChangeEvent subscription (push trigger)

**Goal:** Subscribe to server's GeneralModelChangeEvents and trigger reconciliation.

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaModelChangeEventHandler.cs` (new)

**Design:**
- Small class: subscribes to GeneralModelChangeEvent on the Server object
- When NodeAdded/NodeDeleted events received, calls a reconciliation callback
- Constructor takes Session, callback, ILogger
- Implements IDisposable for cleanup

```csharp
internal class OpcUaModelChangeEventHandler : IDisposable
{
    public OpcUaModelChangeEventHandler(
        Session session,
        Func<ModelChangeStructureVerbMask, NodeId, CancellationToken, Task> onChangeDetected,
        ILogger logger);

    public void Subscribe();
    public void Dispose();
}
```

**Callback contract:** `onChangeDetected(verb, affectedNodeId, ct)` is called for each NodeAdded/NodeDeleted change. The handler does NOT know about subjects or reconciliation; it just forwards OPC UA events.

**Test file:** `src/Namotion.Interceptor.OpcUa.Tests/Client/OpcUaModelChangeEventHandlerTests.cs` (new)

**Tests:** (integration tests, Category=Integration)
- WhenServerFiresNodeAdded_ThenCallbackInvoked
- WhenServerFiresNodeDeleted_ThenCallbackInvoked
- WhenDisposed_ThenNoMoreCallbacks

**Verification:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "OpcUaModelChangeEventHandler"`

---

## Task 5: Client-side periodic re-browse (poll trigger)

**Goal:** Timer-based periodic reconciliation of known parent nodes.

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaPeriodicResyncHandler.cs` (new)

**Design:**
- Small class: runs a timer, calls reconciliation callback on each tick
- Constructor takes interval, callback, ILogger
- Implements IDisposable

```csharp
internal class OpcUaPeriodicResyncHandler : IDisposable
{
    public OpcUaPeriodicResyncHandler(
        TimeSpan interval,
        Func<CancellationToken, Task> onResyncRequested,
        ILogger logger);

    public void Start();
    public void Dispose();
}
```

**The callback triggers a full reconcile of the root subject's subtree.** The handler itself doesn't know about OPC UA; it just fires the callback on a timer.

**Test file:** Not needed for unit tests (trivial timer wrapper). Covered by integration tests in Task 7.

---

## Task 6: Client-side subtree reconciliation

**Goal:** Given a parent subject, re-browse its OPC UA children, diff with local state, create/remove subjects and monitored items.

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectLoader.cs` (modify)

**New method: `ReconcileSubtreeAsync`**

```csharp
public async Task ReconcileSubtreeAsync(
    IInterceptorSubject parentSubject,
    NodeId parentNodeId,
    ISession session,
    ConnectorSubjectMap<NodeId> subjectMap,
    SubscriptionManager subscriptionManager,
    CancellationToken cancellationToken)
```

**Logic:**
1. Browse `parentNodeId` children (reuse existing `BrowseNodeAsync`)
2. Get current local children from `parentSubject.TryGetRegisteredSubject().Properties`
3. For each remote child node:
   - If already in `subjectMap` -> skip (already synced)
   - If new -> create subject (via SubjectFactory), load properties, set up monitored items, register in map, store NodeId in subject.Data
4. For each local child not found in remote browse:
   - Detach subject, remove monitored items, remove from map
5. Recurse into child subjects for nested structural changes

**Subject.Data storage:** During both initial load and reconciliation:
```csharp
subject.SetData(OpcUaNodeIdDataKey, nodeId);
```
where `OpcUaNodeIdDataKey` is a well-known constant on `OpcUaSubjectClientSource`.

**Changes to existing `LoadSubjectAsync`:**
- After loading a subject, register it in the `ConnectorSubjectMap` passed down
- Store NodeId in subject.Data
- The existing `subjectsByNodeId` dictionary on this branch can be replaced by the `ConnectorSubjectMap`

**Test file:** `src/Namotion.Interceptor.OpcUa.Tests/Client/SubtreeReconciliationTests.cs` (new, unit tests with mocked session)

**Tests:**
- WhenRemoteNodeAdded_ThenLocalSubjectCreated
- WhenRemoteNodeRemoved_ThenLocalSubjectDetached
- WhenRemoteNodeUnchanged_ThenNoAction
- WhenNestedNodeAdded_ThenRecursivelyReconciled

**Verification:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "SubtreeReconciliation"`

---

## Task 7: OpcUaStructureHandler + wire into OpcUaSubjectClientSource

**Goal:** Extract client-side sync wiring into a small class to avoid bloating OpcUaSubjectClientSource.

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaStructureHandler.cs` (new)

**Design:**
- Owns: `ConnectorSubjectMap<NodeId>`, `OpcUaModelChangeEventHandler`, `OpcUaPeriodicResyncHandler`
- Created by `OpcUaSubjectClientSource` after initial load
- Calls `OpcUaSubjectLoader.ReconcileSubtreeAsync` when changes detected
- Implements `IDisposable`

```csharp
internal class OpcUaStructureHandler : IDisposable
{
    private readonly ConnectorSubjectMap<NodeId> _subjectMap;
    private readonly OpcUaSubjectLoader _loader;
    private readonly ILogger _logger;

    private OpcUaModelChangeEventHandler? _modelChangeEventHandler;
    private OpcUaPeriodicResyncHandler? _periodicResyncHandler;

    public OpcUaStructureHandler(
        ConnectorSubjectMap<NodeId> subjectMap,
        OpcUaSubjectLoader loader,
        OpcUaClientConfiguration configuration,
        ILogger logger);

    // Called after initial load to set up push/poll triggers
    public void Start(Session session, SubscriptionManager subscriptionManager);

    // Called on reconnect to refresh with new session
    public void OnReconnected(Session session);

    public void Dispose();
}
```

**Changes to OpcUaSubjectClientSource:**
- Add single field: `private OpcUaStructureHandler? _structureHandler;`
- In `StartListeningAsync` (after initial load): create and start `OpcUaStructureHandler` (~5 lines)
- In `DisposeAsync`: dispose it (~2 lines)
- In `ReconnectSessionAsync`: call `OnReconnected` (~2 lines)
- Total: ~10 lines added to the existing class

**Verification:** `dotnet build src/Namotion.Interceptor.slnx`

---

## Task 8: Loop prevention for bidirectional sync

**Goal:** Prevent infinite sync loops when both client and server react to each other's changes.

**Mechanism:** Use `change.Source` to tag the origin of structural changes.

**Changes:**
- Server-side: When `OnSubjectAttached`/`OnSubjectDetached` fires, check if the change originated from an OPC UA client write. If so, skip creating/removing nodes (they already exist on the remote side).
  - The server's `OpcUaSubjectServerBackgroundService` is already the source for `UpdateProperty` calls (line 232). Use the same instance as source tag.
- Client-side: When reconciliation creates/removes subjects, use `_clientSource` as the source via `SetValueFromSource`. When `SubjectAttached` fires from OPC UA-originated changes, the source will be `_clientSource`, so the server won't echo them back.

**Where this lives:**
- In `OnSubjectAttached`/`OnSubjectDetached` handlers on both sides, check `change.Source`:
  ```csharp
  // Skip if this change originated from our own sync
  if (change.Source == this) return;
  ```

**Test file:** Part of integration tests in Task 9.

---

## Task 9: Integration tests (port early, expect failures)

**Goal:** Port test scenarios from PR #121 as failing tests that define "done." Written in Batch 2, before the implementation is complete.

**File:** `src/Namotion.Interceptor.OpcUa.Tests/Integration/StructuralSyncTests.cs` (new)

**Test infrastructure:** Reuse existing `OpcUaTestServer` and `OpcUaTestClient` from `Integration/Testing/`. May need to extend `SharedTestModel` with collection/dictionary properties if not already present.

**Test scenarios (all Category=Integration):**

Server -> Client:
- WhenServerAddsCollectionItem_ThenClientSeesNewSubject
- WhenServerRemovesCollectionItem_ThenClientSubjectDetaches
- WhenServerAddsDictionaryEntry_ThenClientSeesNewEntry
- WhenServerRemovesDictionaryEntry_ThenClientEntryDetaches
- WhenServerSetsReference_ThenClientSeesNewSubject
- WhenServerClearsReference_ThenClientSubjectDetaches

Client -> Server:
- WhenClientAddsCollectionItem_ThenServerCreatesNodes
- WhenClientRemovesCollectionItem_ThenServerRemovesNodes

Periodic resync:
- WhenPeriodicResyncEnabled_ThenChangesDetectedWithoutEvents

Loop prevention:
- WhenServerMutates_ThenNoInfiniteLoop
- WhenClientMutates_ThenNoInfiniteLoop

**Source:** Adapt scenarios from PR #121's test files:
- `OpcUaBidirectionalSyncE2ETests.cs`
- `OpcUaAddressSpaceSyncTests.cs`
- `OpcUaSyncStrategyTests.cs`
- `OpcUaModelChangeEventTests.cs`

**Verification:** `dotnet test src/Namotion.Interceptor.OpcUa.Tests --filter "Category=Integration&StructuralSync"`

---

## Task 10: ConnectorTester chaos validation

**Goal:** Full chaos testing with structural mutations on BOTH server and client to validate eventual consistency for graph changes.

**What ConnectorTester already does:**
- `MutateCollection()` - add/remove collection items at runtime
- `MutateDictionary()` - add/remove dictionary entries at runtime
- `MutateObjectRef()` - set/clear object references at runtime
- Configurable `StructuralMutationRate` per participant
- Transaction support, max depth/size constraints

**What needs to happen:**
1. Configure OPC UA server participant with structural mutations enabled
2. Configure OPC UA client participant with structural mutations enabled
3. Both mutate structure concurrently (chaos)
4. Verify eventual consistency: after mutations settle, server and client models converge
5. Verify no memory leaks: subjects created during mutations are properly cleaned up
6. Verify no infinite loops: bidirectional sync doesn't create runaway propagation

**Changes:**
- Enable `EnableStructureSynchronization` in ConnectorTester's OPC UA client/server configs
- May need a convergence check after mutations: compare server and client subject graphs
- Run with ConnectorTester's existing load testing harness

**Verification:** Run ConnectorTester with structural mutations on both sides, verify convergence and no crashes/leaks.

---

## Execution Order

Tests first: port PR #121's integration test scenarios as failing tests early to define "done." Implement until they pass. ConnectorTester chaos validation at the end.

Suggested batch order:

1. **Batch 1** (parallel): Tasks 1, 3 (ConnectorSubjectMap + server-side node creation)
2. **Batch 2**: Task 9 - Port integration tests from PR #121 (they will fail, that's the point)
3. **Batch 3** (parallel): Tasks 2, 4, 5 (MQTT refactor + both triggers)
4. **Batch 4**: Tasks 6, 8 (reconciler + loop prevention)
5. **Batch 5**: Task 7 (OpcUaStructureHandler + wire into client source - integration tests should start passing)
6. **Batch 6**: Task 10 (ConnectorTester chaos validation with structural mutations on both server and client)

## Config Properties (Need to be added)

`OpcUaConfigurationBase` does NOT exist on this branch. Neither do `EnableStructureSynchronization` or `EnablePeriodicResynchronization`. These config properties must be added as part of Task 3:

- Either create `OpcUaConfigurationBase` as a shared base class for client and server configs
- Or add the properties directly to both `OpcUaClientConfiguration` and `OpcUaServerConfiguration`

Properties needed:
- `EnableStructureSynchronization` (bool, default: true) - master toggle for structural sync
- `EnablePeriodicResynchronization` (bool, default: false) - opt-in periodic re-browse
- `PeriodicResynchronizationInterval` (TimeSpan, default: 30s) - poll interval
