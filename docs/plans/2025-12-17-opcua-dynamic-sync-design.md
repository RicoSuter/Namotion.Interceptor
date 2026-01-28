# OPC UA Dynamic Address Space Synchronization

## Overview

**Problem:** Currently, both OPC UA client and server only sync structure once at startup. Changes after initialization (local model changes or remote address space changes) are not reflected.

**Goals:**
1. **Bidirectional live sync** - Local changes update OPC UA, remote changes update local model
2. **Unified sync logic** - Single code path for initial load and live updates (idempotent)
3. **Shared infrastructure** - Push abstractions to Connectors library for reuse by other protocols
4. **Scoped processing** - Only process changes for relevant sub-graph (not global lifecycle events)

**Non-goals:**
- Changing the existing public API surface
- Supporting OPC UA method calls (only structure/values)

---

## Architecture

### Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Change detection | Property change queue (not lifecycle events) | Scoped to sub-graph via `propertyFilter`, not global |
| Structural vs value | Branch on `IsSubjectReference`, `IsSubjectCollection`, `IsSubjectDictionary` | Existing properties, no new abstractions needed |
| Collection diffing | Use existing `CollectionDiffBuilder` | Already in Connectors library |
| Loop prevention | Use existing `change.Source` mechanism | Mark changes from OPC UA to prevent sync-back |
| Initial vs incremental | Same code path, initial = "sync with empty old state" | Idempotent, simplifies implementation |

### The Four Cases

| Direction | Client | Server |
|-----------|--------|--------|
| **Model → OPC UA** | AddNodes/DeleteNodes + MonitoredItems | Create/delete nodes in address space |
| **OPC UA → Model** | ModelChangeEvent / browse → create/remove subjects | AddNodes/DeleteNodes handler → create/remove subjects |

### Core Methods (Symmetric Design)

| Direction | Method | Trigger | Unit |
|-----------|--------|---------|------|
| **Model → OPC UA** | `ProcessPropertyChangeAsync` | Property change queue | Property |
| **OPC UA → Model** | `ProcessNodeChangeAsync` | ModelChangeEvent / browse / AddNodes | Property |

Both methods:
- Are **property-level** (subject-level is just iteration via extension)
- Are **idempotent** (safe to call anytime, diff-based)
- Branch on `IsSubjectReference` / `IsSubjectCollection` / `IsSubjectDictionary`
- Handle the `else` case as value changes (existing logic)

### OPC UA Mapping for Collections and Dictionaries

Collections and dictionaries map to OPC UA structures differently:

| .NET Type | OPC UA Structure | Identity | BrowseName Source | Example |
|-----------|------------------|----------|-------------------|---------|
| `IList<T>` (Flat) | Direct children on parent | NodeId mapping | `PropertyName[index]` | `Parent/Sensors[0], Sensors[1]` |
| `IList<T>` (Container) | Container with child nodes | NodeId mapping | `PropertyName[index]` | `Parent/Sensors/Sensors[0], Sensors[1]` |
| `IDictionary<K,T>` | Container with child nodes (always) | BrowseName = dictionary key | Dictionary key (string) | `Machines/Machine1, Machine2` |
| Single reference | Object node | Subject reference | From subject/property | `Identification` |

**Per OPC UA Machinery Companion Spec:** The `Machines` dictionary maps to a folder where each machine's BrowseName is the dictionary key (e.g., `Machines/MyMachine`).

**Collections vs Dictionaries for OPC UA:**
- **Dictionaries** always use container nodes (keys could conflict with property names)
- **Collections** default to flat structure (children directly on parent)
- Collections can optionally use container structure for compatibility

### Collection Node Structure

Collections support two node structures, configured via `[OpcUaReference]`:

```csharp
public enum CollectionNodeStructure
{
    Flat,       // Default: Parent/Machines[0] - no intermediate node
    Container   // Parent/Machines/Machines[0] - intermediate container node
}
```

**Flat (default):**
```
Plant/
  ├── Machines[0]/        ← Direct children of Plant
  ├── Machines[1]/
  ├── Status              ← Regular property (no conflict - different pattern)
```
Path: `Plant.Machines[0].Temp`

**Container:**
```
Plant/
  └── Machines/           ← Container node (FolderType by default)
        ├── Machines[0]/
        ├── Machines[1]/
```
Path: `Plant.Machines.Machines[0].Temp`

**Configuration examples:**
```csharp
// Flat (default) - children directly on parent
public partial IList<Machine> Machines { get; set; }

// Container with default FolderType
[OpcUaReference(CollectionStructure = CollectionNodeStructure.Container)]
public partial IList<Machine> Machines { get; set; }

// Container with custom TypeDefinition
[OpcUaNode(TypeDefinition = "CustomContainerType")]
[OpcUaReference(CollectionStructure = CollectionNodeStructure.Container)]
public partial IList<Machine> Machines { get; set; }
```

**Rules:**
- `[OpcUaReference]` controls `Flat` vs `Container`
- `[OpcUaNode]` configures the container node (ignored when `Flat`)
- Container defaults to FolderType if no `[OpcUaNode]` specified
- Dictionaries always use Container (no option) - keys could conflict with property names

**OPC UA → Model matching:**
- **Flat:** Pattern match children by `PropertyName[\d+]` regex on parent
- **Container:** Browse container node for children (existing logic)

**Null vs Empty Semantics:**

| C# Value | Flat Collection | Container Collection | Dictionary (always Container) |
|----------|-----------------|---------------------|-------------------------------|
| `null` | No children on parent | No container node | No container node |
| Empty `[]` or `{}` | No children on parent | Container with 0 children | Container with 0 children |

**On load from OPC UA:**
- Flat: No matching `PropertyName[*]` children → empty collection (not null)
- Container: Container with 0 children → empty collection (not null)
- Container: No container node found → null

**Note:** For Flat collections, null and empty are indistinguishable in OPC UA (both result in no children). If you need to distinguish null from empty, use Container structure.

**Shared Subject BrowseName:**
When the same subject instance is referenced from multiple places (e.g., two dictionaries with different keys), the subject gets ONE OPC UA node. The BrowseName is determined by **first-reference-wins** - whichever property processes the subject first sets the BrowseName.

**Dictionary Key Handling:**
Dictionary keys are converted to strings via `ToString()` for the OPC UA BrowseName. On load, `BrowseName.Name` becomes the dictionary key (always string). Non-string key types must have consistent `ToString()` formatting.

### Shared Infrastructure (Connectors Library)

**Existing (no changes needed):**
- `CollectionDiffBuilder` - diffs collections and dictionaries
- `ChangeQueueProcessor` - processes property changes with filtering
- `DefaultSubjectFactory` - creates subjects

**New (to be added):**
- `ConnectorReferenceCounter<TData>` - connector-scoped reference counting with associated data
- `StructuralChangeProcessor` - property type branching base class
- `SubjectGraphSynchronizer<TData>` - recursive sync with ref counting

**Existing properties used:**
- `RegisteredSubjectProperty.IsSubjectReference` - single subject reference
- `RegisteredSubjectProperty.IsSubjectCollection` - collection of subjects
- `RegisteredSubjectProperty.IsSubjectDictionary` - dictionary of subjects

