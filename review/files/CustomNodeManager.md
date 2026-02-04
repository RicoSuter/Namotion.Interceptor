# Code Review: CustomNodeManager.cs

**File:** `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\CustomNodeManager.cs`
**Branch:** `feature/opc-ua-full-sync`
**Last Reviewed:** 2026-02-04

---

## Summary

`CustomNodeManager` is the central OPC UA server node manager that bridges the C# interceptor model with the OPC UA address space. The architecture includes:

1. **Data Structure:**
   - Uses `SubjectConnectorRegistry<NodeId, NodeState>` for unified reference counting and bidirectional O(1) lookups

2. **Responsibility Extraction:**
   - `OpcUaServerNodeCreator` - handles node creation logic
   - `OpcUaServerGraphChangeReceiver` - handles external AddNodes/DeleteNodes requests
   - `OpcUaServerGraphChangePublisher` - batches and emits ModelChangeEvents

3. **Capabilities:**
   - Bidirectional graph sync (external clients can modify the address space)
   - Reference counting for shared subjects (same subject in multiple collections)
   - ModelChangeEvent publishing for live client updates
   - Collection reindexing after item removal

---

## Thread Safety Analysis

### Lock Strategy: `_structureLock` (SemaphoreSlim)

The `CustomNodeManager` uses a `SemaphoreSlim` named `_structureLock` for synchronizing structural changes.

**Methods Protected by `_structureLock.Wait()`:**

| Method | Lines | Lock Acquired |
|--------|-------|---------------|
| `CreateAddressSpace` | 104-131 | Yes |
| `RemoveSubjectNodes` | 141-152 | Yes |
| `RemoveSubjectNodesAndReindex` | 160-175 | Yes |
| `CreateSubjectNode` | 564-622 | Yes |

### Issues Identified

| Severity | Issue | Details |
|----------|-------|---------|
| **Critical** | `ClearPropertyData` method has no lock protection | Lines 69-92: This method iterates over `_subjectRegistry.GetAllSubjects()` and the root subject's properties without holding `_structureLock`. If called concurrently with structural changes, it could process stale data or encounter collection-modified exceptions. |
| **Important** | Synchronous `.Wait()` used instead of `.WaitAsync()` | All locked methods use blocking `.Wait()` which can cause thread pool starvation under high load. The OPC UA SDK may call from async contexts. |
| **Important** | Lock not held during `FlushModelChangeEvents` | Lines 450-453: `FlushModelChangeEvents()` calls `_modelChangePublisher.Flush()` without acquiring `_structureLock`. While `_modelChangePublisher` has its own lock, the timing of flush relative to structural changes is uncoordinated. |

### Thread Safety of Helper Classes

**`SubjectConnectorRegistry<TExternalId, TData>`:**
- Uses `Lock` (newer .NET 9 primitive) for internal synchronization
- All operations are individually thread-safe
- Operations spanning multiple calls are NOT atomic but `_structureLock` coordinates at higher level

---

## Race Condition Analysis

### Potential TOCTOU Issues

| Location | Pattern | Risk Level |
|----------|---------|------------|
| `RemoveSubjectNodesCore` lines 183-186 | Gets node state, then unregisters, then uses | **Low** - All under same `_structureLock` |
| `ReindexCollectionBrowseNamesCore` lines 538-546 | Gets `PredefinedNodes`, removes old key, adds new key | **Medium** - Multiple dictionary operations |

### Detailed Analysis: ReindexCollectionBrowseNamesCore

```csharp
// Lines 538-546
var predefinedNodes = GetPredefinedNodes();
if (predefinedNodes.ContainsKey(oldNodeId))
{
    predefinedNodes.Remove(oldNodeId);      // Step 1
    nodeState.NodeId = newNodeId;            // Step 2
    predefinedNodes[newNodeId] = nodeState;  // Step 3
    _subjectRegistry.UpdateExternalId(subject, newNodeId);  // Step 4
}
```

**Risk:** If `PredefinedNodes` is accessed by OPC UA browse operations between steps 1 and 3:
- Step 1: Old key removed - browse may fail to find node
- Step 2: NodeId changed in memory
- Step 3: New key added

