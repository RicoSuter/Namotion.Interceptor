# Code Review: CustomNodeManager.cs

**File:** `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\CustomNodeManager.cs`
**Branch:** `feature/opc-ua-full-sync`
**Change Summary:** +445 additions, -260 deletions

---

## Summary

`CustomNodeManager` is the central OPC UA server node manager that bridges the C# interceptor model with the OPC UA address space. The refactoring introduced significant architectural improvements:

1. **Data Structure Changes:**
   - Replaced simple `Dictionary<RegisteredSubject, NodeState>` with `ConnectorReferenceCounter<NodeState>` for reference counting
   - Added `ConnectorSubjectMapping<NodeId>` for bidirectional O(1) lookups between subjects and NodeIds

2. **Responsibility Extraction:**
   - `OpcUaServerNodeCreator` - handles node creation logic
   - `OpcUaServerGraphChangeReceiver` - handles external AddNodes/DeleteNodes requests
   - `OpcUaServerGraphChangePublisher` - batches and emits ModelChangeEvents

3. **New Capabilities:**
   - Bidirectional graph sync (external clients can modify the address space)
   - Reference counting for shared subjects (same subject in multiple collections)
   - ModelChangeEvent publishing for live client updates
   - Collection reindexing after item removal

---

## Feature Flag Safety Analysis

### Configuration: `EnableNodeManagement`

The feature flag `EnableNodeManagement` controls whether external OPC UA clients can add/delete nodes.

**Positive Findings:**

1. **Proper Guard at Entry Points:**
   - `OpcUaServerGraphChangeReceiver.AddSubjectFromExternal()` (line 76): Checks `_externalNodeValidator.IsEnabled` before proceeding
   - `OpcUaServerGraphChangeReceiver.RemoveSubjectFromExternal()` (line 145): Same guard check

2. **Exposed Property:**
   - `CustomNodeManager.IsExternalNodeManagementEnabled` (line 646) delegates to `_graphChangeProcessor.IsEnabled`

**Issues Identified:**

| Severity | Issue | Location |
|----------|-------|----------|
| **Suggestion** | The `_graphChangeProcessor` instance is always created regardless of feature flag state | `CustomNodeManager` constructor, lines 46-57 |

**Analysis:** While the `_graphChangeProcessor` is always instantiated, this is acceptable because:
- The class is lightweight (no expensive initialization)
- Guards are properly implemented at the public method entry points
- Creating it unconditionally simplifies the code structure

**Verdict:** Feature flag safety is **adequate**. The guards are correctly placed at the API boundary.

---

## Thread Safety Analysis

### Lock Strategy: `_structureLock` (SemaphoreSlim)

The `CustomNodeManager` uses a `SemaphoreSlim` named `_structureLock` for synchronizing structural changes.

**Methods Protected by `_structureLock.Wait()`:**

| Method | Lines | Lock Acquired |
|--------|-------|---------------|
| `CreateAddressSpace` | 112-134 | Yes |
| `RemoveSubjectNodes` | 147-206 | Yes |
| `ReindexCollectionBrowseNames` | 441-522 | Yes |
| `CreateSubjectNode` | 534-616 | Yes |

### Issues Identified

| Severity | Issue | Details |
|----------|-------|---------|
| **Important** | Synchronous `.Wait()` used instead of `.WaitAsync()` | All locked methods use blocking `.Wait()` which can cause thread pool starvation under high load. The methods are sync, but the OPC UA SDK may call from async contexts. |
| **Important** | Lock not held during `FlushModelChangeEvents` | Line 422-425: `FlushModelChangeEvents()` calls `_modelChangePublisher.Flush()` without acquiring `_structureLock`. While `_modelChangePublisher` has its own lock, the timing of flush relative to structural changes is uncoordinated. |
| **Critical** | `ClearPropertyData` method has no lock protection | Lines 73-97: This method iterates over `_subjectMapping.GetAllSubjects()` and modifies property data without holding `_structureLock`. If called concurrently with structural changes, it could process stale data. |

### Thread Safety of Helper Classes

**`ConnectorReferenceCounter<TData>` and `ConnectorSubjectMapping<TExternalId>`:**
- Both use `Lock` (newer .NET 9 primitive) for internal synchronization
- All operations are individually thread-safe
- **However:** Operations spanning multiple calls are NOT atomic

**Example Race Condition Pattern (RemoveSubjectNodes, lines 145-206):**

```csharp
// Line 151: First read (under _structureLock)
_subjectRefCounter.TryGetData(subject, out var existingNodeState);

// Line 154: Decrement (still under _structureLock - GOOD)
var isLast = _subjectRefCounter.DecrementAndCheckLast(subject, out var nodeState);
```

This particular sequence is protected because both operations happen under `_structureLock`. The potential issue is if external code accesses these structures outside of the lock.

### OPC UA SDK Thread Model Considerations

The OPC UA SDK may call `CustomNodeManager` methods from:
- Session threads (multiple concurrent clients)
- Subscription publishing threads
- Service request processing threads

**Recommendation:** All structural modification methods should be reviewed to ensure they are called from within the same thread model that the SDK expects.

