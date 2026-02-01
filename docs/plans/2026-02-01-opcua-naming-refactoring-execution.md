# OPC UA Naming & Architecture Refactoring - Execution Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Clean up naming, reduce duplication, and establish clear architectural patterns for the OPC UA dynamic graph synchronization feature.

**Architecture:** Rename classes to indicate direction (Receiver/Sender), move shared utilities to Connectors library, extract model manipulation to `GraphChangeApplier`, and consolidate duplicate `NodeId <-> Subject` mappings.

**Tech Stack:** C# 13, .NET 9.0, OPC UA SDK, System.Reactive

---

## Phase 1: OPC UA Client Class Renames (No Logic Changes)

### Task 1.1: Rename Client Graph Processor

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Client/Graph/OpcUaGraphChangeProcessor.cs` -> `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs`

**Steps:**
1. Rename the file (move out of Graph/ folder)
2. Update namespace from `Namotion.Interceptor.OpcUa.Client.Graph` to `Namotion.Interceptor.OpcUa.Client`
3. Update class name from `OpcUaGraphChangeProcessor` to `OpcUaClientGraphChangeReceiver`
4. Update all references in:
   - `Client/Graph/RemoteSyncManager.cs`
   - `Client/OpcUaSubjectClientSource.cs`

---

### Task 1.2: Rename Client Structural Change Processor (Sender)

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientStructuralChangeProcessor.cs` -> `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeSender.cs`

**Steps:**
1. Rename the file
2. Update class name from `OpcUaClientStructuralChangeProcessor` to `OpcUaClientGraphChangeSender`
3. Update all references in `Client/OpcUaSubjectClientSource.cs`

---

### Task 1.3: Rename RemoteSyncManager (Trigger)

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Client/Graph/RemoteSyncManager.cs` -> `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeTrigger.cs`

**Steps:**
1. Rename the file (move out of Graph/ folder)
2. Update namespace from `Namotion.Interceptor.OpcUa.Client.Graph` to `Namotion.Interceptor.OpcUa.Client`
3. Update class name from `RemoteSyncManager` to `OpcUaClientGraphChangeTrigger`
4. Update all references in `Client/OpcUaSubjectClientSource.cs`

---

### Task 1.4: Rename OpcUaPropertyWriter

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Client/Graph/OpcUaPropertyWriter.cs` -> `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientPropertyWriter.cs`

**Steps:**
1. Rename the file (move out of Graph/ folder)
2. Update namespace from `Namotion.Interceptor.OpcUa.Client.Graph` to `Namotion.Interceptor.OpcUa.Client`
3. Update class name from `OpcUaPropertyWriter` to `OpcUaClientPropertyWriter`
4. Update all references in `Client/OpcUaSubjectClientSource.cs`

---

### Task 1.5: Run Tests After Client Renames

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - All client class renames complete. Verify tests pass before proceeding to server renames.

---

## Phase 2: OPC UA Server Class Renames (No Logic Changes)

### Task 2.1: Rename Server Graph Processor

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Server/Graph/OpcUaGraphChangeProcessor.cs` -> `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeReceiver.cs`

**Steps:**
1. Rename the file (move out of Graph/ folder)
2. Update namespace from `Namotion.Interceptor.OpcUa.Server.Graph` to `Namotion.Interceptor.OpcUa.Server`
3. Update class name from `OpcUaGraphChangeProcessor` to `OpcUaServerGraphChangeReceiver`
4. Update all references in `Server/CustomNodeManager.cs`

---

### Task 2.2: Rename Server Structural Change Processor (Sender)

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerStructuralChangeProcessor.cs` -> `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeSender.cs`

**Steps:**
1. Rename the file
2. Update class name from `OpcUaServerStructuralChangeProcessor` to `OpcUaServerGraphChangeSender`
3. Update all references in `Server/OpcUaSubjectServerBackgroundService.cs`

---

### Task 2.3: Rename ExternalNodeManagementHelper

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Server/ExternalNodeManagementHelper.cs` -> `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerExternalNodeValidator.cs`

**Steps:**
1. Rename the file
2. Update class name from `ExternalNodeManagementHelper` to `OpcUaServerExternalNodeValidator`
3. Update all references in:
   - `Server/OpcUaServerGraphChangeReceiver.cs` (already renamed)
   - `Server/CustomNodeManager.cs`

---

### Task 2.4: Rename Server Graph Helper Classes

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Server/Graph/OpcUaModelChangePublisher.cs` -> `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerModelChangePublisher.cs`
- Rename: `src/Namotion.Interceptor.OpcUa/Server/Graph/OpcUaNodeCreator.cs` -> `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs`