### Subject Identity Tracking

For OPC UA → Model synchronization, we need to match remote OPC UA nodes to local subjects. Different property types use different identity mechanisms:

| Property Type | Identity Mechanism | Rationale |
|---------------|-------------------|-----------|
| Single reference | Subject object reference | Only one child, trivial match |
| Dictionary | BrowseName = dictionary key | Keys are stable identifiers |
| Collection | NodeId mapping | Index is unstable; NodeId is stable |

**Collection Identity via NodeId Mapping:**

Collections are unordered in OPC UA (folders with references). Index-based matching is unreliable because browse order isn't guaranteed. Instead, we maintain a `Subject → NodeId` mapping:

```csharp
// Stored when subject is first synced to OPC UA
private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();

// On first sync (Model → OPC UA or OPC UA → Model):
_subjectToNodeId[subject] = nodeId;

// On resync (OPC UA → Model), match by NodeId:
var localSubject = _subjectToNodeId
    .FirstOrDefault(kvp => kvp.Value == remoteNodeId).Key;
```

**When mapping is established:**
- **Model → OPC UA:** When node is created for subject
- **OPC UA → Model:** When subject is created from node

**Cleanup:**
- When subject is removed (ref count → 0), remove from mapping
- On reconnect, clear mapping and rebuild during full resync

---

## Model → OPC UA Direction

### Change Processing Logic

When a property change is received from the change queue:

```csharp
async Task ProcessPropertyChangeAsync(SubjectPropertyChange change, RegisteredSubjectProperty property)
{
    // Skip if change came from OPC UA (loop prevention)
    if (change.Source == _opcUaSource)
        return;

    if (property.IsSubjectReference)
    {
        // Single subject: compare old vs new
        var oldSubject = change.GetOldValue<IInterceptorSubject?>();
        var newSubject = change.GetNewValue<IInterceptorSubject?>();

        if (oldSubject is not null && !ReferenceEquals(oldSubject, newSubject))
            await OnSubjectRemovedAsync(property, oldSubject, index: null);
        if (newSubject is not null && !ReferenceEquals(oldSubject, newSubject))
            await OnSubjectAddedAsync(property, newSubject, index: null);
    }
    else if (property.IsSubjectCollection)
    {
        // Collection: use differ (matches by subject identity, not index)
        var oldCollection = change.GetOldValue<IReadOnlyList<IInterceptorSubject>?>() ?? [];
        var newCollection = change.GetNewValue<IReadOnlyList<IInterceptorSubject>?>() ?? [];

        _diffBuilder.GetCollectionChanges(oldCollection, newCollection,
            out var operations, out var newItems, out var reorderedItems);

        // 1. Process removes (descending index order)
        foreach (var op in operations ?? [])
        {
            if (op.Action == SubjectCollectionOperationType.Remove)
            {
                var subject = oldCollection[(int)op.Index];
                await OnSubjectRemovedAsync(property, subject, op.Index);
            }
        }

        // 2. Process adds (truly new subjects)
        foreach (var (index, subject) in newItems ?? [])
        {
            await OnSubjectAddedAsync(property, subject, index);
        }

        // 3. Reorders are no-op for OPC UA
        // Whether Flat or Container, OPC UA has no ordering concept
        // Order is a .NET concept that doesn't translate to OPC UA
        // (reorderedItems ignored)
    }
    else if (property.IsSubjectDictionary)
    {
        // Dictionary: use differ
        var oldDict = change.GetOldValue<IDictionary?>();
        var newDict = change.GetNewValue<IDictionary?>() ?? new Dictionary<object, object>();

        _diffBuilder.GetDictionaryChanges(oldDict, newDict,
            out var operations, out var newItems, out var removedKeys);

        // removedKeys contains keys to remove
        var oldChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
        foreach (var key in removedKeys ?? [])
        {
            if (oldChildren.TryGetValue(key, out var subject))
                await OnSubjectRemovedAsync(property, subject, key);
        }

        // newItems contains (key, subject) pairs to add
        foreach (var (key, subject) in newItems ?? [])
        {
            await OnSubjectAddedAsync(property, subject, key);
        }
    }
    else
    {
        // Regular value change - existing logic
        await UpdateValueAsync(change);
    }
}
```

### Client Implementation (Model → OPC UA)

Uses `ConnectorReferenceCounter<List<MonitoredItem>>` for resource management:

```csharp
async Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
{
    // Check ref count - only create resources on first reference
    var isFirst = _refCounter.IncrementAndCheckFirst(subject,
        () => CreateMonitoredItemsForSubject(subject),
        out var monitoredItems);

    if (isFirst)
    {
        // Add monitored items to subscription
        _subscription.AddItems(monitoredItems);

        // Call AddNodes on server if supported
        if (_configuration.EnableRemoteNodeManagement)
        {
            try { await _session.AddNodesAsync(...); }
            catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadServiceUnsupported)
            { _logger.LogWarning("AddNodes not supported by server"); }
        }

        // Recursively sync child structure (ref counter stops cycles)
        await _synchronizer.SyncSubjectAsync(subject);
    }
}

async Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
{
    // Check ref count - only cleanup on last reference
    var isLast = _refCounter.DecrementAndCheckLast(subject, out var monitoredItems);

    if (isLast && monitoredItems is not null)
    {
        _subscription.RemoveItems(monitoredItems);

        if (_configuration.EnableRemoteNodeManagement)
        {
            try { await _session.DeleteNodesAsync(...); }
            catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadServiceUnsupported)
            { _logger.LogWarning("DeleteNodes not supported by server"); }
        }
    }
}
```

### Server Implementation (Model → OPC UA)

Uses `ConnectorReferenceCounter<NodeState>` for resource management.

**Flat vs Container handling:** `CreateNodeForSubject` and `AddReferenceToParent` must check `GetCollectionStructure(property)`:
- **Flat:** Add child node directly to grandparent with BrowseName `PropertyName[index]`
- **Container:** Create/reuse container node, add child to container

```csharp
async Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
{
    await _structureLock.WaitAsync();
    try
    {
        // Check ref count - only create node on first reference
        var isFirst = _refCounter.IncrementAndCheckFirst(subject,
            () => CreateNodeForSubject(subject, property, index),
            out var node);

        if (isFirst)
        {
            // Recursively sync child structure (ref counter stops cycles)
            await _synchronizer.SyncSubjectAsync(subject);
        }

        // Always add reference from parent to node
        AddReferenceToParent(property, node, index);
    }
    finally
    {
        _structureLock.Release();
    }
}

async Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
{
    await _structureLock.WaitAsync();
    try
    {
        // Remove reference from parent
        RemoveReferenceFromParent(property, subject, index);

        // Check ref count - only delete node on last reference
        var isLast = _refCounter.DecrementAndCheckLast(subject, out var node);

        if (isLast && node is not null)
        {
            DeleteNode(SystemContext, node.NodeId);
        }
    }
    finally
    {
        _structureLock.Release();
    }
}
```

---

## OPC UA → Model Direction

### Core Method: `ProcessNodeChangeAsync`

Property-level, idempotent - works for both initial load and incremental changes:

