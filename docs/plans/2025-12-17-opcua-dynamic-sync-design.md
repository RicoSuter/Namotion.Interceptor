# OPC UA Dynamic Address Space Synchronization

## Overview

**Problem:** Currently, both OPC UA client and server only sync structure once at startup. Changes after initialization (local attach/detach or remote address space changes) are not reflected.

**Goals:**
1. **Bidirectional live sync** - Local changes update OPC UA, remote changes update local model
2. **Unified sync logic** - Single code path for initial load and live updates
3. **Shared implementation** - Client and server share the sync coordinator via strategy pattern
4. **Configurable** - Use existing `ShouldAddDynamicProperty` for runtime decisions; optional periodic fallback

**Non-goals:**
- Changing the existing public API surface
- Supporting OPC UA method calls (only structure/values)

**Key Design Decisions:**

| Decision | Choice |
|----------|--------|
| Remote change detection | ModelChangeEvents with periodic fallback (configurable, off by default) |
| Unresolved object nodes | Create DynamicObject subjects |
| Sync trigger | Incremental on each change, not full resync |
| Shared code approach | Composition with strategy pattern (`IOpcUaSyncStrategy`) |
| Client node management | Support AddNodes/DeleteNodes if server supports it, log warning otherwise |

---

## Architecture

### Core Components

```
┌─────────────────────────────────────────────────────────────────┐
│                    OpcUaAddressSpaceSync                        │
│  (Shared coordinator - handles sync logic for both directions) │
├─────────────────────────────────────────────────────────────────┤
│  - Subscribes to LifecycleInterceptor (SubjectAttached/Detached)│
│  - Subscribes to ModelChangeEvents (remote changes)             │
│  - Optional periodic resync timer                               │
│  - Delegates actual operations to IOpcUaSyncStrategy            │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      IOpcUaSyncStrategy                         │
├─────────────────────────────────────────────────────────────────┤
│  + OnSubjectAttachedAsync(subject)     // Local → OPC UA        │
│  + OnSubjectDetachedAsync(subject)     // Local → OPC UA        │
│  + OnRemoteNodeAddedAsync(node)        // OPC UA → Local        │
│  + OnRemoteNodeRemovedAsync(nodeId)    // OPC UA → Local        │
│  + CreateMonitoredItemsAsync(subject)  // For value sync        │
│  + BrowseNodeAsync(nodeId)             // Browse remote/local   │
└─────────────────────────────────────────────────────────────────┘
           ▲                                       ▲
           │                                       │
┌──────────┴──────────┐             ┌──────────────┴──────────────┐
│ OpcUaClientStrategy │             │    OpcUaServerStrategy      │
├─────────────────────┤             ├─────────────────────────────┤
│ - Creates monitored │             │ - Creates OPC UA nodes      │
│   items/subscriptions│            │ - Fires ModelChangeEvents   │
│ - Connects to       │             │ - Updates CustomNodeManager │
│   external server   │             │                             │
│ - Calls AddNodes if │             │ - Handles AddNodes requests │
│   server supports   │             │   from external clients     │
└─────────────────────┘             └─────────────────────────────┘
```

### File Structure

```
src/Namotion.Interceptor.OpcUa/
├── Sync/
│   ├── IOpcUaSyncStrategy.cs
│   ├── OpcUaAddressSpaceSync.cs
│   ├── OpcUaSyncConfigurationBase.cs
│   └── ModelChangeEventSubscription.cs
├── Client/
│   ├── OpcUaClientSyncStrategy.cs      // New
│   └── ... (existing files)
├── Server/
│   ├── OpcUaServerSyncStrategy.cs      // New
│   ├── OpcUaNodeManagementHandler.cs   // New
│   └── ... (existing files)
```

---

## Configuration

### Shared Base Configuration

```csharp
public abstract class OpcUaConfigurationBase
{
    // Already shared between client/server
    public ISourcePathProvider PathProvider { get; set; }
    public OpcUaValueConverter ValueConverter { get; set; }
    public OpcUaTypeResolver TypeResolver { get; set; }
    public OpcUaSubjectFactory SubjectFactory { get; set; }
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperty { get; set; }

    // New sync options
    public bool EnableLiveSync { get; set; } = false;              // Master switch
    public bool EnableRemoteNodeManagement { get; set; } = false;  // AddNodes/DeleteNodes
    public bool EnablePeriodicResync { get; set; } = false;        // Fallback polling
    public TimeSpan PeriodicResyncInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public class OpcUaClientConfiguration : OpcUaConfigurationBase
{
    public string ServerUrl { get; set; }
    public string? RootName { get; set; }
    public int DefaultSamplingInterval { get; set; }
    // ... other client-specific settings
}

public class OpcUaServerConfiguration : OpcUaConfigurationBase
{
    public string? ApplicationName { get; set; }
    public string? NamespaceUri { get; set; }
    public bool CleanCertificateStore { get; set; }
    // ... other server-specific settings
}
```

---

## Data Flow

### Local Subject Attached → OPC UA

