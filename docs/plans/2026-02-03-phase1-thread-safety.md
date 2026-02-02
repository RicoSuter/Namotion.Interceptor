# Phase 1: Thread Safety - Implementation Plan

**Created:** 2026-02-03
**Status:** Ready for Implementation
**Branch:** `feature/opc-ua-full-sync`
**PR:** #121

---

## Overview

This phase addresses 6 critical race conditions in the OPC UA graph sync implementation by introducing a unified `SubjectConnectorRegistry` that eliminates fragmented state management.

### Problem Statement

Current architecture has multiple independent locks:
- `ConnectorReferenceCounter` has its own `Lock`
- `ConnectorSubjectMapping` has its own `Lock`
- Operations spanning both classes have race windows between lock acquisitions

### Solution

Merge into single `SubjectConnectorRegistry<TExternalId, TData>` with:
- One lock for all operations
- Atomic registration/unregistration
- Protected extension points for subclasses

---

## Design

### Class Hierarchy

```
SubjectConnectorRegistry<TExternalId, TData>     (Connectors layer - generic)
    │
    └── OpcUaClientSubjectRegistry               (OPC UA layer - adds recently-deleted)

SubjectConnectorRegistry<NodeId, NodeState>      (Server uses base directly)
```

### API Surface

```csharp
public class SubjectConnectorRegistry<TExternalId, TData>
    where TExternalId : notnull
{
    // === Protected for subclasses ===
    protected Lock Lock { get; }

    // === Registration ===
    public bool Register(
        IInterceptorSubject subject,
        TExternalId externalId,
        Func<TData> dataFactory,
        out TData data,
        out bool isFirstReference);
    protected virtual bool RegisterCore(...);

    // === Unregistration ===
    public bool Unregister(
        IInterceptorSubject subject,
        out TExternalId? externalId,
        out TData? data,
        out bool wasLastReference);
    protected virtual bool UnregisterCore(...);

    public bool UnregisterByExternalId(
        TExternalId externalId,
        out IInterceptorSubject? subject,
        out TData? data,
        out bool wasLastReference);
    protected virtual bool UnregisterByExternalIdCore(...);

    // === Lookups ===
    public bool TryGetExternalId(IInterceptorSubject subject, out TExternalId? externalId);
    public bool TryGetSubject(TExternalId externalId, out IInterceptorSubject? subject);
    public bool TryGetData(IInterceptorSubject subject, out TData? data);
    public bool IsRegistered(IInterceptorSubject subject);

    // === Modification ===
    public bool UpdateExternalId(IInterceptorSubject subject, TExternalId newExternalId);
    protected virtual bool UpdateExternalIdCore(...);

    public bool ModifyData(IInterceptorSubject subject, Action<TData> modifier);

    // === Enumeration ===
    public List<IInterceptorSubject> GetAllSubjects();
    public List<(IInterceptorSubject Subject, TExternalId ExternalId, TData Data)> GetAllEntries();

    // === Lifecycle ===
    public void Clear();
    protected virtual void ClearCore();
}
```

### OPC UA Client Subclass

```csharp
internal class OpcUaClientSubjectRegistry
    : SubjectConnectorRegistry<NodeId, List<MonitoredItem>>
{
    private readonly Dictionary<NodeId, DateTime> _recentlyDeleted = new();
    private readonly TimeSpan _recentlyDeletedExpiry = TimeSpan.FromSeconds(30);

    protected override bool UnregisterCore(...)
    {
        var result = base.UnregisterCore(...);
        if (result && wasLast && nodeId is not null)
        {
            _recentlyDeleted[nodeId] = DateTime.UtcNow;
        }
        return result;
    }

    public bool WasRecentlyDeleted(NodeId nodeId)
    {
        lock (Lock)
        {
            if (!_recentlyDeleted.TryGetValue(nodeId, out var deletedAt))
                return false;
            if (DateTime.UtcNow - deletedAt > _recentlyDeletedExpiry)
            {
                _recentlyDeleted.Remove(nodeId);
                return false;
            }
            return true;
        }
    }
}
```

---

## Implementation Steps

### Step 1: Create SubjectConnectorRegistry

**File:** `src/Namotion.Interceptor.Connectors/SubjectConnectorRegistry.cs`

- [ ] Create class with type parameters `<TExternalId, TData>`
- [ ] Implement internal storage (`Dictionary` for subjects, `Dictionary` for reverse lookup)
- [ ] Implement `Register` / `RegisterCore`
- [ ] Implement `Unregister` / `UnregisterCore`
- [ ] Implement `UnregisterByExternalId` / `UnregisterByExternalIdCore`
- [ ] Implement lookup methods (`TryGetExternalId`, `TryGetSubject`, `TryGetData`, `IsRegistered`)
- [ ] Implement `UpdateExternalId` / `UpdateExternalIdCore`
- [ ] Implement `ModifyData`
- [ ] Implement enumeration methods (`GetAllSubjects`, `GetAllEntries`)
- [ ] Implement `Clear` / `ClearCore`
- [ ] Add XML documentation

### Step 2: Create Unit Tests for SubjectConnectorRegistry

**File:** `src/Namotion.Interceptor.Connectors.Tests/SubjectConnectorRegistryTests.cs`