```csharp
async Task ProcessNodeChangeAsync(
    RegisteredSubjectProperty property,
    NodeId parentNodeId,
    ISession session,
    CancellationToken ct)
{
    // Browse OPC UA to get current remote state
    var remoteNodes = await BrowseChildNodesAsync(parentNodeId, session, ct);

    if (property.IsSubjectReference)
    {
        // Single reference: expect 0 or 1 remote node
        var localSubject = property.Children.SingleOrDefault().Subject;
        var remoteNode = remoteNodes.FirstOrDefault();

        if (localSubject is not null && remoteNode is null)
        {
            // Remote removed → remove local
            await RemoveLocalSubjectAsync(property, localSubject);
        }
        else if (localSubject is null && remoteNode is not null)
        {
            // Remote added → create local
            await CreateLocalSubjectAsync(property, remoteNode, index: null);
        }
        else if (localSubject is not null && remoteNode is not null)
        {
            // Both exist → recurse to sync children
            await ProcessSubjectNodeChangesAsync(localSubject, remoteNode.NodeId, session, ct);
        }
    }
    else if (property.IsSubjectCollection)
    {
        // Get collection structure setting from attribute
        var collectionStructure = GetCollectionStructure(property); // Flat or Container

        IEnumerable<ReferenceDescription> collectionNodes;

        if (collectionStructure == CollectionNodeStructure.Flat)
        {
            // Flat: Children are direct children of parent, matched by pattern
            var pattern = new Regex($@"^{Regex.Escape(property.Name)}\[(\d+)\]$");
            collectionNodes = remoteNodes
                .Where(n => pattern.IsMatch(n.BrowseName.Name))
                .ToList();
        }
        else
        {
            // Container: Browse the container node for children
            var containerNode = remoteNodes.FirstOrDefault(n => n.BrowseName.Name == property.Name);
            if (containerNode is null)
            {
                // No container = null or empty collection
                if (property.Children.Any())
                {
                    foreach (var child in property.Children.ToList())
                        await RemoveLocalSubjectAsync(property, child.Subject, null);
                }
                return;
            }
            var containerNodeId = ExpandedNodeId.ToNodeId(containerNode.NodeId, session.NamespaceUris);
            collectionNodes = await BrowseChildNodesAsync(containerNodeId, session, ct);
        }

        // Match by NodeId (subject identity), not index
        var localChildren = property.Children.ToList();

        // Build NodeId → local subject mapping
        var localByNodeId = new Dictionary<NodeId, (int Index, SubjectPropertyChild Child)>();
        foreach (var (child, index) in localChildren.Select((c, i) => (c, i)))
        {
            if (_subjectToNodeId.TryGetValue(child.Subject, out var nodeId))
            {
                localByNodeId[nodeId] = (index, child);
            }
        }

        // Build remote NodeId set
        var remoteNodeIds = collectionNodes
            .Select(n => ExpandedNodeId.ToNodeId(n.NodeId, session.NamespaceUris))
            .ToHashSet();

        // Remove locals whose NodeId no longer exists remotely
        foreach (var (nodeId, (index, child)) in localByNodeId)
        {
            if (!remoteNodeIds.Contains(nodeId))
            {
                await RemoveLocalSubjectAsync(property, child.Subject, index);
            }
        }

        // Add/update for each remote node
        foreach (var node in collectionNodes)
        {
            var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, session.NamespaceUris);

            if (localByNodeId.TryGetValue(nodeId, out var local))
            {
                // Exists locally → recurse to sync children
                await ProcessSubjectNodeChangesAsync(local.Child.Subject, nodeId, session, ct);
            }
            else
            {
                // New remote → create local (append to collection)
                await CreateLocalSubjectAsync(property, node, index: null);
            }
        }
    }
    else if (property.IsSubjectDictionary)
    {
        // Dictionary: diff by BrowseName (key)
        var localByKey = property.Children.ToDictionary(c => c.Index!.ToString()!, c => c);
        var remoteByKey = remoteNodes.ToDictionary(n => n.BrowseName.Name, n => n);

        // Remove locals not in remote
        foreach (var key in localByKey.Keys.Except(remoteByKey.Keys))
        {
            await RemoveLocalSubjectAsync(property, localByKey[key].Subject, key);
        }

        // Add/update for each remote
        foreach (var (key, node) in remoteByKey)
        {
            if (localByKey.TryGetValue(key, out var local))
            {
                // Exists locally → recurse
                await ProcessSubjectNodeChangesAsync(local.Subject, node.NodeId, session, ct);
            }
            else
            {
                // New remote → create local
                await CreateLocalSubjectAsync(property, node, key);
            }
        }
    }
    else
    {
        // Value property: handled by MonitoredItems (existing logic)
    }
}

// Subject-level convenience (extension method)
async Task ProcessSubjectNodeChangesAsync(
    IInterceptorSubject subject,
    NodeId nodeId,
    ISession session,
    CancellationToken ct)
{
    var registered = subject.TryGetRegisteredSubject();
    if (registered is null) return;

    foreach (var property in registered.Properties)
    {
        var propertyNodeId = GetNodeIdForProperty(property, nodeId);
        await ProcessNodeChangeAsync(property, propertyNodeId, session, ct);
    }
}
```

### Helper Methods

```csharp
async Task CreateLocalSubjectAsync(RegisteredSubjectProperty property, ReferenceDescription node, object? index)
{
    // Reuse existing loader logic
    var subject = await _configuration.SubjectFactory.CreateSubjectAsync(property, node, _session, ct);

    // Store NodeId mapping for identity tracking (used for collection matching on resync)
    var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, _session.NamespaceUris);
    _subjectToNodeId[subject] = nodeId;

    // Add to local model with source tracking (prevents sync-back loop)
    using (SubjectChangeContext.WithSource(_opcUaSource))
    {
        if (property.IsSubjectReference)
            property.SetValue(subject);
        else if (property.IsSubjectCollection)
            AddToCollection(property, subject);  // Append (index not used - collections unordered)
        else if (property.IsSubjectDictionary)
            AddToDictionary(property, subject, index!);  // index = dictionary key
    }

    // Recurse to load children
    await ProcessSubjectNodeChangesAsync(subject, nodeId, _session, ct);
}

async Task RemoveLocalSubjectAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index = null)
{
    // Clean up NodeId mapping
    _subjectToNodeId.Remove(subject);

    using (SubjectChangeContext.WithSource(_opcUaSource))
    {
        if (property.IsSubjectReference)
            property.SetValue(null);
        else if (property.IsSubjectCollection)
            RemoveFromCollection(property, subject);  // Remove by subject reference
        else if (property.IsSubjectDictionary)
            RemoveFromDictionary(property, index!);   // Remove by key
    }
}
```

**Implementation note:** Helper methods like `AddToCollection`, `RemoveFromCollection`, `AddToDictionary`, `RemoveFromDictionary`, `FindPropertyForNodeId`, `FindSubjectForNodeId`, `GetNodeIdForProperty`, and `GetCollectionStructure` are implementation details to be defined during implementation. They handle the mechanics of modifying collections/dictionaries, mapping between OPC UA NodeIds and model properties, and reading attribute configuration.

**Required state:**
```csharp
// Subject → NodeId mapping for collection identity matching
private readonly Dictionary<IInterceptorSubject, NodeId> _subjectToNodeId = new();
```