---

## Race Condition Analysis

### Potential TOCTOU Issues

| Location | Pattern | Risk Level |
|----------|---------|------------|
| `RemoveSubjectNodes` lines 150-160 | Gets node state, then decrements, then uses | **Low** - All under same `_structureLock` |
| `ReindexCollectionBrowseNames` lines 500-508 | Gets `PredefinedNodes`, removes old key, adds new key | **Medium** - Multiple dictionary operations |
| `CreateChildObject` in `OpcUaServerNodeCreator` | `IncrementAndCheckFirst` then `CreateSubjectNodes` | **Low** - Designed with lock semantics in mind |

### Detailed Analysis: ReindexCollectionBrowseNames

```csharp
// Lines 499-508
var predefinedNodes = GetPredefinedNodes();
if (predefinedNodes.ContainsKey(oldNodeId))
{
    predefinedNodes.Remove(oldNodeId);      // Step 1
    nodeState.NodeId = newNodeId;            // Step 2
    predefinedNodes[newNodeId] = nodeState;  // Step 3
    _subjectMapping.UpdateExternalId(subject, newNodeId);  // Step 4
}
```

**Risk:** If `PredefinedNodes` is accessed by OPC UA browse operations between steps 1 and 3:
- Step 1: Old key removed - browse may fail to find node
- Step 2: NodeId changed in memory
- Step 3: New key added

**Impact:** A client browsing during reindexing might see inconsistent results (missing nodes briefly).

**Mitigation:** The `_structureLock` is held during this operation, but `PredefinedNodes` is accessed by the OPC UA SDK's browse implementation which may not coordinate with this lock.

### ModelChangeEvent Publishing Timing

The `_modelChangePublisher.QueueChange()` calls happen inside the `_structureLock`:
- Line 185: `NodeDeleted` queued
- Line 200: `ReferenceDeleted` queued

But `FlushModelChangeEvents()` (line 422-425) has no lock:

```csharp
public void FlushModelChangeEvents()
{
    _modelChangePublisher.Flush(Server, SystemContext);
}
```

**Risk:** Events could be flushed while structural changes are still in progress if `Flush` is called from a different thread.

---

## Code Quality Issues

### Method Complexity

| Method | Lines | Cyclomatic Complexity | Assessment |
|--------|-------|----------------------|------------|
| `RemoveReferenceFromParentProperty` | 213-320 | ~12 | **High** - Multiple nested conditions |
| `GetParentNodeId` | 360-415 | ~10 | **Medium-High** - Complex branching |
| `CreateSubjectNode` | 532-617 | ~8 | **Medium** - Multiple property type handling |

### Detailed Review: RemoveReferenceFromParentProperty

This method (lines 213-320) handles removing a child reference from its parent when dealing with shared subjects. The complexity stems from:

1. Determining parent node (root subject special case vs regular subject)
2. Determining container node (flat vs container collection mode)
3. Building path-based NodeIds with multiple fallbacks

**Issues:**
- Null checks are scattered throughout (lines 217, 231, 239, 249, 296)
- Path building logic is duplicated with `GetParentNodeId`
- Magic string "." used as path delimiter but `PathDelimiter` constant exists

**Example of Inconsistent Path Building:**

```csharp
// Line 275 uses string interpolation
containerPath = $"{parentPath}.{propertyName}";

// Line 279 uses concatenation with PathDelimiter constant
containerPath = $"{_configuration.RootName}.{propertyName}";  // Still hardcoded "."
```

The `PathDelimiter` constant exists at line 14 but is not consistently used.

### Error Handling Review

**Positive:**
- Logging at debug/warning levels for expected failure cases
- Graceful degradation when nodes not found

**Missing:**
- No exception handling in `RemoveNodeAndReferences` for `DeleteNode` failures
- No validation that `sourceProperty` in `RemoveSubjectNodes` actually references the subject being removed

### GetParentNodeId Method Correctness

Lines 360-415: This method finds the parent NodeId for a subject using `RegisteredSubject.Parents`.

**Potential Issue (line 363-364):**
```csharp
if (registered?.Parents.Length > 0)
{
    var parentProperty = registered.Parents[0].Property;
```

Always takes the first parent. For shared subjects with multiple parents, this may not be the correct parent in context.

**Impact:** When removing a shared subject, the wrong parent might be identified, leading to incorrect reference cleanup.

---

## Refactoring Opportunities

### 1. Extract Path Building Logic

Path building is duplicated across multiple methods:
- `RemoveReferenceFromParentProperty` (lines 272-284)
- `GetParentNodeId` (lines 380-410)
- `CreateSubjectNode` (lines 540-564)
- `ReindexCollectionBrowseNames` (lines 451-469)

**Recommendation:** Create a `PathBuilder` helper class or extension methods:

```csharp
internal static class OpcUaPathHelper
{
    public static string BuildSubjectPath(
        IInterceptorSubject parentSubject,
        IInterceptorSubject rootSubject,
        string? rootName,
        ConnectorReferenceCounter<NodeState> refCounter)
    {
        // Centralized path building logic
    }
}
```

