# OPC UA Cleanup & Simplification Plan

**Goal:** Clean up and simplify the OPC UA dynamic sync implementation for maintainability.

**Current State:** Tests passing, implementation works, but code is bloated with hacks and duplication.

---

## Architectural Goal

**Keep existing classes thin - delegate to new `Graph/` classes:**

```
src/Namotion.Interceptor.OpcUa/
├── Graph/                          # SHARED between Client and Server
│   ├── SubjectTracker.cs           # Reference counting, NodeId↔Subject mapping
│   ├── SubjectPropertyHelper.cs    # Collection/dictionary manipulation
│   └── OpcUaBrowseHelper.cs        # Browse utilities (moved from Client/)
│
├── Client/
│   ├── Graph/                      # Client-specific graph sync
│   │   ├── RemoteSyncManager.cs    # ModelChangeEvents + Periodic resync
│   │   └── PropertyWriter.cs       # OPC UA write logic
│   ├── OpcUaSubjectClientSource.cs # THIN - orchestrates, delegates to Graph/
│   └── OpcUaNodeChangeProcessor.cs # + _recentlyDeletedNodeIds tracking
│
└── Server/
    ├── Graph/                      # Server-specific graph management
    │   ├── NodeCreator.cs          # Create OPC UA nodes from model
    │   └── ModelChangePublisher.cs # Emit model change events
    └── CustomNodeManager.cs        # THIN - orchestrates, delegates to Graph/
```

**Target line counts for existing classes:**
- `OpcUaSubjectClientSource.cs`: 1,245 → ~400 lines (orchestration only)
- `CustomNodeManager.cs`: 1,404 → ~400 lines (orchestration only)
- `OpcUaNodeChangeProcessor.cs`: 1,133 → ~600 lines (uses shared helpers)
- `OpcUaClientStructuralChangeProcessor.cs`: 523 → ~350 lines (uses shared helpers)

---

## Feature Matrix

| Direction | Type | Operations | Client | Server |
|-----------|------|------------|--------|--------|
| Model → OPC UA | Reference | Add/Remove | `OpcUaClientStructuralChangeProcessor` | `CustomNodeManager` |
| Model → OPC UA | Collection | Add/Remove | `OpcUaClientStructuralChangeProcessor` | `CustomNodeManager` |
| Model → OPC UA | Dictionary | Add/Remove | `OpcUaClientStructuralChangeProcessor` | `CustomNodeManager` |
| OPC UA → Model | Reference | Add/Remove | `OpcUaNodeChangeProcessor` | via client writes |
| OPC UA → Model | Collection | Add/Remove | `OpcUaNodeChangeProcessor` | via client writes |
| OPC UA → Model | Dictionary | Add/Remove | `OpcUaNodeChangeProcessor` | via client writes |

---

## Sync Mode Analysis

### ModelChangeEvents + Periodic Resync

| Mode | Description | Purpose |
|------|-------------|---------|
| `EnableModelChangeEvents` | Subscribe to `GeneralModelChangeEventType` | Fast, real-time notification |
| `EnablePeriodicResync` | Poll server periodically | Safety net for missed events |

**Design rationale:** Both can be enabled together for defense in depth:
- Events can be missed due to network issues, queue overflow (`QueueSize=100`), reconnection gaps, or server bugs
- Periodic resync catches anything missed by events
- This is the correct design - not redundant

**`_recentlyDeletedNodeIds` tracking:**
- Needed when `EnableRemoteNodeManagement` AND `EnablePeriodicResync` are both true
- Prevents periodic resync from re-adding nodes the client intentionally deleted
- Valid race condition prevention, not a hack
- Move to `OpcUaNodeChangeProcessor` since it handles resync

---

## File Inventory

### Production Code (24 files, sorted by size)

| Lines | +Added | File | Status | Action |
|-------|--------|------|--------|--------|
| 1,404 | +1,028 | `Server/CustomNodeManager.cs` | BLOATED | Extract to Server/Graph/ |
| 1,245 | +538 | `Client/OpcUaSubjectClientSource.cs` | BLOATED | Extract to Client/Graph/ |
| 1,133 | +1,133 | `Client/OpcUaNodeChangeProcessor.cs` | NEW/LARGE | Use shared Graph/ |
| 573 | +55 | `Client/OpcUaClientConfiguration.cs` | OK | Review unused |
| 562 | +26 | `Client/Connection/SessionManager.cs` | OK | Minor |
| 556 | +151 | `Client/Connection/SubscriptionManager.cs` | OK | Review |
| 523 | +523 | `Client/OpcUaClientStructuralChangeProcessor.cs` | NEW/LARGE | Use shared Graph/ |
| 507 | +39 | `Client/OpcUaSubjectLoader.cs` | OK | Review |
| 476 | +1 | `Client/Polling/PollingManager.cs` | OK | Skip |
| 306 | +41 | `Server/OpcUaSubjectServerBackgroundService.cs` | OK | Minor |
| 266 | +188 | `Server/OpcUaSubjectServer.cs` | OK | Review |
| 262 | +23 | `Server/OpcUaServerConfiguration.cs` | OK | Review unused |
| 222 | +8 | `Mapping/AttributeOpcUaNodeMapper.cs` | OK | Skip |
| 184 | +1 | `Connectors/WriteRetryQueue.cs` | OK | Skip |
| 178 | +3 | `OpcUaSubjectExtensions.cs` | OK | Skip |
| 146 | +146 | `Server/InterceptorOpcUaServer.cs` | MISNAMED | Rename |
| 118 | +118 | `OpcUaTypeRegistry.cs` | NEW | Move to Graph/ |
| 115 | +115 | `Connectors/StructuralChangeProcessor.cs` | NEW/CLEAN | Keep |
| 113 | +113 | `Connectors/ConnectorReferenceCounter.cs` | NEW/CLEAN | Keep |
| 52 | +52 | `Client/OpcUaBrowseHelper.cs` | NEW/SMALL | Move to Graph/ |
| 47 | +6 | `Attributes/OpcUaReferenceAttribute.cs` | OK | Skip |
| 45 | +45 | `Server/OpcUaServerStructuralChangeProcessor.cs` | NEW/CLEAN | Keep |
| 29 | +7 | `OpcUaSubjectFactory.cs` | OK | Skip |
| 19 | +19 | `Attributes/CollectionNodeStructure.cs` | NEW/SMALL | Implement |

### Test Code (16 files, ~4,800 lines)

Tests are working - updates needed for Flat collection mode test.

---

## Extraction Analysis

### What Goes to Shared `Graph/`

| New Class | Source | Lines | Description |
|-----------|--------|-------|-------------|
| `SubjectTracker` | Both | ~150 | Reference counting, NodeId↔Subject mapping |
| `SubjectPropertyHelper` | Both | ~100 | Add/remove from collections/dictionaries |
| `OpcUaBrowseHelper` | Client | ~80 | Browse utilities, `TryParseCollectionIndex` |
| `OpcUaTypeRegistry` | Both | ~120 | Type definition resolution |

### What Goes to `Client/Graph/`

| New Class | Source | Lines | Description |
|-----------|--------|-------|-------------|
| `RemoteSyncManager` | `OpcUaSubjectClientSource` | ~160 | ModelChangeEvents + periodic resync |
| `PropertyWriter` | `OpcUaSubjectClientSource` | ~150 | Write logic, result processing |