### Client Triggers

**1. ModelChangeEvent (preferred, if server supports):**
```csharp
// Subscribe to GeneralModelChangeEvent
subscription.AddMonitoredItem(new MonitoredItem
{
    StartNodeId = ObjectIds.Server,
    AttributeId = Attributes.EventNotifier,
    // Filter for GeneralModelChangeEventType
});

// On event:
void OnModelChangeEvent(MonitoredItem item, MonitoredItemNotificationEventArgs e)
{
    foreach (var change in e.NotificationValue.GetChanges())
    {
        if (change.Verb.HasFlag(ModelChangeStructureVerbMask.NodeAdded) ||
            change.Verb.HasFlag(ModelChangeStructureVerbMask.NodeDeleted))
        {
            // Find affected property and sync
            var property = FindPropertyForNodeId(change.Affected);
            await ProcessNodeChangeAsync(property, parentNodeId, _session, ct);
        }
    }
}
```

**2. Periodic Browse (fallback):**
```csharp
// Timer-based full resync
async Task PeriodicResyncAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(_configuration.PeriodicResyncInterval, ct);
        await ProcessSubjectNodeChangesAsync(_rootSubject, _rootNodeId, _session, ct);
    }
}
```

**3. Reconnection:**

On disconnect/reconnect, perform full resync from server:

```csharp
async Task OnReconnectedAsync()
{
    // Clear ref counter - server is source of truth
    foreach (var items in _refCounter.Clear())
    {
        // Cleanup orphaned monitored items
    }

    // Clear NodeId mapping - will be rebuilt during resync
    _subjectToNodeId.Clear();

    // Full resync from server
    await ProcessSubjectNodeChangesAsync(_rootSubject, _rootNodeId, _session, ct);
}
```

**Rationale:**
- Server is authoritative for OPC UA → Model direction
- Local changes during disconnect are edge case
- Simple and reliable
- Existing subjects are reused where BrowseNames/NodeIds match
- NodeId mapping rebuilt during resync (subjects matched by dictionary key or recreated)

### Server Triggers

**AddNodes/DeleteNodes Service Handlers:**
```csharp
// Override in CustomNodeManager
public override void AddNodes(ISystemContext context, IList<AddNodesItem> nodesToAdd)
{
    foreach (var item in nodesToAdd)
    {
        // Find parent property
        var parentProperty = FindPropertyForNodeId(item.ParentNodeId);
        if (parentProperty is null)
        {
            // Not in our managed tree
            base.AddNodes(context, nodesToAdd);
            continue;
        }

        // Create local subject
        var subject = _subjectFactory.CreateSubjectForNodeClass(item.NodeClass, item.TypeDefinition);

        // Add to local model with source tracking
        using (SubjectChangeContext.WithSource(_externalClientSource))
        {
            AddSubjectToProperty(parentProperty, subject, item.BrowseName.Name);
        }

        // Create the OPC UA node (will be picked up by Model → OPC UA sync)
        // Or directly create here to avoid round-trip
    }
}

public override void DeleteNodes(ISystemContext context, IList<DeleteNodesItem> nodesToDelete)
{
    foreach (var item in nodesToDelete)
    {
        var property = FindPropertyForNodeId(item.NodeId);
        if (property is null) continue;

        var subject = FindSubjectForNodeId(item.NodeId);
        if (subject is null) continue;

        using (SubjectChangeContext.WithSource(_externalClientSource))
        {
            RemoveSubjectFromProperty(property, subject);
        }
    }
}
```

---

## Initial Load = Sync with Empty State

The sync logic is **idempotent**. Initial load is just syncing when local state is empty:

### Model → OPC UA (Server/Client startup)
```csharp
// Initial: local model exists, OPC UA is empty
// Use SubjectGraphSynchronizer to sync entire graph
// Result: creates all OPC UA nodes for current model state

await _synchronizer.SyncSubjectAsync(rootSubject);  // Recursively syncs all subjects
```

### OPC UA → Model (Client connecting to server)
```csharp
// Initial: OPC UA has nodes, local model is empty
// ProcessNodeChangeAsync sees: remote = OPC UA nodes, local = empty
// Result: creates all local subjects from OPC UA

await ProcessSubjectNodeChangesAsync(rootSubject, rootNodeId, session, ct);
```

**Same methods, same logic** - just different starting states. Safe to call anytime for resync.

---

## Configuration

```csharp
public class OpcUaClientConfiguration
{
    // Existing...

    // New sync options
    public bool EnableLiveSync { get; set; } = false;
    public bool EnableRemoteNodeManagement { get; set; } = false;  // AddNodes/DeleteNodes
    public bool EnableModelChangeEvents { get; set; } = false;
    public bool EnablePeriodicResync { get; set; } = false;
    public TimeSpan PeriodicResyncInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public class OpcUaServerConfiguration
{
    // Existing...

    // New sync options
    public bool EnableLiveSync { get; set; } = false;
    public bool EnableExternalNodeManagement { get; set; } = false;  // Accept AddNodes/DeleteNodes
}
```

---

## Processing Architecture

### Separate Queues for Value vs Structure

Structural changes need `_structureLock` and can be slow. Value changes are fast. Separate them:

```csharp
// In change handler (existing ChangeQueueProcessor)
async Task ProcessChangeAsync(SubjectPropertyChange change, RegisteredSubjectProperty property)
{
    if (change.Source == _opcUaSource)
        return;  // Loop prevention

    if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
    {
        // Queue for structural processing (don't block value changes)
        _structuralChangeQueue.Enqueue(change);
    }
    else
    {
        // Value change - process inline (fast)
        await UpdateValueAsync(change);
    }
}

// Separate worker for structural changes
async Task ProcessStructuralChangesAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var change = await _structuralChangeQueue.DequeueAsync(ct);

        await _structureLock.WaitAsync(ct);
        try
        {
            await ProcessStructuralChangeAsync(change);
        }
        finally
        {
            _structureLock.Release();
        }
    }
}
```

### Value Changes During Structure Creation

Race condition: value change arrives before structure is created.

**Solution:** Skip value changes if node not ready. Structure creation reads current values anyway.

```csharp
async Task UpdateValueAsync(SubjectPropertyChange change)
{
    if (!change.Property.TryGetPropertyData(OpcVariableKey, out var node))
    {
        // Structure not created yet - skip
        // When structure is created, it reads current value from model
        _logger.LogDebug("Skipping value change for {Property} - node not ready", change.Property.Name);
        return;
    }

    // Node exists - update value
    UpdateNodeValue(node, change.GetNewValue<object?>());
}
```

**Why no data is lost:**
1. Value change updates local model (already happened before we process)
2. Value handler skips (node not ready)
3. Structure worker creates node, reads current value from model
4. OPC UA gets correct value via structure creation

---

## Eventual Consistency Model

All 4 cases are **eventually consistent**:

### Model → OPC UA (Server)
| Event | Processing | Consistency |
|-------|------------|-------------|
| Add subject | Structural queue → create node (reads current values) | ✓ Immediate |
| Remove subject | Structural queue → remove node | ✓ Immediate |
| Value change | Value handler → update node (skip if not ready) | ✓ Eventually |

