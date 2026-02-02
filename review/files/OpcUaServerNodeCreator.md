# Code Review: OpcUaServerNodeCreator.cs

**File:** `src/Namotion.Interceptor.OpcUa/Server/OpcUaServerNodeCreator.cs`
**Status:** Complete
**Reviewer:** Claude
**Date:** 2026-02-02
**Lines:** ~479

---

## Overview

`OpcUaServerNodeCreator` creates OPC UA nodes from C# model subjects and properties. It was extracted from `CustomNodeManager` for better separation of concerns. This is a core class for the server-side graph sync feature.

### Key Responsibilities

1. **Node Creation**: Creates OPC UA nodes (Variables, Objects, Folders) from model properties
2. **Collection Handling**: Creates container nodes for collections (Container/Flat modes) and dictionary properties
3. **Reference Counting**: Uses `ConnectorReferenceCounter` to handle shared subjects across multiple parents
4. **Subject Registration**: Registers subjects with `ConnectorSubjectMapping` for O(1) bidirectional lookup
5. **ModelChangeEvent Publishing**: Queues node/reference add events via `OpcUaServerGraphChangePublisher`
6. **Value Change Handling**: Subscribes to `StateChanged` events for bidirectional value sync

### Dependencies (8 injected)

| Dependency | Purpose |
|------------|---------|
| `CustomNodeManager` | Parent node manager for SDK operations |
| `OpcUaServerConfiguration` | Configuration and node mapper |
| `OpcUaNodeFactory` | Low-level OPC UA node creation |
| `OpcUaSubjectServerBackgroundService` | Source for property updates |
| `ConnectorReferenceCounter<NodeState>` | Reference counting for shared subjects |
| `ConnectorSubjectMapping<NodeId>` | Bidirectional subject-to-NodeId mapping |
| `OpcUaServerGraphChangePublisher` | Batches ModelChangeEvents |
| `ILogger` | Logging |

---

## Critical Issues

### Issue 1: Memory Leak - StateChanged Event Handlers Never Unsubscribed (CRITICAL)

**Location:** Lines 367-381

```csharp
variableNode.StateChanged += (_, _, changes) =>
{
    if (changes.HasFlag(NodeStateChangeMasks.Value))
    {
        DateTimeOffset timestamp;
        object? nodeValue;
        lock (variableNode)
        {
            timestamp = variableNode.Timestamp;
            nodeValue = variableNode.Value;
        }

        _source.UpdateProperty(property.Reference, timestamp, nodeValue);
    }
};
```

**Problems:**

1. **Event handlers never unsubscribed**: No `-=` operator found anywhere in the codebase for `StateChanged`.

2. **Lambda captures create reference cycles**:
   - Captures: `variableNode`, `property`, `_source`
   - These references prevent garbage collection even after nodes are "deleted"

3. **ClearPropertyData is insufficient**: `CustomNodeManager.ClearPropertyData()` removes the variableNode from storage but does NOT unsubscribe the event handlers.

4. **Recursive creation amplifies the problem**: `CreateAttributeNodes` (lines 279-302) recursively creates attribute nodes, each with its own `StateChanged` handler.

5. **Server restart loop compounds leaks**: The `OpcUaSubjectServerBackgroundService.ExecuteServerLoopAsync` restarts the server on failure. Each restart creates new handlers without cleaning up old ones.

**Impact:**

```
Server start #1: 100 nodes → 100 handlers
Server restart #2: 100 NEW handlers (200 total)
Server restart #10: 1000 handlers accumulated
Memory grows with each restart cycle
```

**Recommendation:**

```csharp
// Option 1: Store handler reference for cleanup
private void ConfigureVariableNode(...)
{
    NodeStateChangedHandler handler = (_, _, changes) => { ... };
    variableNode.StateChanged += handler;

    // Store for later cleanup
    variableNode.Handle = new VariableNodeHandle(property.Reference, handler);
}

// Option 2: Use weak references
variableNode.StateChanged += WeakEventHandler.Create<NodeStateChangedEventArgs>(
    (sender, args) => { ... });
```

### Issue 2: Recursive Attribute Nodes with No Depth Limit (Important)

**Location:** Lines 299-300

```csharp
// Recursive: attributes can have attributes
CreateAttributeNodes(attributeNode, attribute, attributePath);
```

**Problem:** No depth limit on attribute recursion. A model with deeply nested attributes could cause stack overflow.

**Recommendation:** Add depth tracking:
```csharp
public void CreateAttributeNodes(NodeState parentNode, RegisteredSubjectProperty property, string parentPath, int depth = 0)
{
    if (depth > 10) // or configurable max
    {
        _logger.LogWarning("Maximum attribute depth exceeded for {Path}", parentPath);
        return;
    }
    // ...
    CreateAttributeNodes(attributeNode, attribute, attributePath, depth + 1);
}
```

