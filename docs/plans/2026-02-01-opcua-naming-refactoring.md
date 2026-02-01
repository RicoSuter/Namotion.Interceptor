# OPC UA Graph Sync Naming & Architecture Refactoring

**Date:** 2026-02-01
**Status:** Planning
**Branch:** feature/opc-ua-full-sync (PR #121)

## Overview

Clean up naming, reduce duplication, and establish clear architectural patterns for the OPC UA dynamic graph synchronization feature.

**Goals:**
- Clear, consistent naming that indicates direction (Receiver/Sender)
- Shared utilities in Connectors library (reusable by MQTT, etc.)
- Classes under 600 lines
- Use `ConnectorSubjectMapping<NodeId>` everywhere
- Eliminate duplicate NodeId ↔ Subject mappings

---

## Issues Found During Code Review

### Issue 1: `ConnectorSubjectMapping` missing method

**Problem:** The client receiver needs to remove by NodeId AND get the subject atomically:
```csharp
// Current code in receiver:
_nodeIdToSubject.Remove(nodeId, out deletedSubject);  // atomic remove + get
```

`ConnectorSubjectMapping` only has `Unregister(subject, out nodeId)` - unregister by SUBJECT, not by ExternalId.

**Solution:** Add method to `ConnectorSubjectMapping`:
```csharp
public bool TryUnregisterByExternalId(TExternalId externalId, out IInterceptorSubject? subject)
```

### Issue 2: Duplicate mappings between Source and Receiver

**Problem:**
- `OpcUaSubjectClientSource` has `_subjectMapping` (ConnectorSubjectMapping<NodeId>)
- `OpcUaClientGraphChangeReceiver` has its own `_nodeIdToSubject` dictionary

These are **duplicates** kept in sync manually via `RegisterSubjectNodeId()`.

**Solution:** Pass the shared `ConnectorSubjectMapping<NodeId>` from Source to Receiver instead of maintaining duplicates.

### Issue 3: Server uses O(n) lookup for NodeId → Subject

**Problem:** Server's receiver iterates all entries to find subject by NodeId:
```csharp
foreach (var (subject, nodeState) in _subjectRefCounter.GetAllEntries())
{
    if (nodeState.NodeId.Equals(nodeId))  // O(n) lookup!
        return subject;
}
```

**Solution:** Server should use BOTH:
- `ConnectorReferenceCounter<NodeState>` for ref counting + NodeState storage (keep)
- `ConnectorSubjectMapping<NodeId>` for fast O(1) bidirectional lookup (add)

### Issue 4: `GraphChangeApplier` needs source parameter

**Problem:** Model manipulation code passes `_source` for loop prevention:
```csharp
parentProperty.SetValueFromSource(_source, DateTimeOffset.UtcNow, null, null);
```

**Solution:** `GraphChangeApplier` methods need a `source` parameter:
```csharp
void AddToCollection(RegisteredSubjectProperty property, IInterceptorSubject subject,
                     object? source, object? index);
```

---

## Part 1: Connectors Library Changes

### 1.1 Rename `StructuralChangeProcessor` → `GraphChangePublisher`

| From | To |
|------|-----|
| `Connectors/StructuralChangeProcessor.cs` | `Connectors/GraphChangePublisher.cs` |

Update all references (OPC UA sender classes extend this).

### 1.2 Add `TryUnregisterByExternalId` to `ConnectorSubjectMapping`

Add new method for atomic lookup + unregister by external ID:
```csharp
/// <summary>
/// Unregisters by external ID and returns the subject.
/// Returns true if found and unregistered, false otherwise.
/// </summary>
public bool TryUnregisterByExternalId(TExternalId externalId, out IInterceptorSubject? subject)
{
    lock (_lock)
    {
        if (!_idToSubject.TryGetValue(externalId, out subject))
        {
            return false;
        }

        var entry = _subjectToId[subject];
        if (entry.RefCount == 1)
        {
            _subjectToId.Remove(subject);
            _idToSubject.Remove(externalId);
        }
        else
        {
            _subjectToId[subject] = (entry.Id, entry.RefCount - 1);
        }
        return true;
    }
}
```

### 1.3 Create `GraphChangeApplier`

New file: `Connectors/GraphChangeApplier.cs`

**Responsibility:** Apply external changes to the C# model (collections, dictionaries, references).

**Methods:**
```csharp
public class GraphChangeApplier
{
    void AddToCollection(RegisteredSubjectProperty property, IInterceptorSubject subject,
                         object? source, object? index);
    void RemoveFromCollection(RegisteredSubjectProperty property, IInterceptorSubject subject,
                              object? source);
    void RemoveFromCollectionByIndex(RegisteredSubjectProperty property, int index,
                                     object? source);

    void AddToDictionary(RegisteredSubjectProperty property, object key,
                         IInterceptorSubject subject, object? source);
    void RemoveFromDictionary(RegisteredSubjectProperty property, object key,
                              object? source);

    void SetReference(RegisteredSubjectProperty property, IInterceptorSubject? subject,
                      object? source);
}
```

**Used by:** `OpcUaClientGraphChangeReceiver`, `OpcUaServerGraphChangeReceiver`

---

## Part 2: OPC UA Class Renames

### 2.1 Client Classes

| Current | New Name | Lines |
|---------|----------|-------|
| `Client/Graph/OpcUaGraphChangeProcessor` | `Client/OpcUaClientGraphChangeReceiver` | ~600* |
| `Client/OpcUaClientStructuralChangeProcessor` | `Client/OpcUaClientGraphChangeSender` | 587 |
| `Client/Graph/RemoteSyncManager` | `Client/OpcUaClientGraphChangeTrigger` | 278 |
| `Client/Graph/OpcUaPropertyWriter` | `Client/OpcUaClientPropertyWriter` | 221 |
| `Client/Graph/OpcUaRemoteChangeDispatcher` | **Merge into Receiver** | 126 |

*After extracting model manipulation to `GraphChangeApplier`

### 2.2 Server Classes

| Current | New Name | Lines |
|---------|----------|-------|
| `Server/Graph/OpcUaGraphChangeProcessor` | `Server/OpcUaServerGraphChangeReceiver` | 593 |
| `Server/OpcUaServerStructuralChangeProcessor` | `Server/OpcUaServerGraphChangeSender` | 45 |
| `Server/ExternalNodeManagementHelper` | `Server/OpcUaServerExternalNodeValidator` | 146 |
| `Server/Graph/OpcUaModelChangePublisher` | `Server/OpcUaServerModelChangePublisher` | 95 |
| `Server/Graph/OpcUaNodeCreator` | `Server/OpcUaServerNodeCreator` | 473 |

### 2.3 Shared Classes

| Current | New Name | Location |
|---------|----------|----------|
| `Graph/OpcUaBrowseHelper` | `OpcUaHelper` | Root (not in Graph/) |

---

## Part 3: Consolidate `ConnectorSubjectMapping<NodeId>` Usage

### 3.1 Client Side

**Current (duplicate mappings):**
- `OpcUaSubjectClientSource._subjectMapping` - ConnectorSubjectMapping<NodeId>
- `OpcUaClientGraphChangeReceiver._nodeIdToSubject` - Dictionary<NodeId, IInterceptorSubject>

**After (shared mapping):**
- Pass `ConnectorSubjectMapping<NodeId>` from Source to Receiver
- Remove `_nodeIdToSubject` dictionary from Receiver
- Receiver uses shared mapping via `TryGetSubject()` and `TryUnregisterByExternalId()`

### 3.2 Server Side

**Current:**
- `CustomNodeManager._subjectRefCounter` - ConnectorReferenceCounter<NodeState>
- `OpcUaServerGraphChangeReceiver._externallyAddedSubjects` - Dictionary<NodeId, IInterceptorSubject>
- O(n) lookup via `GetAllEntries()` iteration

**After:**
- Keep `ConnectorReferenceCounter<NodeState>` for ref counting + NodeState storage
- Add `ConnectorSubjectMapping<NodeId>` for O(1) bidirectional lookup
- Pass mapping to Receiver
- Remove `_externallyAddedSubjects` dictionary

---

## Part 4: Folder Structure

**Remove `Graph/` subfolders** - the word "Graph" in class names is sufficient.

**Before:**
```
OpcUa/
  Graph/
    OpcUaBrowseHelper.cs
  Client/
    Graph/
      OpcUaGraphChangeProcessor.cs
      OpcUaPropertyWriter.cs
      OpcUaRemoteChangeDispatcher.cs
      RemoteSyncManager.cs
    OpcUaClientStructuralChangeProcessor.cs
  Server/
    Graph/
      OpcUaGraphChangeProcessor.cs
      OpcUaModelChangePublisher.cs
      OpcUaNodeCreator.cs
    OpcUaServerStructuralChangeProcessor.cs
    ExternalNodeManagementHelper.cs
```

**After:**
```
OpcUa/
  OpcUaHelper.cs                              (moved from Graph/)
  Client/
    OpcUaClientGraphChangeReceiver.cs         (renamed + slimmed)
    OpcUaClientGraphChangeSender.cs           (renamed)
    OpcUaClientGraphChangeTrigger.cs          (renamed)
    OpcUaClientPropertyWriter.cs              (renamed)
  Server/
    OpcUaServerGraphChangeReceiver.cs         (renamed)
    OpcUaServerGraphChangeSender.cs           (renamed)
    OpcUaServerExternalNodeValidator.cs       (renamed)
    OpcUaServerModelChangePublisher.cs        (renamed)
    OpcUaServerNodeCreator.cs                 (renamed)
```

---

## Part 5: Architecture Diagram

```
                         ┌─────────────────────────────────────┐
                         │        C# Object Model (Graph)      │
                         └─────────────────────────────────────┘
                                   ▲                 │
                                   │                 │
                    ┌──────────────┴──┐           ┌──┴──────────────┐
                    │  GraphChange-   │           │  GraphChange-   │
                    │  Applier        │           │  Publisher      │
                    │  (Connectors)   │           │  (Connectors)   │
                    └──────────────┬──┘           └──┬──────────────┘
                                   │                 │
              ┌────────────────────┼─────────────────┼────────────────────┐
              │                    │     OPC UA      │                    │
              │    ┌───────────────┴──┐           ┌──┴───────────────┐    │
              │    │ Client/Server    │           │ Client/Server    │    │
              │    │ GraphChange-     │           │ GraphChange-     │    │
              │    │ Receiver         │           │ Sender           │    │
              │    └───────────────┬──┘           └──┬───────────────┘    │
              │                    │                 │                    │
              └────────────────────┼─────────────────┼────────────────────┘
                                   │                 │
                                   ▼                 ▼
                         ┌─────────────────────────────────────┐
                         │        OPC UA Address Space         │
                         └─────────────────────────────────────┘
```

---

## Part 6: Execution Steps

### Phase 1: Connectors Library
- [ ] 1. Rename `StructuralChangeProcessor` → `GraphChangePublisher`
- [ ] 2. Add `TryUnregisterByExternalId` method to `ConnectorSubjectMapping`
- [ ] 3. Create `GraphChangeApplier` class (with `source` parameter on all methods)
- [ ] 4. Add tests for new `ConnectorSubjectMapping` method
- [ ] 5. Add tests for `GraphChangeApplier`

### Phase 2: OPC UA Renames (no logic changes)
- [ ] 6. Rename client classes (see table 2.1)
- [ ] 7. Rename server classes (see table 2.2)
- [ ] 8. Move `OpcUaBrowseHelper` → `OpcUaHelper`
- [ ] 9. Remove empty `Graph/` folders
- [ ] 10. Update all `using` statements and references

### Phase 3: Extract to `GraphChangeApplier`
- [ ] 11. Extract model manipulation from `OpcUaClientGraphChangeReceiver` to use `GraphChangeApplier`
- [ ] 12. Extract model manipulation from `OpcUaServerGraphChangeReceiver` to use `GraphChangeApplier`
- [ ] 13. Merge `OpcUaRemoteChangeDispatcher` into `OpcUaClientGraphChangeReceiver`

### Phase 4: Consolidate `ConnectorSubjectMapping<NodeId>`
- [ ] 14. Client: Pass shared mapping from Source to Receiver, remove duplicate `_nodeIdToSubject`
- [ ] 15. Server: Add `ConnectorSubjectMapping<NodeId>` to CustomNodeManager, pass to Receiver
- [ ] 16. Server: Remove `_externallyAddedSubjects` dictionary, use shared mapping

### Phase 5: Verify
- [ ] 17. Run all tests: `dotnet test src/Namotion.Interceptor.OpcUa.Tests`
- [ ] 18. Verify line counts are under 600
- [ ] 19. Run full solution tests: `dotnet test src/Namotion.Interceptor.slnx`

---

## Expected Final Line Counts

| Class | Before | After |
|-------|--------|-------|
| `OpcUaClientGraphChangeReceiver` | 1216 | ~500 |
| `OpcUaClientGraphChangeSender` | 587 | ~550 |
| `OpcUaServerGraphChangeReceiver` | 593 | ~450 |
| `GraphChangeApplier` (new) | - | ~200 |
| `GraphChangePublisher` (renamed) | 115 | ~115 |

---

## Summary of All Renames

| Current Name | New Name |
|--------------|----------|
| `StructuralChangeProcessor` | `GraphChangePublisher` |
| `Client/Graph/OpcUaGraphChangeProcessor` | `OpcUaClientGraphChangeReceiver` |
| `Server/Graph/OpcUaGraphChangeProcessor` | `OpcUaServerGraphChangeReceiver` |
| `OpcUaClientStructuralChangeProcessor` | `OpcUaClientGraphChangeSender` |
| `OpcUaServerStructuralChangeProcessor` | `OpcUaServerGraphChangeSender` |
| `RemoteSyncManager` | `OpcUaClientGraphChangeTrigger` |
| `ExternalNodeManagementHelper` | `OpcUaServerExternalNodeValidator` |
| `OpcUaBrowseHelper` | `OpcUaHelper` |
| `OpcUaPropertyWriter` | `OpcUaClientPropertyWriter` |
| `OpcUaModelChangePublisher` | `OpcUaServerModelChangePublisher` |
| `OpcUaNodeCreator` | `OpcUaServerNodeCreator` |

---

## New Classes with Good Names (No Rename Needed)

| Class | Purpose |
|-------|---------|
| `ConnectorReferenceCounter<T>` | Reference counting with connector-specific data |
| `ConnectorSubjectMapping<T>` | Bidirectional Subject ↔ ExternalId mapping |
| `CollectionNodeStructure` | Enum for flat vs container collection structure |
| `OpcUaTypeRegistry` | Maps C# types ↔ OPC UA TypeDefinition NodeIds |

---

## Verification Commands

After each phase:
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

Final verification:
```bash
dotnet test src/Namotion.Interceptor.slnx -v minimal
```