**Impact:** A client browsing during reindexing might see inconsistent results (missing nodes briefly).

**Mitigation:** The `_structureLock` is held during this operation, but `PredefinedNodes` is accessed by the OPC UA SDK's browse implementation which may not coordinate with this lock.

---

## Code Quality Issues

### Method Complexity

| Method | Lines | Cyclomatic Complexity | Assessment |
|--------|-------|----------------------|------------|
| `RemoveReferenceFromParentProperty` | 237-344 | ~12 | **High** - Multiple nested conditions |
| `GetParentNodeId` | 384-443 | ~10 | **Medium-High** - Complex branching |
| `CreateSubjectNode` | 564-622 | ~8 | **Medium** - Multiple property type handling |

### Path Building Inconsistency

Path building uses hardcoded `"."` in several places while `PathDelimiter` constant exists at line 14:

- Line 119: `_configuration.RootName + PathDelimiter` (correct)
- Line 299: `$"{parentPath}.{propertyName}"` (hardcoded)
- Line 303: `$"{_configuration.RootName}.{propertyName}"` (hardcoded)
- Line 412: `$"{_configuration.RootName}.{propertyName}"` (hardcoded)
- Line 433: `$"{parentPath}.{propertyName}"` (hardcoded)

### GetParentNodeId Method

Lines 384-443: This method always uses `Parents[0]` to find the parent, which is documented as correct:

```csharp
// Note: Taking Parents[0] is safe here because this method is only called
// when removing the last reference (isLast=true). At that point, the
// SubjectRegistry has already processed all previous parent removals,
// leaving exactly one parent in the array - the current one being removed.
```

This design is acceptable given the documented constraint.

---

## Refactoring Opportunities

### 1. Extract Path Building Logic

Path building is duplicated across multiple methods. Consider a centralized utility:

```csharp
internal static class OpcUaPathHelper
{
    public static string BuildPath(string? basePath, string segment)
        => string.IsNullOrEmpty(basePath) ? segment : $"{basePath}{PathDelimiter}{segment}";
}
```

### 2. Reduce Lock Scope in ReindexCollectionBrowseNamesCore

The entire method is locked, but only the dictionary manipulation portion needs protection. Consider calculating new names outside the lock.

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
   - Or use a centralized path building utility

4. **Consider async lock pattern:**
   - Replace `_structureLock.Wait()` with `_structureLock.WaitAsync()` for methods that may be called from async contexts
   - This prevents thread pool starvation under high concurrent load

### Suggestions (Nice to Have)

5. **Extract path building to helper class** (reduces duplication, improves testability)

6. **Add XML documentation to public/internal methods** that explains thread safety guarantees

7. **Add integration tests** that simulate:
   - Multiple clients adding nodes simultaneously
   - Browse operations during reindexing
   - Shared subject removal from one parent

---

## Acknowledgments (What Was Done Well)

1. **Clean separation of concerns** - Extracting `OpcUaServerNodeCreator`, `OpcUaServerGraphChangeReceiver`, and `OpcUaServerGraphChangePublisher` significantly improves maintainability

2. **Unified registry** - `SubjectConnectorRegistry<NodeId, NodeState>` provides atomic reference counting with bidirectional O(1) lookup, essential for external node management performance

3. **Atomic remove-and-reindex** - `RemoveSubjectNodesAndReindex` method ensures removal and reindexing happen under a single lock, preventing race conditions

4. **Comprehensive logging** - Debug-level logging throughout helps with troubleshooting

5. **Feature flag design** - The `EnableNodeManagement` flag is properly checked at API boundaries in `OpcUaServerGraphChangeReceiver`

6. **ModelChangeEvent batching** - The `OpcUaServerGraphChangePublisher` with atomic swap pattern is a good design for efficient client notification

7. **Documented design decisions** - The comment explaining why `Parents[0]` is safe in `GetParentNodeId` is excellent

---

## Files Referenced

- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\CustomNodeManager.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerNodeCreator.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerGraphChangeReceiver.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.OpcUa\Server\OpcUaServerGraphChangePublisher.cs`
- `C:\Users\rsute\GitHub\Namotion.Interceptor\src\Namotion.Interceptor.Connectors\SubjectConnectorRegistry.cs`