---

## Thread Safety Analysis

### Lock Strategy: Relies on Parent Class

`OpcUaServerNodeCreator` has **no internal locking** - it relies entirely on `CustomNodeManager._structureLock`.

**Protected Call Sites in CustomNodeManager:**

| Method | Lock Acquired | NodeCreator Methods Called |
|--------|---------------|----------------------------|
| `CreateAddressSpace` (112-134) | Yes | `CreateSubjectNodes` |
| `CreateSubjectNode` (534-617) | Yes | `CreateCollectionChildNode`, `CreateDictionaryChildNode`, `CreateSubjectReferenceNode`, `GetOrCreateContainerNode` |

**Thread-Safe Components:**

- `ConnectorReferenceCounter`: Uses `Lock` internally
- `ConnectorSubjectMapping`: Uses `Lock` internally
- `OpcUaNodeFactory`: Stateless, thread-safe

**Assessment:** Thread safety is **adequate** when called from locked contexts. Direct external calls would not be safe.

### Issue 3: Lock Inside StateChanged Handler (Moderate)

**Location:** Lines 373-377

```csharp
lock (variableNode)
{
    timestamp = variableNode.Timestamp;
    nodeValue = variableNode.Value;
}
```

**Problem:** Uses `lock(variableNode)` - locking on a public object. The OPC UA SDK or other code may also lock on `variableNode`, creating potential deadlock risk.

**Recommendation:** Use a dedicated lock object or rely on SDK's thread safety guarantees.

---

## Code Quality Analysis

### Issue 4: 8 Constructor Dependencies (Moderate)

**Location:** Lines 30-49

```csharp
public OpcUaServerNodeCreator(
    CustomNodeManager nodeManager,
    OpcUaServerConfiguration configuration,
    OpcUaNodeFactory nodeFactory,
    OpcUaSubjectServerBackgroundService source,
    ConnectorReferenceCounter<NodeState> subjectRefCounter,
    ConnectorSubjectMapping<NodeId> subjectMapping,
    OpcUaServerGraphChangePublisher modelChangePublisher,
    ILogger logger)
```

**Analysis:** 8 dependencies is high but justified:
- Each dependency has a distinct responsibility
- No dependency group could be combined into a facade without losing cohesion
- The class was already extracted from `CustomNodeManager` to reduce that class's complexity

**Verdict:** Acceptable given the domain complexity.

### Issue 5: Null-Forgiving Operator on child.Index (Moderate)

**Location:** Lines 215, 225

```csharp
CreateCollectionChildNode(property, child.Subject, child.Index!, propertyName, parentPath, parentNodeId, nodeConfiguration);
```

**Problem:** `child.Index!` assumes Index is never null for collection children. If this assumption is violated, it will throw at runtime rather than be handled gracefully.

**Recommendation:** Add validation:
```csharp
foreach (var child in children)
{
    if (child.Index is null)
    {
        _logger.LogWarning("Collection child has null index, skipping");
        continue;
    }
    CreateCollectionChildNode(property, child.Subject, child.Index, ...);
}
```

### Issue 6: Inconsistent Container Mode Handling (Minor)

**Collections:** Support both Container and Flat modes (lines 209-227)
**Dictionaries:** Only Container mode (lines 241)

This asymmetry may be intentional (dictionaries always need containers?) but is not documented.

---

## Separation of Concerns Analysis

### OpcUaNodeFactory vs OpcUaServerNodeCreator

| Aspect | OpcUaNodeFactory | OpcUaServerNodeCreator |
|--------|------------------|------------------------|
| **Purpose** | OPC UA SDK primitives | Domain model integration |
| **Creates** | Raw node instances | Configured nodes with bindings |
| **Knows about** | OPC UA types, NodeIds | Subjects, properties, reference counting |
| **Thread safety** | Stateless, safe | Relies on parent locking |

**Verdict:** Excellent separation of concerns. Factory handles SDK; Creator handles domain logic.

### CreateChildObject: The Key Integration Point

**Location:** Lines 428-468

This method is the **only server-side point** that:
1. Increments reference count
2. Registers subject with mapping
3. Recursively creates child nodes
4. Queues ModelChangeEvents