### 2. Consolidate Parent Node Resolution

`GetParentNodeId` and `RemoveReferenceFromParentProperty` both need to find parent nodes but implement different logic. Consider:

```csharp
private (NodeState? ParentNode, NodeState? ContainerNode) ResolveParentAndContainer(
    RegisteredSubjectProperty parentProperty,
    IInterceptorSubject subject)
{
    // Unified parent/container resolution
}
```

### 3. Reduce Lock Scope in ReindexCollectionBrowseNames

The entire method is locked, but only the dictionary manipulation portion needs protection:

```csharp
// Instead of locking the entire method, lock only critical section:
foreach (var i = 0; i < children.Count; i++)
{
    // ... calculate new names without lock ...

    _structureLock.Wait();
    try
    {
        // Only dictionary manipulation here
    }
    finally
    {
        _structureLock.Release();
    }
}
```

**Caveat:** This would require ensuring children collection is stable during iteration.

### 4. Consider Moving More Logic to OpcUaServerNodeCreator

Methods like `RemoveSubjectNodes` and `RemoveNodeAndReferences` deal with node lifecycle but live in `CustomNodeManager`. For consistency with the refactoring direction:

- `RemoveSubjectNodes` could delegate to a new `OpcUaServerNodeRemover` or extend `OpcUaServerNodeCreator` to `OpcUaServerNodeLifecycle`
- This would centralize all node creation/deletion logic

### 5. Improve OpcUaServerGraphChangeReceiver Integration

The `_graphChangeProcessor` receives function delegates in the constructor:

```csharp
_graphChangeProcessor = new OpcUaServerGraphChangeReceiver(
    ...
    FindNodeInAddressSpace,  // Func<NodeId, NodeState?>
    CreateSubjectNode,       // Action<RegisteredSubjectProperty, IInterceptorSubject, object?>
    () => NamespaceIndex,    // Func<ushort>
    ...
);
```

This creates tight coupling through delegates. Consider an interface-based approach:

```csharp
internal interface INodeManagerOperations
{
    NodeState? FindNode(NodeId nodeId);
    void CreateSubjectNode(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);
    ushort NamespaceIndex { get; }
}
```

---

## Recommendations

### Critical (Must Fix)

1. **Add lock protection to `ClearPropertyData`:**
   ```csharp
   public void ClearPropertyData()
   {
       _structureLock.Wait();
       try
       {
           // existing logic
       }
       finally
       {
           _structureLock.Release();
       }
   }
   ```

### Important (Should Fix)

2. **Coordinate `FlushModelChangeEvents` with structural changes:**
   - Either acquire `_structureLock` before flushing, or
   - Document that `Flush` must only be called after all structural changes complete

3. **Fix path delimiter inconsistency:**
   - Replace all hardcoded `"."` with `PathDelimiter` constant
   - Consider making path building a centralized utility

4. **Review `GetParentNodeId` for shared subjects:**
   - The method should accept the `sourceProperty` parameter to correctly identify which parent to use
   - Update signature: `GetParentNodeId(IInterceptorSubject subject, RegisteredSubjectProperty? sourceProperty = null)`

5. **Consider async lock pattern:**
   - Replace `_structureLock.Wait()` with `_structureLock.WaitAsync()` for methods that may be called from async contexts
   - This prevents thread pool starvation under high concurrent load

### Suggestions (Nice to Have)

6. **Extract path building to helper class** (reduces duplication, improves testability)

7. **Add XML documentation to public/internal methods** that explains thread safety guarantees

8. **Consider using `ConcurrentDictionary` for `PredefinedNodes`** if OPC UA SDK supports it, to reduce lock contention during browse operations

9. **Add integration tests** that simulate:
   - Multiple clients adding nodes simultaneously
   - Browse operations during reindexing
   - Shared subject removal from one parent

10. **Improve logging consistency:**
    - Some debug logs include `{SubjectType}` but not NodeId
    - Standardize log message format across methods

---

## Acknowledgments (What Was Done Well)

1. **Clean separation of concerns** - Extracting `OpcUaServerNodeCreator`, `OpcUaServerGraphChangeReceiver`, and `OpcUaServerGraphChangePublisher` significantly improves maintainability

2. **Reference counting implementation** - The `ConnectorReferenceCounter` pattern elegantly handles shared subjects across multiple collections

3. **Bidirectional mapping** - `ConnectorSubjectMapping` provides O(1) lookup in both directions, essential for external node management performance

4. **Comprehensive logging** - Debug-level logging throughout helps with troubleshooting

5. **Feature flag design** - The `EnableNodeManagement` flag is properly checked at API boundaries, not scattered throughout

6. **ModelChangeEvent batching** - The `OpcUaServerGraphChangePublisher` with atomic swap pattern is a good design for efficient client notification

---

## Files Referenced

- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\CustomNodeManager.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerNodeCreator.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerGraphChangeReceiver.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerGraphChangePublisher.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerConfiguration.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerExternalNodeValidator.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Connectors\ConnectorReferenceCounter.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Connectors\ConnectorSubjectMapping.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Connectors\GraphChangeApplier.cs`