```
Person.Children.Add(new Child(...))
         │
         ▼
LifecycleInterceptor.SubjectAttached event
         │
         ▼
OpcUaAddressSpaceSync.OnLocalSubjectAttached()
         │
         ▼
IOpcUaSyncStrategy.OnSubjectAttachedAsync(child)
         │
         ├─── Client: Create MonitoredItems, call AddNodes if supported
         │
         └─── Server: Create OPC UA nodes, fire GeneralModelChangeEvent
```

### Local Subject Detached → OPC UA

```
Person.Children.Remove(child)
         │
         ▼
LifecycleInterceptor.SubjectDetached event
         │
         ▼
OpcUaAddressSpaceSync.OnLocalSubjectDetached()
         │
         ▼
IOpcUaSyncStrategy.OnSubjectDetachedAsync(child)
         │
         ├─── Client: Remove MonitoredItems, call DeleteNodes if supported
         │
         └─── Server: Remove nodes (tracking only*), fire GeneralModelChangeEvent
```

*OPC UA SDK limitation: nodes remain in address space until restart

### Remote Node Added → Local Subject

```
GeneralModelChangeEvent (Verb=NodeAdded)
         │  (or periodic browse detects new node)
         ▼
OpcUaAddressSpaceSync.OnRemoteNodeAdded()
         │
         ▼
ShouldAddDynamicProperty(node) → false? → skip
         │ true
         ▼
TypeResolver.TryGetTypeForNodeAsync()
         │
         ├─── Type found → Create typed subject via SubjectFactory
         │
         └─── No type → Create DynamicObject subject
         │
         ▼
Attach to parent (triggers value sync automatically)
```

### Remote Node Removed → Local Subject

```
GeneralModelChangeEvent (Verb=NodeDeleted)
         │  (or periodic browse detects missing node)
         ▼
OpcUaAddressSpaceSync.OnRemoteNodeRemoved()
         │
         ▼
Find local subject by NodeId mapping
         │
         ▼
Detach from parent collection/property
         │
         ▼
LifecycleInterceptor handles cleanup automatically
```

### Server Receives AddNodes from External Client

```
External OPC UA Client calls AddNodes service
         │
         ▼
CustomNodeManager.AddNode() override
         │
         ▼
OpcUaNodeManagementHandler.HandleAddNodeAsync()
         │
         ▼
Create local subject, attach to parent
         │
         ▼
Fire GeneralModelChangeEvent to notify other clients
```

### Client NodeManagement (Graceful Degradation)

```csharp
public async Task OnSubjectAttachedAsync(IInterceptorSubject subject, CancellationToken ct)
{
    // Always create monitored items for value sync
    await CreateMonitoredItemsAsync(subject, ct);

    // Try to create node on server (optional)
    if (_configuration.EnableRemoteNodeManagement)
    {
        try
        {
            var result = await _session.AddNodesAsync(..., ct);
            if (result.StatusCode == StatusCodes.BadNotSupported ||
                result.StatusCode == StatusCodes.BadServiceUnsupported)
            {
                _logger.LogWarning(
                    "Server does not support AddNodes service. " +
                    "Local subject '{SubjectType}' will sync values but not structure.",
                    subject.GetType().Name);
            }
        }
        catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadServiceUnsupported)
        {
            _logger.LogWarning("AddNodes service not supported by server.");
        }
    }
}
```

---

## Error Handling

### Connection Loss During Sync

| Scenario | Behavior |
|----------|----------|
| Local change while disconnected | Queued in existing write retry queue; structure change logged as warning |
| Reconnect after disconnect | Full resync triggered (same as initial connect) |
| ModelChangeEvent missed | Periodic resync catches up (if enabled), or next connect |

### Concurrent Changes

```csharp
// OpcUaAddressSpaceSync uses lock to serialize local/remote changes
private readonly SemaphoreSlim _syncLock = new(1, 1);

public async Task OnLocalSubjectAttachedAsync(SubjectLifecycleChange change, CancellationToken ct)
{
    await _syncLock.WaitAsync(ct);
    try
    {
        await _strategy.OnSubjectAttachedAsync(change.Subject, ct);
    }
    finally
    {
        _syncLock.Release();
    }
}
```

### Circular References

- Already handled by existing `LifecycleInterceptor` (tracks visited subjects)
- Sync reuses same pattern: `HashSet<IInterceptorSubject> _processedSubjects`

### Type Resolution Failures

```
Remote node added → TypeResolver returns null → ShouldAddDynamicProperty?
    │                                                    │
    ├── false → Log debug, skip node                     │
    │                                                    │
    └── true → Create DynamicObject subject ◄────────────┘
```

### NodeManagement Service Failures

| Service | Failure Behavior |
|---------|------------------|
| AddNodes not supported | Log warning, continue value sync only |
| DeleteNodes not supported | Log warning, local detach still works |
| AddNodes fails (other error) | Log error, skip node |

### Partial Sync Failures

- Individual node failures don't abort entire sync
- Each node synced independently with its own error handling
- Failed nodes logged, successful nodes applied

---

## Testing Strategy

### Unit Tests