```csharp
var isFirst = _subjectRefCounter.IncrementAndCheckFirst(subject, () => { createNode }, out var nodeState);
if (isFirst)
{
    _subjectMapping.Register(subject, nodeState.NodeId);
    CreateSubjectNodes(nodeState.NodeId, registeredSubject, path + PathDelimiter);
    _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.NodeAdded);
}
else
{
    parentNode.AddReference(referenceTypeId ?? ReferenceTypeIds.HasComponent, false, nodeState.NodeId);
    _modelChangePublisher.QueueChange(nodeState.NodeId, ModelChangeStructureVerbMask.ReferenceAdded);
}
```

**Design Quality:** Good - centralized integration point prevents scattered registration logic.

---

## SRP/SOLID Evaluation

### Single Responsibility Principle

**Current responsibilities:**
1. Create variable nodes ✓
2. Create object nodes ✓
3. Create collection/dictionary container nodes ✓
4. Configure variable nodes with value change handlers ✓
5. Manage reference counting ✓
6. Register subjects with mapping ✓
7. Queue ModelChangeEvents ✓

**Assessment:** The class has ~7 related responsibilities, all around "creating and configuring OPC UA nodes from model elements". This is **cohesive** rather than violating SRP.

### Can It Be Split?

**Potential extractions:**

| Candidate | Lines | Benefit | Cost |
|-----------|-------|---------|------|
| VariableNodeConfigurator | 329-384 | Isolates value binding | Low benefit, adds indirection |
| AttributeNodeCreator | 279-324 | Separates attribute handling | Breaks cohesion with parent node creation |
| SharedSubjectHandler | 428-468 | Isolates reference counting | Already well-encapsulated |

**Verdict:** Current structure is appropriate. Extraction would add complexity without proportional benefit.

---

## Test Coverage Analysis

### Coverage Type: Integration Only

**No direct unit tests found** for `OpcUaServerNodeCreator`.

**Indirect coverage via integration tests:**

| Test File | Methods Exercised |
|-----------|-------------------|
| ServerToClientCollectionTests | `CreateCollectionObjectNode`, `CreateCollectionChildNode` |
| ServerToClientDictionaryTests | `CreateDictionaryObjectNode`, `CreateDictionaryChildNode` |
| ServerToClientReferenceTests | `CreateSubjectReferenceNode`, `CreateChildObject` |
| ClientToServerNestedPropertyTests | `CreateVariableNode` |

### Coverage Gaps

1. **No unit tests for edge cases:**
   - Null/empty dictionary keys (line 178-184)
   - Deeply nested attributes
   - Reference counting with shared subjects

2. **No tests for error paths:**
   - What if `TryGetRegisteredSubject()` returns null (line 434)?
   - What if `FindNode()` returns null for parent (line 462)?

3. **No memory leak verification tests**

---

## Recommendations

### Critical (Must Fix)

1. **Fix event handler memory leak** (Issue 1):
   - Store handler references for cleanup
   - Add cleanup in `CustomNodeManager` disposal path
   - OR use weak event pattern

### Important (Should Fix)

2. **Add depth limit to attribute recursion** (Issue 2)

3. **Replace lock(variableNode)** with dedicated lock object (Issue 3)

4. **Add null validation for collection child.Index** (Issue 5)

5. **Add unit tests** for:
   - Edge cases (null keys, missing nodes)
   - Reference counting behavior
   - Shared subject scenarios

### Suggestions (Nice to Have)

6. **Document why dictionaries don't support Flat mode** (Issue 6)

7. **Add XML documentation** to all public methods

8. **Consider extracting VariableNodeConfigurator** if the class grows

9. **Add metrics/logging** for node creation counts

---

## Acknowledgments (What Was Done Well)

1. **Clean extraction from CustomNodeManager** - Clear separation of node creation from node management

2. **Excellent separation from OpcUaNodeFactory** - SDK primitives vs domain logic properly isolated

3. **Centralized integration point in CreateChildObject** - Single location for registration, reference counting, and event queuing

4. **Proper use of reference counting** - `IncrementAndCheckFirst` pattern correctly handles shared subjects

5. **Container/Flat mode support for collections** - Flexible configuration without code duplication

6. **Comprehensive logging** - Debug-level logs throughout for troubleshooting

7. **ConfigureAwait not needed** - Server-side code doesn't need ConfigureAwait(false)

---

## Files Referenced

| File | Purpose |
|------|---------|
| `OpcUaServerNodeCreator.cs` | Main file under review |
| `CustomNodeManager.cs` | Parent class that instantiates and uses this |
| `OpcUaNodeFactory.cs` | Dependency for SDK node creation |
| `OpcUaSubjectServerBackgroundService.cs` | Source for property updates |
| `ConnectorReferenceCounter.cs` | Reference counting utility |
| `ConnectorSubjectMapping.cs` | Bidirectional mapping utility |
| `OpcUaServerGraphChangePublisher.cs` | ModelChangeEvent batching |