**Steps:**
1. Rename OpcUaModelChangePublisher, update namespace and class name
2. Rename OpcUaNodeCreator, update namespace and class name
3. Update all references in `Server/CustomNodeManager.cs`

---

### Task 2.5: Run Tests After Server Renames

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - All server class renames complete. Verify tests pass before proceeding.

---

## Phase 3: Shared Class Renames & Folder Cleanup

### Task 3.1: Rename OpcUaBrowseHelper to OpcUaHelper

**Files:**
- Rename: `src/Namotion.Interceptor.OpcUa/Graph/OpcUaBrowseHelper.cs` -> `src/Namotion.Interceptor.OpcUa/OpcUaHelper.cs`

**Steps:**
1. Rename and move file to root of OpcUa project
2. Update namespace from `Namotion.Interceptor.OpcUa.Graph` to `Namotion.Interceptor.OpcUa`
3. Update class name from `OpcUaBrowseHelper` to `OpcUaHelper`
4. Update all references across Client/ and Server/ folders

---

### Task 3.2: Remove Empty Graph Folders

**Steps:**
1. Delete empty `Client/Graph/` folder
2. Delete empty `Server/Graph/` folder
3. Delete empty `Graph/` folder

---

### Task 3.3: Update Test File Names and Namespaces

**Files:**
- Rename test files to match new class names
- Update namespaces in test files

---

### Task 3.4: Run All Tests After Renames

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - All Phase 1-3 renames complete. All files moved out of Graph/ folders. All tests should pass.

---

## Phase 4: Connectors Library Changes

### Task 4.1: Rename StructuralChangeProcessor to GraphChangePublisher

**Files:**
- Rename: `src/Namotion.Interceptor.Connectors/StructuralChangeProcessor.cs` -> `src/Namotion.Interceptor.Connectors/GraphChangePublisher.cs`

**Steps:**
1. Rename the file
2. Update class name from `StructuralChangeProcessor` to `GraphChangePublisher`
3. Update all derived classes to use new name:
   - `OpcUaClientGraphChangeSender`
   - `OpcUaServerGraphChangeSender`
4. Update test files

---

### Task 4.2: Add TryUnregisterByExternalId to ConnectorSubjectMapping

**Files:**
- Modify: `src/Namotion.Interceptor.Connectors/ConnectorSubjectMapping.cs`
- Add test: `src/Namotion.Interceptor.Connectors.Tests/ConnectorSubjectMappingTests.cs`

**Steps:**
1. Add the new method:
```csharp
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
2. Add tests for the new method
3. Run tests

---

### Task 4.3: Run All Connectors Tests

```bash
dotnet test src/Namotion.Interceptor.Connectors.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - Phase 4 Connectors changes complete. All tests should pass.

---

## Phase 5: Merge OpcUaRemoteChangeDispatcher into Receiver

### Task 5.1: Merge Dispatcher into OpcUaClientGraphChangeReceiver

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs`
- Delete: `src/Namotion.Interceptor.OpcUa/Client/OpcUaRemoteChangeDispatcher.cs`

**Steps:**
1. Move dispatcher logic (Channel-based actor pattern) into the receiver class
2. Update `OpcUaClientGraphChangeTrigger` to use the merged receiver directly
3. Delete the standalone dispatcher file
4. Update/delete dispatcher tests

---

### Task 5.2: Run Tests After Merge

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - Phase 5 merge complete. OpcUaRemoteChangeDispatcher merged into OpcUaClientGraphChangeReceiver.

---

## Phase 6: Create GraphChangeApplier (Extract Model Manipulation)

### Task 6.1: Create GraphChangeApplier Class

**Files:**
- Create: `src/Namotion.Interceptor.Connectors/GraphChangeApplier.cs`
- Create: `src/Namotion.Interceptor.Connectors.Tests/GraphChangeApplierTests.cs`

**Steps:**
1. Create the class with methods (all include `source` parameter for loop prevention):
   - `AddToCollection`
   - `RemoveFromCollection`
   - `RemoveFromCollectionByIndex`
   - `AddToDictionary`
   - `RemoveFromDictionary`
   - `SetReference`
2. Add tests
3. Run tests

---

### Task 6.2: Extract Model Manipulation from Client Receiver

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs`

**Steps:**
1. Replace inline collection/dictionary/reference manipulation with `GraphChangeApplier` calls
2. Run tests

---

