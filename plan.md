# Cache Invalidation via Lifecycle Events

## Status Summary

### ALL IMPLEMENTATION COMPLETE ✓

**Core Lifecycle Events:**
- [x] Step 1: Add events to LifecycleInterceptor (SubjectAttached/SubjectDetached, fire inside lock)
- [x] Step 2: ~~Make ReferenceCountKey internal~~ → Changed to private (implementation detail)
- [x] Step 3: Expose ReferenceCount on RegisteredSubject (via GetReferenceCount extension method)
- [x] Step 4: Add TryGetLifecycleInterceptor extension method

**MQTT Integration:**
- [x] Step 5: Update MqttSubjectClientSource with lifecycle events
- [x] Step 6: Update MqttSubjectServerBackgroundService with lifecycle events
- [x] Remove TODO(memory) comments from MQTT sources
- [x] Build and test - all tests pass

**Post-Review Fixes (PR #111):**
- [x] Fix race condition in cache methods (add-then-validate pattern)
- [x] Add IncrementReferenceCount/DecrementReferenceCount extension methods (avoid key duplication)
- [x] Remove stale cache entries when detecting detached subjects (memory cleanup)
- [x] Move events inside lock (same semantics as ILifecycleHandler, eliminates collector lists)
- [x] Optimize cache lookup: single TryGetValue on cache hit (hot path optimization)
- [x] Fix null safety: `(int)(count ?? 0)` instead of null-forgiving operator
- [x] Add defensive Math.Max(0, ...) to prevent negative reference counts

**OPC UA Integration:**
- [x] Step 8: OPC UA Server - cleanup nodes on subject detach (CustomNodeManager.RemoveSubjectNodes)
- [x] Step 9: OPC UA Client - cleanup subscriptions on subject detach (SubscriptionManager, PollingManager)
- [x] Client also clears property data (OpcUaNodeIdKey) for detached subjects

**Documentation:**
- [x] Updated docs/opcua.md with Lifecycle Management section
- [x] Created docs/mqtt.md with comprehensive documentation including Lifecycle Management
- [x] Updated docs/tracking.md with Lifecycle Events section
- [x] Updated README.md with link to MQTT documentation

### ALL TESTS PASS (263 total)
- [x] Step 7: Add unit tests for lifecycle events (15 new tests in LifecycleEventsTests.cs)

### OPC UA Cleanup on Detach

**Goal**: Minimal lifecycle integration - cleanup stale references when subjects are detached.
New subjects won't be synced dynamically, but detached subjects won't leak memory or hold stale references.

#### Why Cleanup is Needed

1. **OPC UA Server (`CustomNodeManager._subjects`)**:
   - Holds `ConcurrentDictionary<RegisteredSubject, NodeState>`
   - Detached subjects leave stale nodes that can't receive property changes
   - Memory leak: `RegisteredSubject` references kept alive

2. **OPC UA Client (`SubscriptionManager._monitoredItems`)**:
   - Holds `ConcurrentDictionary<uint, RegisteredSubjectProperty>`
   - Detached subjects leave orphan monitored items
   - Changes from server can't be applied (subject gone)
   - Memory leak: `RegisteredSubjectProperty` references kept alive

3. **OPC UA Client (`PollingManager._pollingItems`)**:
   - Same issue for fallback polling items

#### Design Decisions (from Agent Analysis)

**Thread Safety Considerations:**
1. `SubjectDetached` fires INSIDE `LifecycleInterceptor._attachedSubjects` lock
2. Handlers must be exception-free and fast (documented contract)
3. `ConcurrentDictionary.TryRemove` is safe for concurrent modification
4. OPC UA client's `OnFastDataChange` may run concurrently - but `TryGetValue` is atomic
5. Don't clean up during session reconnection (`IsReconnecting` check)

**Architecture Decisions:**
1. **Follow MQTT pattern exactly** for consistency
2. **Background service subscribes** to events (owns lifecycle)
3. **Manager methods are idempotent** (use `TryRemove`, safe to call multiple times)
4. **Error handling with logging** - don't throw from event handlers
5. **Unsubscribe in DisposeAsync** - prevent callbacks after disposal
6. **Server: Direct cleanup** - just dictionary operations (fast like MQTT)
7. **Client: Direct cleanup** - dictionary operations only, no server calls

**What NOT to do:**
- Do NOT call `subscription.Delete()` from detach handlers (requires active session)
- Do NOT acquire locks in event handlers (risk of deadlock)
- Do NOT throw exceptions (breaks lifecycle system)

#### Implementation Plan

**OpcUaSubjectServerBackgroundService** (server):
```csharp
private LifecycleInterceptor? _lifecycleInterceptor;

// In ExecuteAsync (after server starts):
_lifecycleInterceptor = _context.TryGetLifecycleInterceptor();
if (_lifecycleInterceptor is not null)
{
    _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
}

// In finally block or DisposeAsync:
if (_lifecycleInterceptor is not null)
{
    _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
}

private void OnSubjectDetached(SubjectLifecycleChange change)
{
    // Fast, exception-free cleanup (same pattern as MQTT)
    _nodeManager?.RemoveSubjectNodes(change.Subject);
}
```

**CustomNodeManager** (server):
```csharp
/// <summary>
/// Removes nodes for a detached subject. Idempotent - safe to call multiple times.
/// </summary>
public void RemoveSubjectNodes(IInterceptorSubject subject)
{
    // Find and remove all entries for this subject
    foreach (var kvp in _subjects)
    {
        if (kvp.Key.Subject == subject)
        {
            if (_subjects.TryRemove(kvp.Key, out _))
            {
                // Note: Node stays in address space until server restart
                // We just clean up local tracking to avoid memory leaks
            }
        }
    }
}
```

**OpcUaSubjectClientSource** (client):
```csharp
private LifecycleInterceptor? _lifecycleInterceptor;

// In StartListeningAsync:
_lifecycleInterceptor = _subject.Context.TryGetLifecycleInterceptor();
if (_lifecycleInterceptor is not null)
{
    _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
}

// In DisposeAsync:
if (_lifecycleInterceptor is not null)
{
    _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
}

private void OnSubjectDetached(SubjectLifecycleChange change)
{
    // Skip cleanup during reconnection (subscriptions being transferred)
    if (_sessionManager?.IsReconnecting == true)
        return;

    // Fast, exception-free cleanup (same pattern as MQTT)
    _sessionManager?.SubscriptionManager?.RemoveItemsForSubject(change.Subject);
    _sessionManager?.PollingManager?.RemoveItemsForSubject(change.Subject);
    CleanupPropertyDataForSubject(change.Subject);
}

private void CleanupPropertyDataForSubject(IInterceptorSubject subject)
{
    var toRemove = _propertiesWithOpcData
        .Where(p => p.Subject == subject)
        .ToList();

    foreach (var property in toRemove)
    {
        property.RemovePropertyData(OpcUaNodeIdKey);
        _propertiesWithOpcData.Remove(property);
    }
}
```

**SubscriptionManager** (client):
```csharp
/// <summary>
/// Removes monitored items for a detached subject. Idempotent.
/// Note: OPC UA subscription items remain on server until session ends.
/// This just cleans up local tracking to avoid memory leaks.
/// </summary>
public void RemoveItemsForSubject(IInterceptorSubject subject)
{
    foreach (var kvp in _monitoredItems)
    {
        if (kvp.Value.Reference.Subject == subject)
        {
            _monitoredItems.TryRemove(kvp.Key, out _);
        }
    }
}
```

**PollingManager** (client):
```csharp
/// <summary>
/// Removes polling items for a detached subject. Idempotent.
/// </summary>
public void RemoveItemsForSubject(IInterceptorSubject subject)
{
    foreach (var kvp in _pollingItems)
    {
        if (kvp.Value.Property.Reference.Subject == subject)
        {
            _pollingItems.TryRemove(kvp.Key, out _);
        }
    }
}
```

#### Effort Estimate

| Task | Effort |
|------|--------|
| Server: Add lifecycle subscription + cleanup | 0.5 days |
| Server: Add `RemoveSubjectNodes` to CustomNodeManager | 0.5 days |
| Client: Add lifecycle subscription + cleanup | 0.5 days |
| Client: Add `RemoveItemsForSubject` methods | 0.5 days |
| Testing | 0.5 days |
| **Total** | **~2.5 days** |

#### What This Does NOT Do

- Does NOT add new subjects dynamically (requires server browse + subscription creation)
- Does NOT update OPC UA address space when subjects attach
- New subjects after initialization won't be visible in OPC UA

This is intentional - full dynamic support would require significant additional work. This minimal approach just prevents memory leaks.

---

## Problem

The MQTT client and server maintain caches that map between `PropertyReference` and MQTT topics/paths:

**MqttSubjectClientSource:**
- `_topicToProperty: ConcurrentDictionary<string, PropertyReference?>`
- `_propertyToTopic: ConcurrentDictionary<PropertyReference, string?>`

**MqttSubjectServerBackgroundService:**
- `_propertyToTopic: ConcurrentDictionary<PropertyReference, string?>`
- `_pathToProperty: ConcurrentDictionary<string, PropertyReference?>`

When subjects are detached from the object graph, cache entries become stale but are not cleaned up until disposal. This leads to memory leaks in long-running services with dynamic object graphs.

## Solution

Add `SubjectAttached` and `SubjectDetached` events to `LifecycleInterceptor` that sources can subscribe to for cache invalidation.

## Design Decisions

### Events vs ILifecycleHandler

**Decision**: Use events (not `ILifecycleHandler`) for cache invalidation.

**Rationale**:
- `ILifecycleHandler` implementations are "static" - registered at startup via DI
- Sources are "dynamic" - created/disposed at runtime based on configuration
- Events are better suited for dynamic subscribers that come and go

### Events Fire Inside Lock

**Decision**: Events fire inside the lock, same as `ILifecycleHandler` methods.

**Rationale**:
- Consistent semantics with `ILifecycleHandler` - both invoked inside lock
- Strong ordering guarantees - no race between attach/detach events
- Simpler code - no collector lists, no delegate capture, no post-lock iteration
- Event handlers are expected to be fast (dictionary operations - microseconds)

**Contract**: Event handlers must be exception-free and fast (invoked inside lock).

### Consumer-Side Attachment Check (Add-Then-Validate Pattern)

**Problem**: Race condition where cache is populated during/after subject detachment.

**Initial Approach (FLAWED)**: Check BEFORE cache lookup.
- Race: Thread A checks ReferenceCount > 0, Thread B detaches subject, Thread A adds to cache
- Result: Stale entry in cache that won't be cleaned (OnSubjectDetached already ran)

**Solution**: "Add-then-validate" pattern with single lookup on cache hit.
- Fast path: Single `TryGetValue` call returns immediately for cached entries (no validation on hot path)
- Slow path: On cache miss, compute value, `TryAdd`, then validate ReferenceCount
- If validation fails, remove the entry we just added
- 100% memory leak free: entry is added first, so OnSubjectDetached can always find it

```csharp
private string? TryGetTopicForProperty(PropertyReference propertyReference, RegisteredSubjectProperty property)
{
    // Fast path: single lookup for cached entries
    if (_propertyToTopic.TryGetValue(propertyReference, out var cachedTopic))
    {
        return cachedTopic;
    }

    // Slow path: compute and add to cache
    var path = property.TryGetSourcePath(_configuration.PathProvider, _subject);
    var topic = path is null ? null : MqttHelper.BuildTopic(path, _configuration.TopicPrefix);

    // Add first, then validate (guarantees no memory leak)
    if (_propertyToTopic.TryAdd(propertyReference, topic))
    {
        var registeredSubject = propertyReference.Subject.TryGetRegisteredSubject();
        if (registeredSubject is null || registeredSubject.ReferenceCount <= 0)
        {
            _propertyToTopic.TryRemove(propertyReference, out _);
        }
    }

    return topic;
}
```

**Performance**: Single dictionary lookup on cache hit (hot path).
**Thread-Safety Guarantee**: Add-then-validate ensures entry is always visible to OnSubjectDetached.
**Memory Cleanup**: Stale entries are removed immediately when detected, supplementing the OnSubjectDetached handler.

### No Exception Handling in LifecycleInterceptor

The `LifecycleInterceptor` does NOT wrap handler/event invocations in try-catch:
- Exception handling has performance overhead
- Clear contract: `ILifecycleHandler` implementations and event handlers MUST be exception-free
- Handlers must handle their own exceptions internally if needed
- An unhandled exception in a handler is a bug that should surface immediately

## Files Modified

### Namotion.Interceptor.Tracking

**LifecycleInterceptor.cs:**
- Added `SubjectAttached` and `SubjectDetached` events
- Events fire inside lock (same semantics as `ILifecycleHandler`)
- Removed `ReferenceCountKey` constant (moved to extension methods)
- Uses `IncrementReferenceCount()`/`DecrementReferenceCount()` extension methods
- Simplified: no collector lists, no delegate capture, direct `?.Invoke()` inside lock

**LifecycleInterceptorExtensions.cs:**
- Added `TryGetLifecycleInterceptor(this IInterceptorSubjectContext)` extension
- Added `GetReferenceCount(this IInterceptorSubject)` extension
- Added `IncrementReferenceCount(this IInterceptorSubject)` internal extension (avoids key duplication)
- Added `DecrementReferenceCount(this IInterceptorSubject)` internal extension (avoids key duplication)
- Fixed null safety: `(int)(count ?? 0)` instead of `(int)count!`
- Added `Math.Max(0, ...)` in decrement to prevent negative reference counts

### Namotion.Interceptor.Registry

**RegisteredSubject.cs:**
- Added `ReferenceCount` property (delegates to `Subject.GetReferenceCount()`)

### Namotion.Interceptor.Mqtt

**MqttSubjectClientSource.cs:**
- Added `_lifecycleInterceptor` field
- Subscribe to `SubjectDetached` event in `StartListeningAsync`
- `TryGetTopicForProperty` uses add-then-validate pattern with single lookup on cache hit
- `TryGetPropertyForTopic` uses add-then-validate pattern with single lookup on cache hit
- Added `OnSubjectDetached` handler to clean up caches (best-effort cleanup)
- Unsubscribe in `DisposeAsync`
- Removed `// TODO(memory)` comment

**MqttSubjectServerBackgroundService.cs:**
- Same pattern as client source
- Added `_lifecycleInterceptor` field
- Subscribe/unsubscribe to lifecycle events
- Both cache methods use add-then-validate pattern with single lookup on cache hit
- Added `OnSubjectDetached` handler to clean up caches (best-effort cleanup)
- Removed `// TODO(memory)` comment

### Namotion.Interceptor.Tracking.Tests

**LifecycleEventsTests.cs (NEW):**
- 15 unit tests for lifecycle events and extension methods
- Tests for `SubjectAttached`/`SubjectDetached` events with correct `ReferenceCount`
- Tests for `TryGetLifecycleInterceptor()` extension method
- Tests for `GetReferenceCount()` extension method
- Tests for multiple references via properties and arrays
- Tests for root subject lifecycle (attach on context, detach on context removal)
- Edge case tests: same value assignment, replacement ordering