### Model → OPC UA (Client)
| Event | Processing | Consistency |
|-------|------------|-------------|
| Add subject | Structural queue → AddNodes + MonitoredItems | ✓ Immediate |
| Remove subject | Structural queue → DeleteNodes + remove items | ✓ Immediate |
| Value change | Value handler → write to server (skip if not ready) | ✓ Eventually |

### OPC UA → Model (Client)
| Event | Processing | Consistency |
|-------|------------|-------------|
| Remote add node | ModelChangeEvent/browse → create local subject | ✓ Immediate |
| Remote remove node | ModelChangeEvent/browse → remove local subject | ✓ Immediate |
| Remote value change | MonitoredItem callback → update local property | ✓ Immediate |

### OPC UA → Model (Server)
| Event | Processing | Consistency |
|-------|------------|-------------|
| External AddNodes | Handler → create local subject | ✓ Immediate |
| External DeleteNodes | Handler → remove local subject | ✓ Immediate |
| External value write | StateChanged → update local property | ✓ Immediate |

### Consistency Guarantees
- **Structural changes:** Serialized via `_structureLock`, no races
- **Value changes:** Skipped if structure not ready, structure reads current values
- **Source tracking:** Prevents infinite loops in all directions
- **Fallback:** Periodic resync ensures recovery from missed events

### Error Handling Strategy

Structural changes use retry-once-then-continue pattern:

```csharp
foreach (var op in operations)
{
    try
    {
        await ProcessOperationAsync(op);
    }
    catch (Exception ex)
    {
        // One retry for transient failures
        try
        {
            await Task.Delay(100, ct);
            await ProcessOperationAsync(op);
        }
        catch (Exception retryEx)
        {
            _logger.LogWarning(retryEx,
                "Failed to sync subject after retry, will recover on next resync");
            // Continue - periodic resync will fix it
        }
    }
}
```

**Rationale:**
- Partial progress better than no progress
- OPC UA operations are independent (no transactions)
- Periodic resync catches anything missed
- Structural changes are rare, failures even rarer

---

## OPC UA Reference Counting

### Problem with Lifecycle Events

Previously used `SubjectAttached`/`SubjectDetaching` for cleanup. This is wrong:

| Scenario | Lifecycle Event | Issue |
|----------|-----------------|-------|
| Subject in multiple OPC UA properties | `Detaching` only when ALL refs gone | Too late |
| Subject moved between properties | No detach (ref count >0) | Never cleaned up |
| Subject removed from OPC UA but kept elsewhere | No detach | OPC UA resources never freed |

### Solution: Connector-Scoped Reference Counting

Track OPC UA-specific reference counts using `ConnectorReferenceCounter<TData>` (see full implementation in "Follow-Up: Apply Pattern to Other Connectors" section).

Key methods:
- `IncrementAndCheckFirst(subject, dataFactory, out data)` - returns true if first reference
- `DecrementAndCheckLast(subject, out data)` - returns true if last reference
- `Clear()` - returns all data for cleanup (used on reconnect)

### All 4 Cases Use Same Ref Count

**Server:**
```csharp
// Model → OPC UA (local change)
async Task OnLocalSubjectAddedAsync(property, subject, index)
{
    var isFirst = IncrementRefCount(subject);
    if (isFirst) CreateOpcUaNodes(subject);
    AddOpcUaReference(property, subject, index);
}

async Task OnLocalSubjectRemovedAsync(property, subject, index)
{
    RemoveOpcUaReference(property, subject, index);
    var isLast = DecrementRefCount(subject);
    if (isLast) RemoveOpcUaNodes(subject);
}

// OPC UA → Model (external client AddNodes/DeleteNodes)
async Task OnExternalNodeAddedAsync(property, nodeInfo, index)
{
    var subject = CreateLocalSubject(nodeInfo);
    IncrementRefCount(subject);  // Same ref count!
    // Nodes already created by external client

    using (SubjectChangeContext.WithSource(_externalSource))
        AddSubjectToProperty(property, subject, index);
}

async Task OnExternalNodeRemovedAsync(property, subject, index)
{
    using (SubjectChangeContext.WithSource(_externalSource))
        RemoveSubjectFromProperty(property, index);

    DecrementRefCount(subject);  // Same ref count!
    // Nodes already removed by external client
}
```

**Client:**
```csharp
// Model → OPC UA (local change)
async Task OnLocalSubjectAddedAsync(property, subject, index)
{
    var isFirst = IncrementRefCount(subject);
    if (isFirst)
    {
        CreateMonitoredItems(subject);
        TryCallAddNodes(subject);
    }
}

async Task OnLocalSubjectRemovedAsync(property, subject, index)
{
    var isLast = DecrementRefCount(subject);
    if (isLast)
    {
        RemoveMonitoredItems(subject);
        TryCallDeleteNodes(subject);
    }
}

// OPC UA → Model (remote server change)
async Task OnRemoteNodeAddedAsync(property, node, index)
{
    var subject = CreateLocalSubject(node);
    var isFirst = IncrementRefCount(subject);  // Same ref count!
    if (isFirst) CreateMonitoredItems(subject);

    using (SubjectChangeContext.WithSource(_opcUaSource))
        AddSubjectToProperty(property, subject, index);
}

async Task OnRemoteNodeRemovedAsync(property, subject, index)
{
    using (SubjectChangeContext.WithSource(_opcUaSource))
        RemoveSubjectFromProperty(property, index);

    var isLast = DecrementRefCount(subject);  // Same ref count!
    if (isLast) RemoveMonitoredItems(subject);
}
```

### Why Source Tracking Prevents Double-Counting

```
Remote node added
    │
    ▼
OnRemoteNodeAddedAsync
    ├── IncrementRefCount (count: 0 → 1)
    ├── CreateMonitoredItems
    └── AddSubjectToProperty (source = OPC UA)
            │
            ▼
        Property change fires (source = OPC UA)
            │
            ▼
        Model → OPC UA processor
            └── Skips (source == _opcUaSource)

Result: Ref count incremented exactly once
```

### Cleanup: Remove Lifecycle Event Subscriptions

With ref counting in structural change processor, remove:
- `_lifecycleInterceptor.SubjectDetaching += ...` from server
- `_lifecycleInterceptor.SubjectAttached += ...` from server/client
- Any lifecycle-based cache cleanup

---

## Loop Prevention

Use existing `change.Source` mechanism:

```csharp
// When OPC UA updates local model:
using (SubjectChangeContext.WithSource(_opcUaSource))
{
    property.SetValue(newCollection);
}

// When processing changes:
if (change.Source == _opcUaSource)
    return;  // Skip - came from OPC UA, don't sync back
```

---

## Coverage Matrix

### All Cases Verified

| Case | Direction | Method | Ref | Collection | Dictionary | Values |
|------|-----------|--------|-----|------------|------------|--------|
| Client | Model→OPC | `ProcessPropertyChangeAsync` | ✓ | ✓ | ✓ | ✓ |
| Client | OPC→Model | `ProcessNodeChangeAsync` | ✓ | ✓ | ✓ | ✓ MonitoredItems |
| Server | Model→OPC | `ProcessPropertyChangeAsync` | ✓ | ✓ | ✓ | ✓ |
| Server | OPC→Model | AddNodes/DeleteNodes handlers | ✓ | ✓ | ✓ | ✓ StateChanged |