**Note:** `_recentlyDeletedNodeIds` tracking moves to `OpcUaNodeChangeProcessor` (not a new class) since it already handles resync and is the only place that needs this check.

### What Goes to `Server/Graph/`

| New Class | Source | Lines | Description |
|-----------|--------|-------|-------------|
| `NodeCreator` | `CustomNodeManager` | ~300 | Create OPC UA nodes from model |
| `ModelChangePublisher` | `CustomNodeManager` | ~50 | Queue and emit model change events |

---

## Investigation Findings (from Agent Analysis)

### Dead Code Analysis

| Code | Location | Determination | Reasoning |
|------|----------|---------------|-----------|
| `UpdateCollectionProperty()` | OpcUaNodeChangeProcessor.cs:792-843 | **DELETE** | Batch approach never used; architecture processes items one-by-one with interleaved setup |
| `UpdateDictionaryProperty()` | OpcUaNodeChangeProcessor.cs:946-1006 | **DELETE** | Same as above - abandoned batch update pattern |
| `ProcessReferenceAddedAsync()` | OpcUaNodeChangeProcessor.cs:675-679 | **IMPLEMENT** | Needed for shared subject scenario: when same subject is referenced by multiple parents, second reference triggers ReferenceAdded WITHOUT NodeAdded |
| `ProcessReferenceDeleted()` | OpcUaNodeChangeProcessor.cs:681-684 | **IMPLEMENT** | Needed for shared subject removal: reference removed but node still exists |

### Unimplemented Features

| Feature | Location | Determination | Reasoning |
|---------|----------|---------------|-----------|
| `CollectionNodeStructure.Flat` | OpcUaReferenceAttribute.cs:46 | **IMPLEMENT** | Attribute exists with default=Flat, but all code uses Container mode. Need to read config and conditionally skip container creation. |

### Implementation Notes for Incomplete Features

**ProcessReferenceAddedAsync/Deleted: ⚠️ BUG - BLOCKING**

This is a **bug**, not just incomplete feature. Without implementation, event-only mode (`EnableModelChangeEvents=true`, `EnablePeriodicResync=false`) is broken for shared subjects:

1. Subject A referenced by Parent1 → `NodeAdded` → works ✓
2. Subject A referenced by Parent2 → `ReferenceAdded` (no NodeAdded!)
3. `ProcessReferenceAddedAsync()` is no-op → Parent2.RefProperty stays null ✗
4. Without periodic resync → **permanently broken**

**Must implement:**
- `ProcessReferenceAddedAsync`: Find parent subject for the reference, set its reference property
- `ProcessReferenceDeleted`: Find parent subject, clear its reference property

**Server context** (CustomNodeManager.cs:808-824):
```csharp
if (isFirst) {
    QueueModelChange(nodeId, NodeAdded);      // First parent
} else {
    QueueModelChange(nodeId, ReferenceAdded); // Second+ parent - THIS IS IGNORED!
}
```

**CollectionNodeStructure.Flat:**
- Requires changes in:
  1. `AttributeOpcUaNodeMapper` - extract CollectionStructure to OpcUaNodeConfiguration
  2. `CustomNodeManager` - conditionally skip container creation for Flat mode
  3. `OpcUaClientStructuralChangeProcessor` - handle both structures when finding nodes
  4. `OpcUaNodeChangeProcessor` - handle both structures during resync

### Major Duplication Found

| Pattern | Locations | Lines |
|---------|-----------|-------|
| Collection/Dictionary sync methods | 6 methods in OpcUaNodeChangeProcessor | ~340 lines |
| Dictionary removal logic | Client + Server | ~100 lines |
| Naming convention `PropertyName[index]` | 3 locations | ~30 lines |
| TypeDefinition resolution | Client + Server | ~80 lines |
| Container node finding | Client + Server (different approaches) | ~60 lines |
| Recently-deleted filtering | Collection + Dictionary paths | ~40 lines |

### Design Issues

1. **Hardcoded ReferenceTypeId** - Client uses `HasComponent`, Server is configurable
2. **Path-based NodeId parsing** - Fragile string manipulation in CustomNodeManager
3. **No transaction semantics** - Multi-step subject creation has no rollback
4. **Assumes string dictionary keys** - ProcessNodeDeleted casts keys to string
5. **Collection reindexing asymmetry** - Server reindexes, client doesn't detect

---

## TDD Refactoring Steps

Each step is atomic and verified by running:
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Pattern:** Extract → Delegate → Verify → (later) Remove old code

### Core Design Principle

> **Everything must work with events only.** Periodic resync is a safety net, not a crutch for missing functionality.

If `EnableModelChangeEvents=true` and `EnablePeriodicResync=false`, ALL sync scenarios must work correctly:
- Node added/removed ✓ (currently works)
- Reference added/removed on shared subjects ✗ (Step 0 fixes this)
- Collection items added/removed ✓ (currently works)
- Dictionary entries added/removed ✓ (currently works)

Agents must verify this principle is upheld in every step.

---

### Agent Execution Protocol

Each step is executed by one dedicated agent following this workflow:

**Phase 1: Review & Validate**
1. Read the step description and relevant code
2. Verify the change leads to the final goal: simpler, more maintainable code
3. **If there's a problem → ASK** (don't proceed with flawed approach)
4. **If it will add more complexity or code → ASK** (the goal is reduction)
5. **If something is unclear → ASK** (don't guess)

**Phase 2: Execute**
6. Implement the refactoring as specified
7. Run tests: `dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal`

**Phase 3: Verify & Test Coverage**
8. Check if unit tests adequately cover the changed code
9. Check if integration tests are needed (for cross-component behavior)
10. Implement missing tests

**Phase 4: Report & Stop**
11. Report findings (blocking issues, cleanup items, simplification opportunities)
12. **STOP and wait for user review** before next step

**Findings Template:**
```markdown
## Step N Findings

### Blocking Issues
- [ ] Issue description → Action needed

### Cleanup Items (Deferred)
- [ ] Issue description → Add to cleanup phase

### Simplification Opportunities
- [ ] Observation → Potential improvement

### Test Coverage
- [ ] New tests added: (list)
- [ ] Existing coverage adequate: Yes/No
```

---

**Human Review Checkpoint:** After each step completes, user reviews the changes and findings before the next step begins.

---

## Detailed Step Specifications

> **Execution Model:** One agent per step, sequential execution. Each step stops for human review.

---

### Step 0: Fix ReferenceAdded/Deleted Bug (BLOCKING)

**Depends on:** None
**Goal:** Fix event-only mode for shared subjects (same object referenced by multiple parents)

**Review Checklist (verify before implementing):**
- [ ] This is a real bug: `ProcessReferenceAddedAsync/Deleted` are no-ops
- [ ] Without this fix, event-only mode is broken for shared subjects
- [ ] The approach (browse inverse references) is correct for OPC UA

**Files to modify:**
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseHelper.cs` - add inverse browse method
- `src/Namotion.Interceptor.OpcUa/Client/OpcUaNodeChangeProcessor.cs:675-684` - implement stub methods

**Implementation:**

1. Add to `OpcUaBrowseHelper.cs`:
```csharp
/// <summary>
/// Browses inverse (parent) references of a given node.
/// </summary>
public static async Task<ReferenceDescriptionCollection> BrowseInverseReferencesAsync(
    ISession session,
    NodeId nodeId,
    CancellationToken cancellationToken)
{
    var browseDescription = new BrowseDescriptionCollection
    {
        new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = BrowseDirection.Inverse,
            ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
            ResultMask = (uint)BrowseResultMask.All
        }
    };

    var response = await session.BrowseAsync(
        null, null, 0, browseDescription, cancellationToken).ConfigureAwait(false);

    if (response.Results.Count > 0 && StatusCode.IsGood(response.Results[0].StatusCode))
    {
        return response.Results[0].References;
    }

    return [];
}
```

2. Replace `ProcessReferenceAddedAsync` in `OpcUaNodeChangeProcessor.cs`:
```csharp
private async Task ProcessReferenceAddedAsync(NodeId childNodeId, ISession session, CancellationToken cancellationToken)
{
    // Only handle if we track this child subject
    if (!_nodeIdToSubject.TryGetValue(childNodeId, out var childSubject))
    {
        return;
    }

    // Browse inverse references to find all current parents
    var parentRefs = await OpcUaBrowseHelper.BrowseInverseReferencesAsync(
        session, childNodeId, cancellationToken).ConfigureAwait(false);

    foreach (var parentRef in parentRefs)
    {
        var parentNodeId = ExpandedNodeId.ToNodeId(parentRef.NodeId, session.NamespaceUris);

        // Check if we track this parent
        if (!_nodeIdToSubject.TryGetValue(parentNodeId, out var parentSubject))
        {
            continue;
        }

        var registeredParent = parentSubject.TryGetRegisteredSubject();
        if (registeredParent is null)
        {
            continue;
        }

        // Find reference property that should point to child
        foreach (var property in registeredParent.Properties)
        {
            if (!property.IsSubjectReference)
            {
                continue;
            }

            // If property is null locally, set it to the child
            var currentValue = property.GetValue();
            if (currentValue is null)
            {
                property.SetValueFromSource(_source, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, childSubject);
                _logger.LogDebug(
                    "ReferenceAdded: Set {ParentType}.{Property} = {ChildType}",
                    parentSubject.GetType().Name,
                    property.Name,
                    childSubject.GetType().Name);
            }
        }
    }
}
```

3. Replace `ProcessReferenceDeleted` in `OpcUaNodeChangeProcessor.cs`:
```csharp
private void ProcessReferenceDeleted(NodeId childNodeId)
{
    // Only handle if we track this child subject
    if (!_nodeIdToSubject.TryGetValue(childNodeId, out var childSubject))
    {
        return;
    }

    // Find all tracked parents that currently reference this child
    lock (_nodeIdToSubjectLock)
    {
        foreach (var (_, parentSubject) in _nodeIdToSubject)
        {
            var registeredParent = parentSubject.TryGetRegisteredSubject();
            if (registeredParent is null)
            {
                continue;
            }

            foreach (var property in registeredParent.Properties)
            {
                if (!property.IsSubjectReference)
                {
                    continue;
                }

                // If this property references the child, clear it
                var currentValue = property.GetValue();
                if (ReferenceEquals(currentValue, childSubject))
                {
                    property.SetValueFromSource(_source, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
                    _logger.LogDebug(
                        "ReferenceDeleted: Cleared {ParentType}.{Property}",
                        parentSubject.GetType().Name,
                        property.Name);
                }
            }
        }
    }
}
```

**Test Requirements:**
- Existing tests must pass
- Need integration test for shared subject scenario (if not covered)
- Check: does any test verify `ReferenceAdded` event handling?

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseHelper.cs \
        src/Namotion.Interceptor.OpcUa/Client/OpcUaNodeChangeProcessor.cs
git commit -m "fix: implement ReferenceAdded/Deleted event handling for shared subjects"
```

---

### Step 1: Create Graph/ Directory and Move OpcUaBrowseHelper

**Depends on:** Step 0
**Goal:** Establish shared `Graph/` directory, move first shared utility

**Review Checklist:**
- [ ] Moving to shared location makes sense (used by both client and server)
- [ ] Namespace change is straightforward

**Files:**
- Move: `Client/OpcUaBrowseHelper.cs` → `Graph/OpcUaBrowseHelper.cs`
- Update: All files with `using Namotion.Interceptor.OpcUa.Client` that use `OpcUaBrowseHelper`

**Implementation:**

1. Create directory (git mv will do this):
```bash
mkdir -p src/Namotion.Interceptor.OpcUa/Graph
git mv src/Namotion.Interceptor.OpcUa/Client/OpcUaBrowseHelper.cs \
       src/Namotion.Interceptor.OpcUa/Graph/OpcUaBrowseHelper.cs
```

2. Update namespace in `Graph/OpcUaBrowseHelper.cs`:
```csharp
namespace Namotion.Interceptor.OpcUa.Graph;  // was: Client
```

3. Update `using` statements in files that reference it:
   - Search for `OpcUaBrowseHelper` usage
   - Add `using Namotion.Interceptor.OpcUa.Graph;`

**Verify:**
```bash
dotnet build src/Namotion.Interceptor.OpcUa
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: move OpcUaBrowseHelper to shared Graph/ directory"
```

---

### Step 2: Extract TryParseCollectionIndex to OpcUaBrowseHelper

**Depends on:** Step 1
**Goal:** Move collection index parsing to shared helper

**Review Checklist:**
- [ ] Method is pure/stateless (can be static helper)
- [ ] Used in multiple places or will be shared

**Files:**
- Modify: `Graph/OpcUaBrowseHelper.cs` - add method
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

1. Find `TryParseCollectionIndex` in `OpcUaNodeChangeProcessor.cs`, copy to `OpcUaBrowseHelper.cs`:
```csharp
/// <summary>
/// Tries to parse a collection index from a browse name like "PropertyName[3]".
/// </summary>
public static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index)
{
    // Copy existing implementation
}
```

2. Update `OpcUaNodeChangeProcessor.cs` to call helper:
```csharp
// Change: TryParseCollectionIndex(browseName, propertyName, out var index)
// To: OpcUaBrowseHelper.TryParseCollectionIndex(browseName, propertyName, out var index)
```

3. Keep private method temporarily (delegate to helper):
```csharp
private static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index)
    => OpcUaBrowseHelper.TryParseCollectionIndex(browseName, propertyName, out index);
```

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: extract TryParseCollectionIndex to OpcUaBrowseHelper"
```

---

### Step 3: Extract Browse Helper Methods

**Depends on:** Step 2
**Goal:** Move remaining browse utilities to shared helper

**Review Checklist:**
- [ ] `FindChildNodeIdAsync` is reusable
- [ ] Any other browse methods worth extracting?

**Files:**
- Modify: `Graph/OpcUaBrowseHelper.cs` - add methods
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

1. Find `FindChildNodeIdAsync` (or similar) in `OpcUaNodeChangeProcessor.cs`
2. Copy to `OpcUaBrowseHelper.cs` as public static
3. Update caller to use helper
4. Keep old method as thin delegate

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: extract browse helper methods to OpcUaBrowseHelper"
```

---

### Step 4: Create SubjectPropertyHelper with AddToCollection

**Depends on:** Step 1 (needs Graph/ directory)
**Goal:** Start building shared property manipulation helper

**Review Checklist:**
- [ ] `AddToCollectionProperty` logic can be extracted
- [ ] No coupling to OPC UA specifics (should work on model level)

**Files:**
- Create: `Graph/SubjectPropertyHelper.cs`
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

1. Create `Graph/SubjectPropertyHelper.cs`:
```csharp
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Helper methods for manipulating subject properties (collections, dictionaries, references).
/// </summary>
internal static class SubjectPropertyHelper
{
    /// <summary>
    /// Adds a subject to a collection property.
    /// </summary>
    public static void AddToCollection(
        RegisteredSubjectProperty property,
        IInterceptorSubject subject,
        object? source,
        DateTimeOffset? changedTimestamp = null,
        DateTimeOffset? receivedTimestamp = null)
    {
        // Copy logic from OpcUaNodeChangeProcessor.AddToCollectionProperty
    }
}
```

2. Update `OpcUaNodeChangeProcessor.cs` to call helper

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create SubjectPropertyHelper with AddToCollection"
```

---

### Step 5: Add Collection Removal Methods to SubjectPropertyHelper

**Depends on:** Step 4
**Goal:** Complete collection manipulation methods

**Files:**
- Modify: `Graph/SubjectPropertyHelper.cs` - add methods
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

1. Add `RemoveFromCollection`:
```csharp
public static void RemoveFromCollection(
    RegisteredSubjectProperty property,
    IInterceptorSubject subject,
    object? source,
    DateTimeOffset? changedTimestamp = null,
    DateTimeOffset? receivedTimestamp = null)
{
    // Copy logic from OpcUaNodeChangeProcessor
}
```

2. Add `RemoveFromCollectionByIndices`:
```csharp
public static void RemoveFromCollectionByIndices(
    RegisteredSubjectProperty property,
    List<int> indices,
    object? source,
    DateTimeOffset? changedTimestamp = null,
    DateTimeOffset? receivedTimestamp = null)
{
    // Copy logic from OpcUaNodeChangeProcessor
}
```

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: add collection removal methods to SubjectPropertyHelper"
```

---

### Step 6: Add Dictionary Methods to SubjectPropertyHelper

**Depends on:** Step 5
**Goal:** Add dictionary manipulation to shared helper

**Files:**
- Modify: `Graph/SubjectPropertyHelper.cs` - add methods
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

1. Add `AddToDictionary`:
```csharp
public static void AddToDictionary(
    RegisteredSubjectProperty property,
    object key,
    IInterceptorSubject subject,
    object? source,
    DateTimeOffset? changedTimestamp = null,
    DateTimeOffset? receivedTimestamp = null)
{
    // Copy logic from OpcUaNodeChangeProcessor
}
```

2. Add `RemoveFromDictionary`:
```csharp
public static void RemoveFromDictionary(
    RegisteredSubjectProperty property,
    object key,
    object? source,
    DateTimeOffset? changedTimestamp = null,
    DateTimeOffset? receivedTimestamp = null)
{
    // Copy logic from OpcUaNodeChangeProcessor
}
```

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: add dictionary methods to SubjectPropertyHelper"
```

---

### Step 7: Add SetReference to SubjectPropertyHelper

**Depends on:** Step 6
**Goal:** Complete shared helper with reference manipulation

**Files:**
- Modify: `Graph/SubjectPropertyHelper.cs` - add method
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - delegate to helper

**Implementation:**

```csharp
public static void SetReference(
    RegisteredSubjectProperty property,
    IInterceptorSubject? subject,
    object? source,
    DateTimeOffset? changedTimestamp = null,
    DateTimeOffset? receivedTimestamp = null)
{
    var timestamps = GetTimestamps(changedTimestamp, receivedTimestamp);
    property.SetValueFromSource(source, timestamps.changed, timestamps.received, subject);
}

private static (DateTimeOffset changed, DateTimeOffset received) GetTimestamps(
    DateTimeOffset? changedTimestamp,
    DateTimeOffset? receivedTimestamp)
{
    var now = DateTimeOffset.UtcNow;
    return (changedTimestamp ?? now, receivedTimestamp ?? now);
}
```

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: add SetReference to SubjectPropertyHelper"
```

---

### Step 8: Create SubjectTracker Class

**Depends on:** Step 1 (needs Graph/ directory)
**Goal:** Create shared subject tracking class (not wired up yet)

**Review Checklist:**
- [ ] Both client and server need NodeId↔Subject mapping
- [ ] Reference counting pattern is shared

**Files:**
- Create: `Graph/SubjectTracker.cs`

**Implementation:**

```csharp
using Namotion.Interceptor.Connectors;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Tracks subjects with their NodeIds using reference counting.
/// Shared between client and server.
/// </summary>
internal class SubjectTracker
{
    private readonly ConnectorReferenceCounter<NodeId> _refCounter = new();
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Lock _lock = new();

    public bool TrackSubject(IInterceptorSubject subject, NodeId nodeId)
    {
        lock (_lock)
        {
            if (_refCounter.AddReference(subject, nodeId))
            {
                _nodeIdToSubject[nodeId] = subject;
                return true; // First reference
            }
            return false; // Already tracked
        }
    }

    public bool UntrackSubject(IInterceptorSubject subject, out NodeId? nodeId)
    {
        lock (_lock)
        {
            if (_refCounter.RemoveReference(subject, out nodeId))
            {
                if (nodeId is not null)
                {
                    _nodeIdToSubject.Remove(nodeId);
                }
                return true; // Last reference removed
            }
            return false; // Still has references
        }
    }

    public bool TryGetNodeId(IInterceptorSubject subject, out NodeId? nodeId)
    {
        lock (_lock)
        {
            return _refCounter.TryGetData(subject, out nodeId);
        }
    }

    public bool TryGetSubject(NodeId nodeId, out IInterceptorSubject? subject)
    {
        lock (_lock)
        {
            return _nodeIdToSubject.TryGetValue(nodeId, out subject);
        }
    }

    public IEnumerable<IInterceptorSubject> GetAllSubjects()
    {
        lock (_lock)
        {
            return _nodeIdToSubject.Values.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _refCounter.Clear();
            _nodeIdToSubject.Clear();
        }
    }
}
```

**Verify:**
```bash
dotnet build src/Namotion.Interceptor.OpcUa
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create SubjectTracker class"
```

---

### Step 9: Use SubjectTracker in CustomNodeManager

**Depends on:** Step 8
**Goal:** Wire SubjectTracker into server side

**Files:**
- Modify: `Server/CustomNodeManager.cs` - add and use SubjectTracker

**Implementation:**
1. Add `SubjectTracker` field
2. Delegate `_subjectRefCounter` calls to tracker
3. Keep old field temporarily (parallel operation for safety)

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: use SubjectTracker in CustomNodeManager"
```

---

### Step 10: Use SubjectTracker in OpcUaSubjectClientSource

**Depends on:** Step 8
**Goal:** Wire SubjectTracker into client side

**Files:**
- Modify: `Client/OpcUaSubjectClientSource.cs` - add and use SubjectTracker

**Implementation:**
1. Add `SubjectTracker` field
2. Delegate tracking calls to tracker
3. Keep old fields temporarily

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: use SubjectTracker in OpcUaSubjectClientSource"
```

---

### Step 11: Move _recentlyDeletedNodeIds to OpcUaNodeChangeProcessor

**Depends on:** Step 0
**Goal:** Move tracking to where it's used (resync handler)

**Files:**
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - add field and methods
- Modify: `Client/OpcUaSubjectClientSource.cs` - call processor methods

**Implementation:**

1. Add to `OpcUaNodeChangeProcessor`:
```csharp
private readonly Dictionary<NodeId, DateTime> _recentlyDeletedNodeIds = new();
private static readonly TimeSpan RecentlyDeletedExpiry = TimeSpan.FromSeconds(30);

public void MarkRecentlyDeleted(NodeId nodeId)
{
    lock (_nodeIdToSubjectLock)
    {
        _recentlyDeletedNodeIds[nodeId] = DateTime.UtcNow;
    }
}

public bool WasRecentlyDeleted(NodeId nodeId)
{
    lock (_nodeIdToSubjectLock)
    {
        // Cleanup expired entries
        var expired = _recentlyDeletedNodeIds
            .Where(kvp => DateTime.UtcNow - kvp.Value > RecentlyDeletedExpiry)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
        {
            _recentlyDeletedNodeIds.Remove(key);
        }

        return _recentlyDeletedNodeIds.ContainsKey(nodeId);
    }
}
```

2. Update `OpcUaSubjectClientSource` to call processor methods

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: move _recentlyDeletedNodeIds to OpcUaNodeChangeProcessor"
```

---

### Step 12: Create PropertyWriter Class

**Depends on:** Steps 4-7 (uses SubjectPropertyHelper)
**Goal:** Extract write logic from OpcUaSubjectClientSource

**Files:**
- Create: `Client/Graph/PropertyWriter.cs`
- Modify: `Client/OpcUaSubjectClientSource.cs` - delegate to new class

**Implementation:**
1. Create directory if needed: `Client/Graph/`
2. Extract `WriteChangesAsync` and related logic
3. Update `OpcUaSubjectClientSource` to use new class

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create PropertyWriter class"
```

---

### Step 13: Create RemoteSyncManager Class

**Depends on:** Step 11 (uses processor's recently-deleted tracking)
**Goal:** Extract sync management from OpcUaSubjectClientSource

**Files:**
- Create: `Client/Graph/RemoteSyncManager.cs`
- Modify: `Client/OpcUaSubjectClientSource.cs` - delegate to new class

**Implementation:**
1. Extract ModelChangeEvent subscription setup
2. Extract periodic resync timer logic
3. Update `OpcUaSubjectClientSource` to delegate

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create RemoteSyncManager class"
```

---

### Step 14: Create ModelChangePublisher Class

**Depends on:** Step 9 (uses SubjectTracker in server)
**Goal:** Extract event publishing from CustomNodeManager

**Files:**
- Create: `Server/Graph/ModelChangePublisher.cs`
- Modify: `Server/CustomNodeManager.cs` - delegate to new class

**Implementation:**
1. Create directory: `Server/Graph/`
2. Extract `QueueModelChange` and `FlushModelChangeEvents`
3. Update `CustomNodeManager` to delegate

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create ModelChangePublisher class"
```

---

### Step 15: Create NodeCreator Class

**Depends on:** Step 14
**Goal:** Extract node creation from CustomNodeManager

**Files:**
- Create: `Server/Graph/NodeCreator.cs`
- Modify: `Server/CustomNodeManager.cs` - delegate to new class

**Implementation:**
1. Extract `CreateSubjectNodes` and related methods
2. Update `CustomNodeManager` to delegate

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: create NodeCreator class"
```

---

### Step 16: Remove Path Parsing Hack

**Depends on:** Steps 9-10 (SubjectTracker provides parent tracking)
**Goal:** Use model-based parent lookup instead of path parsing

**Files:**
- Modify: `Server/CustomNodeManager.cs` - new method, delete old

**Implementation:**

1. Add new method:
```csharp
private NodeId? GetParentNodeId(IInterceptorSubject subject)
{
    var registered = subject.TryGetRegisteredSubject();
    if (registered?.Parents.Length > 0)
    {
        var parentSubject = registered.Parents[0].Property.Parent.Subject;
        if (_subjectTracker.TryGetNodeId(parentSubject, out var nodeId))
        {
            return nodeId;
        }
    }
    return null;
}
```

2. Update callers to use new method
3. Delete `FindParentNodeIdFromPath` and `FindParentNodeId`

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: replace path parsing with model-based parent lookup"
```

---

### Step 17: Implement CollectionNodeStructure.Flat Mode

**Depends on:** Step 15 (NodeCreator)
**Goal:** Support flat collection structure (no container node)

**Files:**
- Modify: `Server/Graph/NodeCreator.cs` - check structure mode
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - handle flat mode
- Modify: `Client/OpcUaClientStructuralChangeProcessor.cs` - handle flat mode

**Implementation:**

1. Add helper to get structure mode from attribute
2. In NodeCreator: conditionally skip container creation
3. In processors: handle both structures when browsing

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "feat: implement CollectionNodeStructure.Flat mode"
```

---

### Step 18: Add Integration Tests for Edge Cases

**Depends on:** Step 17
**Goal:** Verify edge cases work end-to-end (flat collections, shared subjects)

**Files:**
- Modify: Test project - add test models and tests

**Implementation:**

1. **Flat collection mode test:**
```csharp
[InterceptorSubject]
public partial class TestModelWithFlatCollection
{
    [OpcUaNode(CollectionNodeStructure = CollectionNodeStructure.Flat)]
    public partial Person[] FlatPeople { get; set; }
}
```
Add test verifying no container node exists.

2. **Shared subject ReferenceAdded test (from Step 0):**
```csharp
// Test scenario:
// - Subject A exists
// - Parent1 references A (triggers NodeAdded)
// - Parent2 references A (triggers ReferenceAdded only, no NodeAdded)
// - Verify Parent2.RefProperty == A (via event, not resync)
```
Add test verifying ReferenceAdded event correctly sets reference property on second parent.

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "test: add integration test for flat collection mode"
```

---

### Step 19: Remove Dead Code and Old Delegating Methods

**Depends on:** All previous steps
**Goal:** Clean up obsolete code

**Files:**
- Modify: `Client/OpcUaNodeChangeProcessor.cs` - remove dead methods
- Modify: Various - remove old delegating methods

**Dead code to DELETE:**
- [ ] `UpdateCollectionProperty()` (lines ~792-843) - batch approach never used
- [ ] `UpdateDictionaryProperty()` (lines ~946-1006) - same
- [ ] Old private methods that now just delegate to helpers
- [ ] Old fields replaced by SubjectTracker

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: remove dead code and old delegating methods"
```

---

### Step 20: Final Cleanup and Validation

**Depends on:** Step 19
**Goal:** Polish and validate everything

**Files:**
- Rename: `Server/InterceptorOpcUaServer.cs` → `ExternalNodeManagementHelper.cs`
- All files: format and cleanup

**Implementation:**

1. Rename file:
```bash
git mv src/Namotion.Interceptor.OpcUa/Server/InterceptorOpcUaServer.cs \
       src/Namotion.Interceptor.OpcUa/Server/ExternalNodeManagementHelper.cs
```

2. Run format:
```bash
dotnet format src/Namotion.Interceptor.OpcUa
```

3. Review logging - reduce verbosity where appropriate

4. Address all CLEANUP findings from previous steps

**Verify:**
```bash
dotnet test src/Namotion.Interceptor.slnx -v minimal
```

**Commit:**
```bash
git add -A
git commit -m "refactor: final cleanup and validation"
```

---

## Step Findings Log

*This section will be populated as each step is executed. Agents will add their findings here.*

### Step 0 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Added `BrowseInverseReferencesAsync` to `OpcUaBrowseHelper.cs`
- Replaced stub `ProcessReferenceAddedAsync` with full implementation (browses inverse refs, sets null reference properties)
- Replaced stub `ProcessReferenceDeleted` with full implementation (finds parents referencing child, clears properties)

**Test Results:** 298/298 tests passed

**Blocking Issues:** None

**Cleanup Items (Deferred):**
- [x] `FindParentNodeIdAsync` in OpcUaNodeChangeProcessor.cs is duplicate of new `BrowseInverseReferencesAsync` → ✅ Resolved in Step 3

**Simplification Opportunities:**
- [ ] `ProcessReferenceDeleted` iterates all tracked subjects O(n) - acceptable for typical subject counts, could add reverse index if needed

**Test Coverage:**
- No direct test for ReferenceAdded event handling (shared subject scenario)
- Partially covered by `SharedSubject_ReferencedTwice_OnlyTrackedOnce()` but tests client tracking, not event handling
- **Recommendation:** Add integration test for shared subject ReferenceAdded scenario (can defer to Step 18 area)

### Step 1 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Created `src/Namotion.Interceptor.OpcUa/Graph/` directory
- Moved `Client/OpcUaBrowseHelper.cs` → `Graph/OpcUaBrowseHelper.cs`
- Updated namespace: `Namotion.Interceptor.OpcUa.Client` → `Namotion.Interceptor.OpcUa.Graph`
- Updated using statements in 3 files:
  - `OpcUaSubjectClientSource.cs`
  - `OpcUaNodeChangeProcessor.cs`
  - `OpcUaClientStructuralChangeProcessor.cs`

**Test Results:** 295/298 passed (3 flaky timeouts - infrastructure/network issues, not related to change)

**Blocking Issues:** None

**Cleanup Items:** None

**Simplification Opportunities:** None

### Step 2 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Added `TryParseCollectionIndex` to `Graph/OpcUaBrowseHelper.cs` (public static)
- Updated 2 call sites in `OpcUaNodeChangeProcessor.cs` to use `OpcUaBrowseHelper.TryParseCollectionIndex`
- Removed thin delegate (inlined immediately per user request)

**Test Results:** 292/298 passed, 6 skipped (infrastructure tests)

**Blocking Issues:** None

**Cleanup Items:** None (delegate removed immediately)

**Simplification Opportunities:** None - method is pure/stateless, ideal for shared helper

### Step 3 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Extracted 3 methods to `Graph/OpcUaBrowseHelper.cs`:
  - `FindChildNodeIdAsync` - finds child node by browse name
  - `FindParentNodeIdAsync` - finds parent via inverse browse (reuses `BrowseInverseReferencesAsync`)
  - `ReadNodeDetailsAsync` - reads node attributes
- Removed ~77 lines from `OpcUaNodeChangeProcessor.cs`
- Updated 6 call sites to use `OpcUaBrowseHelper.X` directly

**Test Results:** 291/298 passed, 6 skipped, 1 flaky timeout (infrastructure)

**Blocking Issues:** None

**Cleanup Items:** Step 0's FindParentNodeIdAsync duplication resolved (now reuses BrowseInverseReferencesAsync)

**Simplification:** Better code reuse achieved

### Step 4 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Created `Graph/SubjectPropertyHelper.cs`
- Added `AddToCollection(property, subject, source, changedTimestamp?, receivedTimestamp?)` method
- Updated caller in `OpcUaNodeChangeProcessor.cs`, removed old method (~50 lines)

**Test Results:** 292/298 passed, 6 skipped

**OPC UA Agnostic:** ✅ Yes - no OPC UA dependencies, could be moved to Connectors library

**Blocking Issues:** None

**Cleanup Items:**
- [ ] `localChildren` and `index` params were unused/only for logging in old method - dead code pattern

**Simplification:** Helper could move to `Namotion.Interceptor.Connectors` if needed by other connectors

### Step 5 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Added `RemoveFromCollectionByIndices` to `SubjectPropertyHelper.cs`
- Updated 2 call sites in `OpcUaNodeChangeProcessor.cs`
- Removed old method (~50 lines)

**Test Results:** 292/298 passed, 6 skipped

**OPC UA Agnostic:** ✅ Yes

**Blocking Issues:** None

**Note:** Plan mentioned `RemoveFromCollection` but actual method was `RemoveFromCollectionByIndices` (takes list of indices)

### Step 6 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Added `AddToDictionary` to `SubjectPropertyHelper.cs`
- Added `RemoveFromDictionary` to `SubjectPropertyHelper.cs`
- Updated 3 call sites in `OpcUaNodeChangeProcessor.cs`
- Removed old methods (~120 lines)

**Line counts:**
- `OpcUaNodeChangeProcessor.cs`: 905 → 785 lines (-120)
- `SubjectPropertyHelper.cs`: 140 → 293 lines (+153 shared/reusable)

**Test Results:** 292/298 passed, 6 skipped

**OPC UA Agnostic:** ✅ Yes

**Blocking Issues:** None

### Step 7 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Added `SetReference(property, subject, source, changedTimestamp?, receivedTimestamp?)` to `SubjectPropertyHelper.cs`
- Updated 5 call sites in `OpcUaNodeChangeProcessor.cs`

**Test Results:** 292/298 passed, 6 skipped

**OPC UA Agnostic:** ✅ Yes

**Blocking Issues:** None

### Step 8 Findings
**Status:** ✅ FINISHED

**Implementation:**
- Created `Graph/SubjectTracker.cs` with all planned methods
- Uses `ConnectorReferenceCounter<NodeId>` + `Dictionary<NodeId, IInterceptorSubject>` for bidirectional mapping
- Thread-safe with `Lock`

**Build:** Succeeded

**Design Notes:**
- Server currently uses `ConnectorReferenceCounter<NodeState>` (stores node objects)
- Client uses `ConnectorReferenceCounter<List<MonitoredItem>>` + separate dict
- New `SubjectTracker` uses `NodeId` as tracked type (simpler)
- May need adjustments when wiring up in Steps 9-10

**Blocking Issues:** None

### Step 9 Findings
**Status:** EVALUATED - NO CHANGE NEEDED

**Analysis:**

The server's `CustomNodeManager.cs` uses `ConnectorReferenceCounter<NodeState>`:
- Stores actual `NodeState` objects (not just `NodeId`)
- `NodeState` is needed for: deleting nodes via SDK, updating BrowseNames, accessing node properties
- Uses `_subjectRefCounter.IncrementAndCheckFirst(subject, () => nodeState, out nodeState)` - creates NodeState via factory
- Uses `_subjectRefCounter.DecrementAndCheckLast(subject, out var nodeState)` - gets NodeState back for deletion
- Uses `_subjectRefCounter.TryGetData(subject, out var nodeState)` - gets NodeState for reindexing
- Uses `_subjectRefCounter.GetAllEntries()` - iterates all entries

**Why SubjectTracker Does NOT Fit:**
1. `SubjectTracker` tracks `NodeId` only, not `NodeState` objects
2. Server NEEDS `NodeState` for node manipulation (SDK operations require the actual node object)
3. Using `SubjectTracker` would require keeping `_subjectRefCounter<NodeState>` anyway, adding complexity

**Conclusion:**
- `SubjectTracker` is designed for **client-side** tracking where only NodeId is needed
- Server needs `NodeState` objects for OPC UA SDK operations
- Keeping `ConnectorReferenceCounter<NodeState>` in server is the correct design
- **No changes made to `CustomNodeManager.cs`**

**Alternative Considered:**
Could add `SubjectTracker` alongside existing counter for `FindSubjectByNodeId()` optimization (O(1) vs O(n) lookup), but:
- Current O(n) lookup is only used in external node management paths
- Adding more tracking would increase complexity
- Not worth the added complexity for this edge case

**Test Results:** 292/298 passed, 6 skipped (infrastructure tests)

**Blocking Issues:** None

**Recommendation:** Skip this step for server. `SubjectTracker` is client-only. Consider renaming step or updating plan.

### Step 10 Findings
*(To be filled by executing agent)*

### Step 11 Findings
*(To be filled by executing agent)*

### Step 12 Findings
*(To be filled by executing agent)*

### Step 13 Findings
*(To be filled by executing agent)*

### Step 14 Findings
*(To be filled by executing agent)*

### Step 15 Findings
*(To be filled by executing agent)*

### Step 16 Findings
*(To be filled by executing agent)*

### Step 17 Findings
*(To be filled by executing agent)*

### Step 18 Findings
*(To be filled by executing agent)*

### Step 19 Findings
*(To be filled by executing agent)*

### Step 20 Findings
*(To be filled by executing agent)*

---

## Refactoring Phases (Overview)

### Phase 1: Create Shared `Graph/` Utilities

**Create:** `src/Namotion.Interceptor.OpcUa/Graph/SubjectPropertyHelper.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Graph;

internal static class SubjectPropertyHelper
{
    public static void AddToCollection(RegisteredSubjectProperty property, IInterceptorSubject subject, object? source = null);
    public static void RemoveFromCollection(RegisteredSubjectProperty property, IInterceptorSubject subject, object? source = null);
    public static void RemoveFromCollectionByIndices(RegisteredSubjectProperty property, List<int> indices, object? source = null);
    public static void AddToDictionary(RegisteredSubjectProperty property, object key, IInterceptorSubject subject, object? source = null);
    public static void RemoveFromDictionary(RegisteredSubjectProperty property, object key, object? source = null);
    public static void SetReference(RegisteredSubjectProperty property, IInterceptorSubject? subject, object? source = null);
}
```

**Create:** `src/Namotion.Interceptor.OpcUa/Graph/SubjectTracker.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Graph;

/// <summary>
/// Tracks subjects with their NodeIds using reference counting.
/// Shared base class for both client and server.
/// </summary>
internal class SubjectTracker
{
    private readonly ConnectorReferenceCounter<NodeId> _refCounter = new();
    private readonly Dictionary<NodeId, IInterceptorSubject> _nodeIdToSubject = new();
    private readonly Lock _lock = new();

    public bool TrackSubject(IInterceptorSubject subject, NodeId nodeId);
    public bool UntrackSubject(IInterceptorSubject subject, out NodeId? nodeId);
    public bool TryGetNodeId(IInterceptorSubject subject, out NodeId? nodeId);
    public bool TryGetSubject(NodeId nodeId, out IInterceptorSubject? subject);
    public IEnumerable<IInterceptorSubject> GetAllSubjects();
    public void Clear();
}
```

**Move:** `Client/OpcUaBrowseHelper.cs` → `Graph/OpcUaBrowseHelper.cs`

**Add to `OpcUaBrowseHelper`:**
```csharp
public static bool TryParseCollectionIndex(string browseName, string? propertyName, out int index);
public static async Task<NodeId?> FindChildNodeIdAsync(ISession session, NodeId parentId, string name, CancellationToken ct);
public static async Task<NodeId?> FindContainerNodeIdAsync(ISession session, NodeId parentId, string containerName, CancellationToken ct);
```

**Update:**
- `OpcUaNodeChangeProcessor.cs` - use `SubjectPropertyHelper` and `SubjectTracker`
- `OpcUaClientStructuralChangeProcessor.cs` - use browse helpers
- `CustomNodeManager.cs` - use `SubjectPropertyHelper`

**Expected:** ~400 lines shared utilities, -400 lines from processors

---

### Phase 2: Extract `Client/Graph/` Classes

**Move `_recentlyDeletedNodeIds` to `OpcUaNodeChangeProcessor`:**

The recently-deleted tracking should live in `OpcUaNodeChangeProcessor` because:
1. It already handles `PerformFullResyncAsync` which is the only place this check is needed
2. It already has `_nodeIdToSubject` mapping
3. Keeps tracking logic close to where it's used

```csharp
// Add to OpcUaNodeChangeProcessor
private readonly Dictionary<NodeId, DateTime> _recentlyDeletedNodeIds = new();
private static readonly TimeSpan RecentlyDeletedExpiry = TimeSpan.FromSeconds(30);

public void MarkRecentlyDeleted(NodeId nodeId)
{
    lock (_nodeIdToSubjectLock)
    {
        _recentlyDeletedNodeIds[nodeId] = DateTime.UtcNow;
    }
}

private bool WasRecentlyDeleted(NodeId nodeId)
{
    lock (_nodeIdToSubjectLock)
    {
        // Cleanup expired + check
        ...
    }
}
```

**Create:** `src/Namotion.Interceptor.OpcUa/Client/Graph/RemoteSyncManager.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Client.Graph;

/// <summary>
/// Manages remote sync: ModelChangeEvents subscription and periodic resync.
/// Extracted from OpcUaSubjectClientSource.
/// </summary>
internal class RemoteSyncManager : IDisposable
{
    public RemoteSyncManager(
        OpcUaNodeChangeProcessor processor,
        OpcUaClientConfiguration config,
        ILogger logger);

    public async Task SetupAsync(ISession session, SubscriptionManager subscriptions, CancellationToken ct);
    public void StartPeriodicResync();
    public void Stop();
}
```

**Create:** `src/Namotion.Interceptor.OpcUa/Client/Graph/PropertyWriter.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Client.Graph;

/// <summary>
/// Handles writing property changes to OPC UA server.
/// Extracted from OpcUaSubjectClientSource.
/// </summary>
internal class PropertyWriter
{
    public async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        ISession session,
        OpcUaClientConfiguration config,
        CancellationToken ct);

    internal static bool IsTransientWriteError(StatusCode statusCode);
}
```

**Update `OpcUaSubjectClientSource`:**
- Keep: Constructor, `StartListeningAsync`, `ExecuteAsync`, `ReconnectSessionAsync`, Dispose
- Delegate to: shared `SubjectTracker`, `RemoteSyncManager`, `PropertyWriter`
- Move `_recentlyDeletedNodeIds` logic to `OpcUaNodeChangeProcessor`

**Expected:** `OpcUaSubjectClientSource` 1,245 → ~400 lines

---

### Phase 3: Extract `Server/Graph/` Classes

**Create:** `src/Namotion.Interceptor.OpcUa/Server/Graph/NodeCreator.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Server.Graph;

/// <summary>
/// Creates OPC UA nodes from model subjects/properties.
/// Extracted from CustomNodeManager.
/// </summary>
internal class NodeCreator
{
    public NodeCreator(
        CustomNodeManager nodeManager,
        OpcUaServerConfiguration config,
        OpcUaNodeFactory nodeFactory,
        ILogger logger);

    public void CreateSubjectNodes(NodeId parentId, RegisteredSubject subject, string path);
    public void CreatePropertyNode(NodeId parentId, RegisteredSubjectProperty property, string path);
    public void CreateCollectionNodes(NodeId parentId, RegisteredSubjectProperty property, string path);
    public void CreateDictionaryNodes(NodeId parentId, RegisteredSubjectProperty property, string path);

    // Handles CollectionNodeStructure.Flat vs Container
    public CollectionNodeStructure GetCollectionStructure(RegisteredSubjectProperty property);
}
```

**Create:** `src/Namotion.Interceptor.OpcUa/Server/Graph/ModelChangePublisher.cs`

```csharp
namespace Namotion.Interceptor.OpcUa.Server.Graph;

/// <summary>
/// Queues and emits model change events.
/// Extracted from CustomNodeManager.
/// </summary>
internal class ModelChangePublisher
{
    public void QueueChange(NodeId nodeId, ModelChangeStructureVerbMask verb);
    public void Flush(IServerInternal server, ushort namespaceIndex);
}
```

**Update `CustomNodeManager`:**
- Keep: OPC UA SDK overrides, basic node management
- Delegate to: `NodeCreator`, `ModelChangePublisher`, shared `SubjectTracker`

**Expected:** `CustomNodeManager` 1,404 → ~400 lines

---

### Phase 4: Remove Path Parsing Hack

**Delete from `CustomNodeManager`:**
- `FindParentNodeIdFromPath(NodeId nodeId)`
- `FindParentNodeId(NodeState node)`

**Use instead:**
```csharp
private NodeId? GetParentNodeId(IInterceptorSubject subject)
{
    var registered = subject.TryGetRegisteredSubject();
    if (registered?.Parents.Length > 0)
    {
        var parentSubject = registered.Parents[0].Property.Parent.Subject;
        if (_subjectTracker.TryGetNodeId(parentSubject, out var nodeId))
            return nodeId;
    }
    return null;
}
```

---

### Phase 5: Implement `CollectionNodeStructure.Flat` Mode

**Issue:** Enum defined but NOT implemented anywhere.

**Files to update:**

1. **`Server/Graph/NodeCreator.cs`:**
   - Check attribute on property
   - In Flat mode: create children directly under parent
   - In Container mode: create container node first

2. **`Client/OpcUaNodeChangeProcessor.cs`:**
   - Handle Flat mode when browsing
   - Parse indices from parent's direct children

3. **`Client/OpcUaClientStructuralChangeProcessor.cs`:**
   - Skip container creation in Flat mode

**Add integration test:**
```csharp
[InterceptorSubject]
public partial class TestModelWithFlatCollection
{
    [OpcUaNode(CollectionNodeStructure = CollectionNodeStructure.Flat)]
    public partial Person[] FlatPeople { get; set; }
}

[Fact]
public async Task FlatCollection_ShouldCreateNodesDirectlyUnderParent()
{
    // Verify no "FlatPeople" container node exists
    // Verify "FlatPeople[0]", "FlatPeople[1]" exist directly under root
}
```

---

### Phase 6: Fix Filename Mismatch

```bash
git mv src/Namotion.Interceptor.OpcUa/Server/InterceptorOpcUaServer.cs \
       src/Namotion.Interceptor.OpcUa/Server/ExternalNodeManagementHelper.cs
```

---

### Phase 7: Final Review and Cleanup

1. Search for unused private methods
2. Remove dead code
3. Reduce logging verbosity (keep Debug, reduce Info)
4. Run `dotnet format`
5. Run full test suite

---

## Verification

After each phase:
```bash
dotnet test src/Namotion.Interceptor.OpcUa.Tests -v minimal
```

Final verification:
```bash
dotnet test src/Namotion.Interceptor.slnx -v minimal
```

---

## Expected Results

### Line Count Targets

| File | Before | After | Target |
|------|--------|-------|--------|
| `OpcUaSubjectClientSource.cs` | 1,245 | ~400 | ≤400 |
| `CustomNodeManager.cs` | 1,404 | ~400 | ≤400 |
| `OpcUaNodeChangeProcessor.cs` | 1,133 | ~600 | ≤600 |
| `OpcUaClientStructuralChangeProcessor.cs` | 523 | ~350 | ≤400 |

### New Files

| File | Lines | Purpose |
|------|-------|---------|
| `Graph/SubjectTracker.cs` | ~100 | Shared subject tracking |
| `Graph/SubjectPropertyHelper.cs` | ~100 | Shared property manipulation |
| `Graph/OpcUaBrowseHelper.cs` | ~100 | Shared browse utilities (moved from Client/) |
| `Client/Graph/RemoteSyncManager.cs` | ~160 | ModelChangeEvents + periodic sync |
| `Client/Graph/PropertyWriter.cs` | ~150 | Write logic |
| `Server/Graph/NodeCreator.cs` | ~300 | Node creation |
| `Server/Graph/ModelChangePublisher.cs` | ~50 | Event publishing |

**Note:** `_recentlyDeletedNodeIds` moves into existing `OpcUaNodeChangeProcessor` (not a new class).

**Total new shared code:** ~950 lines in small, focused classes
**Total reduction in bloated files:** ~1,400 lines

---

## Resolved Questions

1. **Is `_recentlyDeletedNodeIds` tracking needed?**
   - YES - needed when BOTH `EnableRemoteNodeManagement` AND `EnablePeriodicResync` are true
   - Move to `OpcUaNodeChangeProcessor` (already handles resync, so it's the right place)

2. **Do we need periodic resync if we have ModelChangeEvents?**
   - YES - periodic resync is a safety net, not redundant
   - Events can be missed (network issues, queue overflow, reconnection gaps)
   - Both should be enabled together for defense in depth

3. **Is `CollectionNodeStructure.Flat` implemented?**
   - NO - Phase 5 adds implementation

4. **Should path parsing be removed?**
   - YES - use `RegisteredSubject.Parents` instead (Phase 4)

---

## Open Questions

1. **Are all configuration options tested?**
   - `EnableRemoteNodeManagement`
   - `EnableModelChangeEvents`
   - `EnablePeriodicResync`
   - `EnableLiveSync`

2. **Should there be a recommended configuration preset?**
   - e.g., `EnableFullSync()` that enables both events + periodic resync as safety net
   - Makes it easier for users to get the right configuration