**OpcUaAddressSpaceSync tests:**
```csharp
[Fact] public async Task OnSubjectAttached_CallsStrategyOnSubjectAttachedAsync()
[Fact] public async Task OnSubjectDetached_CallsStrategyOnSubjectDetachedAsync()
[Fact] public async Task OnModelChangeEvent_NodeAdded_CallsOnRemoteNodeAddedAsync()
[Fact] public async Task OnModelChangeEvent_NodeDeleted_CallsOnRemoteNodeRemovedAsync()
[Fact] public async Task ConcurrentChanges_SerializedBySemaphore()
```

**Strategy tests (with mocked OPC UA session):**
```csharp
[Fact] public async Task OnSubjectAttached_CreatesMonitoredItems()
[Fact] public async Task OnSubjectAttached_ServerSupportsAddNodes_CreatesRemoteNode()
[Fact] public async Task OnSubjectAttached_ServerNotSupportsAddNodes_LogsWarningAndContinues()
[Fact] public async Task OnRemoteNodeAdded_KnownType_CreatesTypedSubject()
[Fact] public async Task OnRemoteNodeAdded_UnknownType_CreatesDynamicSubject()
[Fact] public async Task OnRemoteNodeAdded_ShouldAddDynamicPropertyFalse_SkipsNode()
```

### Integration Tests

**Extend existing `OpcUaServerClientReadWriteTests.cs`:**
```csharp
[Fact] public async Task LocalAttach_SyncsToServerAddressSpace()
[Fact] public async Task LocalDetach_SyncsToServerAddressSpace()
[Fact] public async Task RemoteNodeAdded_SyncsToLocalModel()
[Fact] public async Task PeriodicResync_DetectsChanges_WhenModelChangeEventsUnsupported()
```

### Test Infrastructure

```csharp
// MockOpcUaSyncStrategy for unit testing coordinator
internal class MockOpcUaSyncStrategy : IOpcUaSyncStrategy { ... }

// Test helper for simulating ModelChangeEvents
internal static class ModelChangeEventTestHelper
{
    public static GeneralModelChangeEventState CreateNodeAddedEvent(NodeId nodeId) { ... }
    public static GeneralModelChangeEventState CreateNodeDeletedEvent(NodeId nodeId) { ... }
}
```

---

## Implementation Plan

### New Files

| File | Purpose | Est. Lines |
|------|---------|------------|
| `Sync/IOpcUaSyncStrategy.cs` | Strategy interface | ~30 |
| `Sync/OpcUaAddressSpaceSync.cs` | Shared sync coordinator | ~250 |
| `OpcUaConfigurationBase.cs` | Shared configuration base class | ~40 |
| `Sync/ModelChangeEventSubscription.cs` | Event subscription helper | ~80 |
| `Client/OpcUaClientSyncStrategy.cs` | Client implementation | ~200 |
| `Server/OpcUaServerSyncStrategy.cs` | Server implementation | ~150 |
| `Server/OpcUaNodeManagementHandler.cs` | AddNodes/DeleteNodes handling | ~120 |

**Total new code: ~870 lines**

### Modified Files

| File | Changes |
|------|---------|
| `Client/OpcUaSubjectClientSource.cs` | Integrate `OpcUaAddressSpaceSync`, subscribe to `SubjectAttached` |
| `Client/OpcUaSubjectLoader.cs` | Extract reusable logic to strategy |
| `Client/OpcUaClientConfiguration.cs` | Inherit from `OpcUaConfigurationBase` |
| `Server/OpcUaSubjectServerBackgroundService.cs` | Integrate `OpcUaAddressSpaceSync` |
| `Server/CustomNodeManager.cs` | Override NodeManagement methods, fire ModelChangeEvents |
| `Server/OpcUaServerConfiguration.cs` | Inherit from `OpcUaConfigurationBase` |

### Implementation Phases

**Phase 1: Core infrastructure**
- `IOpcUaSyncStrategy` interface
- `OpcUaConfigurationBase`
- `OpcUaAddressSpaceSync` (local change handling only)

**Phase 2: Client strategy**
- `OpcUaClientSyncStrategy`
- Extract logic from `OpcUaSubjectLoader`
- Integrate into `OpcUaSubjectClientSource`

**Phase 3: Server strategy**
- `OpcUaServerSyncStrategy`
- Extract logic from `CustomNodeManager`
- Fire `GeneralModelChangeEvent` on changes

**Phase 4: Remote change detection**
- `ModelChangeEventSubscription` (client subscribes to server events)
- Server: Override NodeManagement methods
- `OpcUaNodeManagementHandler`

**Phase 5: Periodic fallback**
- Optional periodic resync in `OpcUaAddressSpaceSync`
- Registry-based diff logic

### Breaking Changes

**None** - All changes are additive:
- New configuration options have sensible defaults (all disabled)
- Existing behavior preserved when sync features disabled
- Public API unchanged

---

## References

- [OPC UA ModelChangeEvents Spec](https://reference.opcfoundation.org/Core/Part3/v104/docs/9.32)
- [Triggering ModelChangeEvent - GitHub Issue #490](https://github.com/OPCFoundation/UA-.NETStandard/issues/490)
- [Event Subscription Discussion](https://github.com/OPCFoundation/UA-.NETStandard/discussions/2618)