### All Operations Verified

| Operation | How Handled | Ref Count |
|-----------|-------------|-----------|
| **Add** | OnSubjectAddedAsync | Increment (create resources if first) |
| **Remove** | OnSubjectRemovedAsync | Decrement (cleanup resources if last) |
| **Move** | Remove + Add (two separate changes) | Decrement then Increment (net zero) |
| **Replace** | Remove old, Add new (in order) | Old: decrement, New: increment |

### Move/Reorder Handling

`CollectionDiffBuilder` detects moves via subject identity (reference equality), not index:

```csharp
_diffBuilder.GetCollectionChanges(oldCollection, newCollection,
    out var operations,      // Remove/Insert ops
    out var newItems,        // Truly new items
    out var reorderedItems); // Items that moved position (same identity)
```

**Reorder within same collection:** No-op for OPC UA.
- Whether Flat or Container, OPC UA has no ordering concept
- `reorderedItems` is ignored - order is a .NET concept only
- No OPC UA operations needed for pure reorders

**Move between different properties:**
Two separate property changes, but ref count stays correct:
```
PropertyA: [subject] → []  → DecrementRefCount: 2 → 1 (no cleanup, still referenced)
PropertyB: [] → [subject]  → IncrementRefCount: 1 → 2 (no create, already exists)
```

If subject only in one place: Remove first (1→0, cleanup), Add second (0→1, recreate).
This is unavoidable for cross-property moves but rare.

### Collection/Dictionary Identity Matching

**Collections:** Match by subject reference (identity), not index position.
- `CollectionDiffBuilder` uses subject as dictionary key
- Same subject at different index = Move, not Remove+Add
- Different subject at same index = Remove old + Add new

**Dictionaries:** Match by dictionary key AND subject identity.
- Same key, same subject = no change
- Same key, different subject = Remove old + Add new (value changed)
- Different key = Remove/Add as appropriate

### Replace Operation Detail

Subject reference: old=SubjectA, new=SubjectB
```csharp
// ProcessPropertyChangeAsync handles this:
if (oldSubject is not null && !ReferenceEquals(oldSubject, newSubject))
    await OnSubjectRemovedAsync(property, oldSubject, index: null);  // First
if (newSubject is not null && !ReferenceEquals(oldSubject, newSubject))
    await OnSubjectAddedAsync(property, newSubject, index: null);    // Second
```

Order: Remove old first, then Add new. Ensures no brief state where both exist.

---

## Resolved Design Decisions

All open questions have been resolved through design review:

| Decision | Resolution | Rationale |
|----------|------------|-----------|
| **Collection ordering** | N/A - unordered in OPC UA | Collections map to OPC UA folders with child references; order is a .NET concept that doesn't translate |
| **Dictionary ordering** | BrowseName = key | Per OPC UA Machinery companion spec pattern (e.g., Machines folder) |
| **Reorder handling** | No-op for OPC UA | `reorderedItems` from diff builder ignored; order changes are local-only |
| **Initial value loading** | Eventual consistency via subscriptions | OPC UA subscriptions send initial values automatically; tiny window is acceptable |
| **Circular references** | Handled by ref counter | `IncrementAndCheckFirst` returns false for already-tracked subjects, stopping recursion |
| **Type mismatch** | SubjectFactory returns null → skip with warning | Rely on existing `SubjectFactory` + `TypeResolver` pattern |
| **Reconnection** | Full resync from server | Server is source of truth; simple and reliable |
| **Structural failures** | Retry once, log, continue | Eventual consistency; periodic resync recovers missed items |
| **Collection AddNodes index** | Append to end | Collections are unordered; index is just insertion order |
| **Concurrent modifications** | Last-write-wins | Structural changes are rare; not worth complex conflict resolution |
| **Dynamic types** | Shared `ShouldAddDynamic*` + `TypeResolver` | Same pattern for both client and server sides |
| **Consistency model** | Eventual consistency | OPC UA has no transactions; strict consistency not feasible |
| **Collection identity (OPC→Model)** | NodeId mapping | Index-based matching unreliable; NodeId is stable across browse calls |
| **Dictionary identity (OPC→Model)** | BrowseName = key | Dictionary key is the natural identifier |
| **Null vs empty collection** | Null = no folder; empty = folder with 0 children | Distinguishes "not set" from "empty set" |
| **Shared subject BrowseName** | First-reference-wins | Subject gets one node; first property to sync determines BrowseName |
| **Dictionary key types** | `ToString()` conversion | Keys become BrowseName strings; non-string keys must have consistent formatting |
| **Collections for OPC UA** | Supported with Flat/Container option | Flat is default (cleaner paths), Container for compatibility |
| **Collection node structure** | `CollectionNodeStructure.Flat` default | No intermediate node; children use `PropertyName[index]` pattern |
| **Dictionary node structure** | Always Container | Keys could conflict with property names; no Flat option |

---

## Implementation Phases

### Existing Code Reuse Analysis

**Server (`CustomNodeManager.cs`):**
- `CreateChildObject` already has reference-like pattern (checks `_subjects` dictionary)
- `CreateVariableNode`, `CreateArrayObjectNode`, `CreateDictionaryObjectNode` - reusable
- `RemoveSubjectNodes` - needs ref count check before deletion
- `_structureLock` pattern - reusable

**Client (`OpcUaSubjectLoader.cs`):**
- Property iteration in `LoadSubjectAsync` - reusable pattern
- `LoadSubjectReferenceAsync`, `LoadSubjectCollectionAsync`, `LoadSubjectDictionaryAsync` - refactorable
- `MonitorValueNode` - reusable
- `BrowseNodeAsync` - reusable

**Refactoring approach:**
- Add `ConnectorReferenceCounter<TData>` to Connectors library
- Server: Replace `_subjects` Dictionary with ref counter usage
- Client: Make `loadedSubjects` HashSet persistent via ref counter
- Both: Replace lifecycle event subscriptions with ref counting

### Phase 1: Add Connectors Library Abstractions
- `ConnectorReferenceCounter<TData>` - thread-safe ref counter with associated data
- `StructuralChangeProcessor` - property type branching base class
- `SubjectGraphSynchronizer<TData>` - recursive sync with ref counting
- Unit tests for all new classes

### Phase 2: Refactor Server for Reference Counting
- Replace `_subjects` Dictionary with `ConnectorReferenceCounter<NodeState>`
- Update `CreateChildObject` to use `IncrementAndCheckFirst`
- Update `RemoveSubjectNodes` to use `DecrementAndCheckLast`
- Remove lifecycle event subscriptions

### Phase 3: Add Server Model → OPC UA Incremental Sync
- Add `ProcessPropertyChangeAsync` to change handler
- Wire up to `ChangeQueueProcessor`
- Branch on property type, call existing creation/removal methods

### Phase 4: Refactor Client for Reference Counting
- Add `ConnectorReferenceCounter<List<MonitoredItem>>`
- Refactor `OpcUaSubjectLoader` to use persistent tracking
- Update monitored item cleanup