### Task 6.3: Extract Model Manipulation from Server Receiver

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeReceiver.cs`

**Steps:**
1. Replace inline collection/dictionary/reference manipulation with `GraphChangeApplier` calls
2. Run tests

---

### Task 6.4: Run All Tests After Extraction

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - Phase 6 GraphChangeApplier extraction complete.

---

## Phase 7: Consolidate ConnectorSubjectMapping Usage

### Task 7.1: Client - Share Mapping Between Source and Receiver

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaSubjectClientSource.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Client/OpcUaClientGraphChangeReceiver.cs`

**Current:**
- `OpcUaSubjectClientSource._subjectMapping` - ConnectorSubjectMapping<NodeId>
- `OpcUaClientGraphChangeReceiver._nodeIdToSubject` - Dictionary (duplicate!)

**After:**
- Pass shared `ConnectorSubjectMapping<NodeId>` from Source to Receiver
- Remove `_nodeIdToSubject` dictionary from Receiver

**Steps:**
1. Modify Receiver constructor to accept shared mapping
2. Replace all `_nodeIdToSubject` usages with mapping methods
3. Remove duplicate dictionary and registration methods
4. Run tests

---

### Task 7.2: Server - Add ConnectorSubjectMapping for O(1) Lookup

**Files:**
- Modify: `src/Namotion.Interceptor.OpcUa/Server/CustomNodeManager.cs`
- Modify: `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerGraphChangeReceiver.cs`

**Current:**
- O(n) lookup via `GetAllEntries()` iteration
- `_externallyAddedSubjects` dictionary (duplicate!)

**After:**
- Add `ConnectorSubjectMapping<NodeId>` for O(1) lookup
- Remove `_externallyAddedSubjects` dictionary

**Steps:**
1. Add `ConnectorSubjectMapping<NodeId>` to CustomNodeManager
2. Modify Receiver to use shared mapping
3. Replace O(n) lookups with O(1) mapping lookups
4. Remove `_externallyAddedSubjects` dictionary
5. Run tests

---

### Task 7.3: Run All Tests

```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - Phase 7 mapping consolidation complete. All duplicate mappings removed.

---

## Phase 8: Final Verification

### Task 8.1: Verify Line Counts

Check that all refactored classes are under 600 lines:
- `OpcUaClientGraphChangeReceiver` - target ~500 lines (was 1216)
- `OpcUaClientGraphChangeSender` - target ~550 lines (was 587)
- `OpcUaServerGraphChangeReceiver` - target ~450 lines (was 593)
- `GraphChangeApplier` - target ~200 lines (new)

### Task 8.2: Run Full Test Suite

```bash
dotnet test src/Namotion.Interceptor.slnx -v minimal
```
Expected: All tests pass

---

**CHECKPOINT: Review with user** - All phases complete. Ready for final review and merge.

---

## Summary of All Renames

| Current Name | New Name |
|--------------|----------|
| `StructuralChangeProcessor` | `GraphChangePublisher` |
| `Client/Graph/OpcUaGraphChangeProcessor` | `Client/OpcUaClientGraphChangeReceiver` |
| `Server/Graph/OpcUaGraphChangeProcessor` | `Server/OpcUaServerGraphChangeReceiver` |
| `OpcUaClientStructuralChangeProcessor` | `OpcUaClientGraphChangeSender` |
| `OpcUaServerStructuralChangeProcessor` | `OpcUaServerGraphChangeSender` |
| `RemoteSyncManager` | `OpcUaClientGraphChangeTrigger` |
| `ExternalNodeManagementHelper` | `OpcUaServerExternalNodeValidator` |
| `OpcUaBrowseHelper` | `OpcUaHelper` |
| `OpcUaPropertyWriter` | `OpcUaClientPropertyWriter` |
| `OpcUaModelChangePublisher` | `OpcUaServerModelChangePublisher` |
| `OpcUaNodeCreator` | `OpcUaServerNodeCreator` |

---

## Critical Files

- `Client/Graph/OpcUaGraphChangeProcessor.cs` - 1216 lines, will become OpcUaClientGraphChangeReceiver
- `Server/Graph/OpcUaGraphChangeProcessor.cs` - 593 lines, will become OpcUaServerGraphChangeReceiver
- `Connectors/ConnectorSubjectMapping.cs` - needs TryUnregisterByExternalId method
- `Client/OpcUaSubjectClientSource.cs` - contains _subjectMapping to share
- `Graph/OpcUaBrowseHelper.cs` - move to root as OpcUaHelper