- [ ] Test `Register` first reference returns `isFirstReference = true`
- [ ] Test `Register` subsequent references increment count
- [ ] Test `Unregister` last reference returns `wasLastReference = true`
- [ ] Test `Unregister` with remaining references returns `wasLastReference = false`
- [ ] Test `UnregisterByExternalId` works correctly
- [ ] Test `UpdateExternalId` updates both directions
- [ ] Test `UpdateExternalId` rejects collision
- [ ] Test `ModifyData` executes inside lock
- [ ] Test `GetAllSubjects` returns snapshot
- [ ] Test `Clear` removes all entries
- [ ] Test thread safety with concurrent operations

### Step 3: Create OpcUaClientSubjectRegistry

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientSubjectRegistry.cs`

- [ ] Inherit from `SubjectConnectorRegistry<NodeId, List<MonitoredItem>>`
- [ ] Add `_recentlyDeleted` dictionary
- [ ] Override `UnregisterCore` to mark recently deleted
- [ ] Implement `WasRecentlyDeleted` with expiry logic
- [ ] Add unit tests

### Step 4: Update OpcUaSubjectClientSource

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`

- [ ] Replace `_subjectRefCounter` and `_subjectMapping` with `_subjectRegistry`
- [ ] Update `TrackSubject` to use `_subjectRegistry.Register()`
- [ ] Update `RemoveItemsForSubject` to use `_subjectRegistry.Unregister()`
- [ ] Update `AddMonitoredItemToSubject` to use `_subjectRegistry.ModifyData()`
- [ ] Update all `TryGetSubjectNodeId` calls
- [ ] Update all `IsSubjectTracked` calls
- [ ] Remove old fields

### Step 5: Update OpcUaClientGraphChangeReceiver

**File:** `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs`

- [ ] Update constructor to receive `OpcUaClientSubjectRegistry`
- [ ] Replace `_subjectMapping` usage with registry
- [ ] Replace `_recentlyDeletedNodeIds` with `_registry.WasRecentlyDeleted()`
- [ ] Remove `_recentlyDeletedLock` and related fields
- [ ] Resolve TODO at line 659 (investigate `_structureSemaphore` need)

### Step 6: Update Server Side

**File:** `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`

- [ ] Replace `_subjectRefCounter` and `_subjectMapping` with `SubjectConnectorRegistry<NodeId, NodeState>`
- [ ] Update all usages
- [ ] Add `_structureLock` protection to `ClearPropertyData`

**File:** `src/Namotion.Interceptor.OpcUa/Server/OpcUaSubjectServer.cs`

- [ ] Add `SemaphoreSlim` for serializing `AddNodes`/`DeleteNodes` requests

**File:** `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeSender.cs`

- [ ] Create atomic `RemoveSubjectNodesAndReindex` in `CustomNodeManager`
- [ ] Update `OnSubjectRemovedAsync` to use atomic method

### Step 7: Remove Old Classes

- [ ] Delete `ConnectorReferenceCounter.cs`
- [ ] Delete `ConnectorSubjectMapping.cs`
- [ ] Delete associated test files
- [ ] Update any remaining references

### Step 8: Final Validation

- [ ] Run full test suite
- [ ] Verify no thread safety TODOs remain
- [ ] Verify hot path performance unchanged (run benchmarks if available)

---

## Files Changed

### New Files
| File | Purpose |
|------|---------|
| `Connectors/SubjectConnectorRegistry.cs` | New unified registry |
| `Connectors.Tests/SubjectConnectorRegistryTests.cs` | Unit tests |
| `OpcUa/Client/OpcUaClientSubjectRegistry.cs` | Client subclass with recently-deleted |

### Modified Files
| File | Changes |
|------|---------|
| `OpcUa/Client/OpcUaSubjectClientSource.cs` | Use new registry |
| `OpcUa/Client/OpcUaClientGraphChangeReceiver.cs` | Use new registry, remove recently-deleted tracking |
| `OpcUa/Client/OpcUaSubjectLoader.cs` | Update registry calls |
| `OpcUa/Client/OpcUaClientGraphChangeSender.cs` | Update registry calls |
| `OpcUa/Server/CustomNodeManager.cs` | Use new registry, protect ClearPropertyData |
| `OpcUa/Server/OpcUaSubjectServer.cs` | Add request serialization |
| `OpcUa/Server/OpcUaServerGraphChangeSender.cs` | Use atomic remove+reindex |
| `OpcUa/Server/OpcUaServerGraphChangeReceiver.cs` | Update registry calls |
| `OpcUa/Server/OpcUaServerNodeCreator.cs` | Update registry calls |

### Deleted Files
| File | Reason |
|------|--------|
| `Connectors/ConnectorReferenceCounter.cs` | Merged into registry |
| `Connectors/ConnectorSubjectMapping.cs` | Merged into registry |
| `Connectors.Tests/ConnectorReferenceCounterTests.cs` | Replaced |
| `Connectors.Tests/ConnectorSubjectMappingTests.cs` | Replaced |

---

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Breaking existing behavior | Comprehensive test suite, run after each step |
| Performance regression | Hot path unchanged, verify with benchmarks |
| Merge conflicts | Coordinate with any parallel work on branch |
| Missing edge cases | Port all existing tests to new registry |

---

## Success Criteria

- [ ] All 6 race conditions eliminated
- [ ] All existing tests pass
- [ ] New unit tests for `SubjectConnectorRegistry` (>90% coverage)
- [ ] No TODO comments about thread safety
- [ ] Code compiles without warnings
- [ ] Hot path performance unchanged

---

## Notes

- The hot path (value read/write/updates) does NOT go through this registry
- Recently-deleted tracking stays OPC UA specific (in subclass)
- Server uses base registry directly (no subclass needed)
- All virtual `*Core` methods execute inside the lock