### Phase 5: Add Client Model → OPC UA Incremental Sync
- Add `ProcessPropertyChangeAsync` for structural changes
- Manage MonitoredItems dynamically
- Optional AddNodes/DeleteNodes support

### Phase 6: Add Client OPC UA → Model Incremental Sync
- Optional ModelChangeEvent subscription
- Optional periodic resync fallback
- Reuse refactored loader for incremental updates

### Phase 7: Add Server OPC UA → Model Sync (Optional)
- Implement AddNodes/DeleteNodes handlers
- Collection AddNodes: append to end (unordered)
- Dictionary AddNodes: BrowseName as key
- Use `ShouldAddDynamicSubject` + `TypeResolver` pattern

---

## Follow-Up: Apply Pattern to Other Connectors

The connector-scoped reference counting pattern should be applied to other bidirectional connectors:

### MQTT Connector
- **Same problem:** Subscriptions need cleanup when subjects removed from MQTT-synced graph
- **Same solution:** MQTT-specific ref count, cleanup subscriptions when count reaches 0
- **Triggers:** Topic subscription/unsubscription instead of OPC UA nodes

### Future Connectors (GraphQL, WebSocket, etc.)
- Each connector tracks its own ref counts
- Cleanup happens when subject leaves that connector's scope
- Independent of context lifecycle events

### New Abstraction: `ConnectorReferenceCounter<TData>` (Connectors Library)

A reusable reference counter for connector-scoped subject tracking with associated data:

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Tracks connector-scoped reference counts for subjects with associated data.
/// Key is always IInterceptorSubject, TData is connector-specific (e.g., NodeState, MonitoredItems).
/// </summary>
public class ConnectorReferenceCounter<TData>
{
    private readonly Dictionary<IInterceptorSubject, (int Count, TData Data)> _entries = new();
    private readonly object _lock = new();

    /// <summary>
    /// Increments reference count. Returns true if this is the first reference.
    /// For first reference, dataFactory is called to create associated data.
    /// </summary>
    public bool IncrementAndCheckFirst(IInterceptorSubject subject, Func<TData> dataFactory, out TData data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                _entries[subject] = (entry.Count + 1, entry.Data);
                data = entry.Data;
                return false;
            }

            data = dataFactory();
            _entries[subject] = (1, data);
            return true;
        }
    }

    /// <summary>
    /// Decrements reference count. Returns true if this was the last reference.
    /// On last reference, data is returned for cleanup.
    /// </summary>
    public bool DecrementAndCheckLast(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(subject, out var entry))
            {
                data = default;
                return false;
            }

            if (entry.Count == 1)
            {
                _entries.Remove(subject);
                data = entry.Data;
                return true;
            }

            _entries[subject] = (entry.Count - 1, entry.Data);
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Gets data for subject if tracked, null otherwise.
    /// </summary>
    public bool TryGetData(IInterceptorSubject subject, out TData? data)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(subject, out var entry))
            {
                data = entry.Data;
                return true;
            }
            data = default;
            return false;
        }
    }

    /// <summary>
    /// Clears all entries, returns all data for cleanup.
    /// </summary>
    public IEnumerable<TData> Clear()
    {
        lock (_lock)
        {
            var data = _entries.Values.Select(e => e.Data).ToList();
            _entries.Clear();
            return data;
        }
    }
}
```

**Usage in OPC UA:**

```csharp
// Server - stores NodeState
private readonly ConnectorReferenceCounter<NodeState> _refCounter = new();

async Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index)
{
    var isFirst = _refCounter.IncrementAndCheckFirst(subject,
        () => CreateNodeForSubject(subject),
        out var node);

    if (isFirst)
    {
        // Node just created, recurse into children
        await SyncSubjectChildrenAsync(subject);
    }

    // Always add reference from parent
    AddReferenceToParent(property, subject, index);
}

// Client - stores list of MonitoredItems
private readonly ConnectorReferenceCounter<List<MonitoredItem>> _refCounter = new();

var isFirst = _refCounter.IncrementAndCheckFirst(subject,
    () => CreateMonitoredItemsForSubject(subject),
    out var items);
```

### Additional Abstractions for Connectors Library

**2. `StructuralChangeProcessor` - Property type branching base class:**

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base class for processing structural property changes (add/remove subjects).
/// Handles branching on property type and collection diffing.
/// </summary>
public abstract class StructuralChangeProcessor
{
    private readonly CollectionDiffBuilder _diffBuilder = new();

    /// <summary>
    /// Source to ignore (prevents sync loops).
    /// </summary>
    protected abstract object? IgnoreSource { get; }

    /// <summary>
    /// Process a property change, branching on property type.
    /// </summary>
    public async Task ProcessPropertyChangeAsync(SubjectPropertyChange change, RegisteredSubjectProperty property)
    {
        if (change.Source == IgnoreSource)
            return;

        if (property.IsSubjectReference)
        {
            var oldSubject = change.GetOldValue<IInterceptorSubject?>();
            var newSubject = change.GetNewValue<IInterceptorSubject?>();

            if (oldSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectRemovedAsync(property, oldSubject, index: null);
            if (newSubject is not null && !ReferenceEquals(oldSubject, newSubject))
                await OnSubjectAddedAsync(property, newSubject, index: null);
        }
        else if (property.IsSubjectCollection)
        {
            var oldCollection = change.GetOldValue<IReadOnlyList<IInterceptorSubject>?>() ?? [];
            var newCollection = change.GetNewValue<IReadOnlyList<IInterceptorSubject>?>() ?? [];

            _diffBuilder.GetCollectionChanges(oldCollection, newCollection,
                out var operations, out var newItems, out _);

            // Process removes (descending order)
            foreach (var op in operations ?? [])
            {
                if (op.Action == SubjectCollectionOperationType.Remove)
                    await OnSubjectRemovedAsync(property, oldCollection[(int)op.Index], op.Index);
            }

            // Process adds
            foreach (var (index, subject) in newItems ?? [])
                await OnSubjectAddedAsync(property, subject, index);

            // Reorders ignored - order is connector-specific (OPC UA: no-op)
        }
        else if (property.IsSubjectDictionary)
        {
            var oldDict = change.GetOldValue<IDictionary?>();
            var newDict = change.GetNewValue<IDictionary?>() ?? new Dictionary<object, object>();

            _diffBuilder.GetDictionaryChanges(oldDict, newDict,
                out _, out var newItems, out var removedKeys);

            var oldChildren = property.Children.ToDictionary(c => c.Index!, c => c.Subject);
            foreach (var key in removedKeys ?? [])
            {
                if (oldChildren.TryGetValue(key, out var subject))
                    await OnSubjectRemovedAsync(property, subject, key);
            }

            foreach (var (key, subject) in newItems ?? [])
                await OnSubjectAddedAsync(property, subject, key);
        }
        else
        {
            await OnValueChangedAsync(change);
        }
    }

    protected abstract Task OnSubjectAddedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);
    protected abstract Task OnSubjectRemovedAsync(RegisteredSubjectProperty property, IInterceptorSubject subject, object? index);
    protected abstract Task OnValueChangedAsync(SubjectPropertyChange change);
}
```

**3. `SubjectGraphSynchronizer<TData>` - Recursive sync with ref counting:**

```csharp
namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base class for synchronizing subject graphs with reference counting.
/// Handles recursion with cycle detection via ref counter.
/// </summary>
public abstract class SubjectGraphSynchronizer<TData>
{
    protected ConnectorReferenceCounter<TData> RefCounter { get; } = new();

    /// <summary>
    /// Sync a subject and its children. Stops recursion for already-synced subjects.
    /// </summary>
    public async Task SyncSubjectAsync(IInterceptorSubject subject)
    {
        var isFirst = RefCounter.IncrementAndCheckFirst(subject, () => CreateDataForSubject(subject), out var data);

        if (!isFirst)
            return; // Already synced - stops recursion (handles cycles)

        await OnSubjectFirstSyncAsync(subject, data);

        // Recurse into structural children
        var registered = subject.TryGetRegisteredSubject();
        if (registered is null) return;

        foreach (var property in registered.Properties)
        {
            if (property.IsSubjectReference || property.IsSubjectCollection || property.IsSubjectDictionary)
            {
                foreach (var child in property.Children)
                {
                    if (child.Subject is not null)
                        await SyncSubjectAsync(child.Subject);
                }
            }
        }
    }

    /// <summary>
    /// Remove a subject from sync. Cleans up if last reference.
    /// </summary>
    public async Task UnsyncSubjectAsync(IInterceptorSubject subject)
    {
        var isLast = RefCounter.DecrementAndCheckLast(subject, out var data);

        if (isLast && data is not null)
            await OnSubjectLastUnsyncAsync(subject, data);
    }

    /// <summary>
    /// Create connector-specific data for a subject (called on first sync).
    /// </summary>
    protected abstract TData CreateDataForSubject(IInterceptorSubject subject);

    /// <summary>
    /// Called when subject is first synced (ref count 0 → 1).
    /// </summary>
    protected abstract Task OnSubjectFirstSyncAsync(IInterceptorSubject subject, TData data);

    /// <summary>
    /// Called when subject is last unsynced (ref count 1 → 0).
    /// </summary>
    protected abstract Task OnSubjectLastUnsyncAsync(IInterceptorSubject subject, TData data);
}
```

**Usage in OPC UA Server:**

```csharp
public class OpcUaServerSynchronizer : SubjectGraphSynchronizer<NodeState>
{
    protected override NodeState CreateDataForSubject(IInterceptorSubject subject)
        => _nodeManager.CreateNodeForSubject(subject);

    protected override Task OnSubjectFirstSyncAsync(IInterceptorSubject subject, NodeState node)
    {
        // Node already created in CreateDataForSubject
        return Task.CompletedTask;
    }

    protected override Task OnSubjectLastUnsyncAsync(IInterceptorSubject subject, NodeState node)
    {
        _nodeManager.DeleteNode(node.NodeId);
        return Task.CompletedTask;
    }
}
```

**Usage in OPC UA Client:**

```csharp
public class OpcUaClientSynchronizer : SubjectGraphSynchronizer<List<MonitoredItem>>
{
    protected override List<MonitoredItem> CreateDataForSubject(IInterceptorSubject subject)
        => CreateMonitoredItemsForSubject(subject);

    protected override Task OnSubjectFirstSyncAsync(IInterceptorSubject subject, List<MonitoredItem> items)
    {
        _subscription.AddItems(items);
        return Task.CompletedTask;
    }

    protected override Task OnSubjectLastUnsyncAsync(IInterceptorSubject subject, List<MonitoredItem> items)
    {
        _subscription.RemoveItems(items);
        return Task.CompletedTask;
    }
}
```

### Future Abstractions (After Implementation)

**Strategy:** Implement OPC UA first, validate these abstractions work, then consider:

- `GraphChangeQueue` - Channel-based queue for structural changes (if needed)

---

## Documentation Updates

### Updates to opcua-mapping.md

Add documentation for collection and dictionary node structure:

1. **Collection Node Structure**
   - `CollectionNodeStructure.Flat` (default): Children directly on parent with `PropertyName[index]` BrowseName
   - `CollectionNodeStructure.Container`: Intermediate container node (FolderType by default)
   - Configuration via `[OpcUaReference(CollectionStructure = ...)]`
   - Container TypeDefinition via `[OpcUaNode(TypeDefinition = "...")]`

2. **Dictionary Node Structure**
   - Always uses container node (no Flat option)
   - Keys become BrowseNames of children
   - Keys could conflict with property names, hence container required

3. **Examples**
   ```csharp
   // Collection - flat (default)
   public partial IList<Machine> Machines { get; set; }
   // Results in: Parent/Machines[0], Parent/Machines[1]

   // Collection - container
   [OpcUaReference(CollectionStructure = CollectionNodeStructure.Container)]
   public partial IList<Machine> Machines { get; set; }
   // Results in: Parent/Machines/Machines[0], Parent/Machines/Machines[1]

   // Dictionary - always container
   public partial IDictionary<string, Machine> Machines { get; set; }
   // Results in: Parent/Machines/Machine1, Parent/Machines/Machine2
   ```

### Updates to opcua.md

After implementation, add a new "Structural Synchronization" section to `docs/connectors/opcua.md` covering:

### Structural Synchronization

**Topics to document:**

1. **Live Sync Overview**
   - Bidirectional structural sync (add/remove subjects at runtime)
   - Configuration options: `EnableLiveSync`, `EnableModelChangeEvents`, `EnablePeriodicResync`

2. **Collections vs Dictionaries**
   - Dictionaries always use container nodes (keys could conflict with properties)
   - Collections default to flat structure (`Parent/Items[0]`) - cleaner, PLC-style
   - Collections can use container structure for compatibility (`[OpcUaReference(CollectionStructure = Container)]`)
   - Configure container TypeDefinition via `[OpcUaNode]` attribute

3. **Null vs Empty Semantics**
   - `null` collection/dictionary = no OPC UA folder node
   - Empty `[]` or `{}` = folder node with 0 children
   - On load: folder with 0 children → empty collection (not null)

4. **BrowseName Handling**
   - Dictionary items: BrowseName = dictionary key
   - Collection items: BrowseName = `PropertyName[index]`
   - Shared subjects: first-reference-wins for BrowseName
   - Dictionary keys converted via `ToString()`

5. **Eventual Consistency**
   - Structural changes are eventually consistent
   - Value changes may be skipped if structure not ready (no data loss - structure creation reads current values)
   - Periodic resync recovers from missed events
   - Error handling: retry once, log, continue

6. **Consistency Windows**
   - Brief window between structural change and OPC UA node creation
   - Brief window between value change and node update
   - Reconnection triggers full resync (server is source of truth)

7. **Subject Identity**
   - Collections: matched by NodeId mapping (not index)
   - Dictionaries: matched by BrowseName (dictionary key)
   - Single references: trivial match (only one child)

---

## References

- [OPC UA ModelChangeEvents Spec](https://reference.opcfoundation.org/Core/Part3/v104/docs/9.32)
- Existing `CollectionDiffBuilder` in Connectors library
- Existing `OpcUaSubjectLoader` for client-side loading
- Existing `CustomNodeManager` for server-side node management
